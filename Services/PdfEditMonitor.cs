using InspectionEditor.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace InspectionEditor.Services
{
    /// <summary>UI-independent, deterministic full-PDF save monitor. Process exit is not a save signal.</summary>
    public sealed class PdfEditMonitor
    {
        public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);
        private readonly InspectionFile _owner;
        private readonly string _filename;
        private readonly string _directory;
        private FileStream? _sessionLease;
        private JObject? _expectedAttachment;
        private byte[] _baseline;
        private byte[]? _observed;
        private DateTimeOffset _observedAt;
        private bool _failed;
        private bool _closed;
        public int? AttachmentIndex { get; private set; }
        public bool IsPendingAdd { get; private set; }
        public string Filename => _filename;
        internal bool BelongsTo(InspectionFile owner) => ReferenceEquals(_owner, owner);
        internal void RebaseAfterDeletion(int[] removed)
        {
            if (AttachmentIndex.HasValue) AttachmentIndex -= removed.Count(i => i < AttachmentIndex.Value);
        }
        internal void CompleteDeletion(FileStream lease)
        {
            // The successful INS delete and working-file retirement share one exclusive lease.
            // An unchanged leftover on a filesystem failure is safer than a recursive delete.
            try { PdfWorkingSession.DeleteLeasedFile(lease, WorkingPath); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            _closed = true;
            HasPendingChanges = false;
        }
        public string WorkingPath { get; }
        public ProcessStartInfo StartInfo => new() { FileName = WorkingPath, UseShellExecute = true, Arguments = "" };
        public string Status { get; private set; } = "Open in your PDF editor. Saved disk changes are captured automatically.";
        public bool HasPendingChanges { get; private set; }
        public bool OfficialPrepared { get; private set; }
        public bool HasLaunched { get; private set; }

        internal void AbandonFailedPreparation()
        {
            // Called only before a newly-created monitor is returned or shell-launched.
            // Preserve a partially prepared copy but release its lifetime ownership lock.
            if (HasLaunched) throw new IOException("Cannot abandon a launched PDF session.");
            try { Cleanup(); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            finally { _sessionLease?.Dispose(); _sessionLease = null; }
        }

        // Mark before shell dispatch: even a failed/indeterminate shell launch cannot prove
        // that an editor did not retain this path with unsaved in-memory changes.
        public void MarkLaunched() => HasLaunched = true;

        internal void PrepareOfficialForOpen()
        {
            if (_closed) throw new IOException("This PDF session is closed. Reopen the report.");
            if (OfficialPrepared) return;
            if (HasLaunched)
                throw new IOException("This walk PDF was already opened without autofill. Save and close it in the PDF editor, capture any changes using ATTACHMENTS > Retry Save, then close and reopen the report before opening the official walk document. RED cannot safely change a PDF that may have unsaved editor changes.");
            if (IsPendingAdd || HasPendingChanges || _observed != null || _failed)
                throw new IOException("Walk PDF preparation is blocked by pending changes. Use ATTACHMENTS > Retry Save before opening; the working PDF is retained.");
            if (AttachmentIndex.HasValue && !JToken.DeepEquals(
                PdfAttachmentService.Attachment(_owner, AttachmentIndex.Value), _expectedAttachment))
                throw new IOException("The walk attachment changed during editing. Close and reopen the report after resolving pending PDF changes.");

            // Never reopen/truncate by path between checking and writing. On Windows this
            // exclusive read/write lease also denies rename/delete and competing editor writes.
            // Only our owned working copy is touched; the INS remains exact until a later save.
            using var lease = new FileStream(WorkingPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (lease.Length > PdfAttachmentService.MaxPdfBytes)
                throw new IOException("Walk PDF exceeds the safety limit; preparation was blocked.");
            using var input = new MemoryStream();
            lease.CopyTo(input);
            byte[] bytes = input.ToArray();
            if (!bytes.SequenceEqual(_baseline))
            {
                HasPendingChanges = true;
                Status = "Walk PDF has uncaptured disk changes. Use ATTACHMENTS > Retry Save before opening.";
                throw new IOException(Status);
            }
            byte[] filled = OrientationPdfForm.Fill(bytes, _owner, blankOnly: true);
            PdfAttachmentService.Validate(filled);
            if (!filled.SequenceEqual(bytes))
            {
                try
                {
                    lease.Position = 0;
                    lease.Write(filled, 0, filled.Length);
                    lease.SetLength(filled.Length);
                    lease.Flush(flushToDisk: true);
                }
                catch
                {
                    HasPendingChanges = true;
                    _failed = true;
                    Status = "Walk PDF preparation could not finish. Working copy retained; resolve it before retrying.";
                    throw;
                }
            }
            _baseline = filled;
            OfficialPrepared = true;
            Status = "Walk PDF prepared. Only a subsequent external save will be captured to INS.";
        }

        internal PdfEditMonitor(InspectionFile owner, int? index, string filename, byte[] bytes,
            string inspectionPath, string workingRoot, bool pendingAdd = false)
        {
            PdfAttachmentService.Validate(bytes);
            if (!PdfAttachmentService.SafeFilename(filename)) throw new IOException("Unsafe PDF filename.");
            _owner = owner; AttachmentIndex = index; _filename = filename;
            _expectedAttachment = index.HasValue ? (JObject)PdfAttachmentService.Attachment(owner, index.Value).DeepClone() : null;
            // Autofill changes only the working copy; opening alone never stages or saves it.
            _baseline = bytes.ToArray();
            IsPendingAdd = pendingAdd;
            if (pendingAdd)
            {
                HasPendingChanges = true;
                _observed = bytes.ToArray();
                _observedAt = DateTimeOffset.MinValue;
                Status = "PDF add pending. Use Retry Save.";
            }
            string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(inspectionPath).ToUpperInvariant())));
            PdfWorkingSession.Scavenge(workingRoot, DateTimeOffset.UtcNow);
            _directory = Path.Combine(Path.GetFullPath(workingRoot), identity, Guid.NewGuid().ToString("N"));
            PdfWorkingSession.RequirePlainPath(_directory);
            Directory.CreateDirectory(_directory);
            WorkingPath = Path.Combine(_directory, "Orientation.pdf");
            try
            {
                _sessionLease = PdfWorkingSession.CreateLease(_directory);
                File.WriteAllBytes(WorkingPath, bytes);
            }
            catch
            {
                _sessionLease?.Dispose();
                // No recursive deletion, including on unexpected/reparse-point paths.
                throw;
            }
        }

        public bool Poll(DateTimeOffset now, Func<bool> save) => Observe(now, save, false);
        public bool RetrySave(Func<bool> save) => Observe(DateTimeOffset.UtcNow, save, true);

        private bool Observe(DateTimeOffset now, Func<bool> save, bool retry)
        {
            if (_closed) return false;
            byte[] bytes;
            try { bytes = PdfAttachmentService.ReadBytes(WorkingPath); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _observed = null; _failed = true; HasPendingChanges = true;
                Status = "PDF is locked, missing, or incomplete. Finish/save/close the PDF editor and retry. " + ex.Message;
                return false;
            }
            if (!IsPendingAdd && bytes.SequenceEqual(_baseline))
            {
                _observed = null; _failed = false; HasPendingChanges = false; Status = "No uncaptured disk changes."; return false;
            }
            HasPendingChanges = true;
            if (_observed == null || !bytes.SequenceEqual(_observed))
            {
                _observed = bytes; _observedAt = now; _failed = false;
                Status = "PDF changed; waiting for a stable complete save."; return false;
            }
            if (now - _observedAt < Debounce) return false;
            if (_failed && !retry) return false;
            try
            {
                // Full parse only after changed bytes are stable; unchanged timer polls never parse PDFs.
                PdfAttachmentService.Validate(bytes);
                var expected = PdfAttachmentService.Snapshot(_owner);
                var replacement = (JArray)expected.DeepClone();
                JObject attachment;
                int target;
                if (AttachmentIndex.HasValue)
                {
                    target = AttachmentIndex.Value;
                    if (target >= expected.Count || !JToken.DeepEquals(expected[target], _expectedAttachment))
                        throw new IOException("Attachment changed during editing. Refresh/reconcile the attachment before retrying; the working PDF is retained.");
                    attachment = (JObject)_expectedAttachment!.DeepClone();
                    attachment["FileData"] = Convert.ToBase64String(bytes);
                    if (attachment["ReducedFileData"] != null) attachment["ReducedFileData"] = null;
                    replacement[target] = attachment;
                }
                else
                {
                    if (expected.OfType<JObject>().Any(a => string.Equals(a.Value<string>("Filename"), _filename, StringComparison.OrdinalIgnoreCase)))
                        throw new IOException("The template attachment was added during editing; refresh before retrying.");
                    target = replacement.Count; attachment = PdfAttachmentService.Create(_filename, bytes); replacement.Add(attachment);
                }
                if (!new PdfAttachmentService(_owner).Transact(expected, replacement, save))
                    throw new IOException("INS save failed. The working PDF is retained. Use Retry Save after resolving the save failure.");
                IsPendingAdd = false;
                AttachmentIndex = target; _expectedAttachment = (JObject)attachment.DeepClone();
                _baseline = bytes; _observed = null; _failed = false; HasPendingChanges = false;
                Status = "PDF saved to INS (all pages).";
                return true;
            }
            catch (Exception ex)
            {
                _failed = true; HasPendingChanges = true; Status = "PDF not saved to INS. Retry Save: " + ex.Message; return false;
            }
        }

        /// <summary>Always rereads disk. Cannot detect unsaved bytes held only in the external editor's memory.</summary>
        public bool CanLeave(out string reason)
        {
            if (_closed) { reason = ""; return true; }
            try
            {
                if (!IsPendingAdd && PdfAttachmentService.ReadBytes(WorkingPath).SequenceEqual(_baseline))
                {
                    HasPendingChanges = false; reason = ""; return true;
                }
                reason = "PDF has uncaptured disk changes. Finish/save/close your PDF editor and wait for capture, or use Retry Save.";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { reason = "PDF is locked, missing, or incomplete. Finish/save/close your PDF editor and retry. " + ex.Message; }
            HasPendingChanges = true; Status = reason; return false;
        }

        internal FileStream LeaseForRetirement()
        {
            FileStream? lease = null;
            try
            {
                PdfWorkingSession.RequirePlainPath(_directory);
                lease = PdfWorkingSession.OpenRetirementLease(WorkingPath);
                if (IsPendingAdd || lease.Length != _baseline.Length)
                    throw new IOException("PDF has uncaptured disk changes. Use Retry Save.");
                using var current = new MemoryStream();
                lease.CopyTo(current);
                if (!current.ToArray().SequenceEqual(_baseline))
                    throw new IOException("PDF has uncaptured disk changes. Use Retry Save.");
                return lease;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                lease?.Dispose();
                HasPendingChanges = true;
                Status = "PDF cleanup blocked; working copy retained. " + ex.Message;
                throw;
            }
        }

        public void Cleanup() => Cleanup(null);

        // Deterministic seam at the exact former TOCTOU boundary. Production uses no callback.
        internal void Cleanup(Action? afterComparison)
        {
            if (_closed) return;
            using (var lease = LeaseForRetirement())
            {
                afterComparison?.Invoke();
                PdfWorkingSession.DeleteLeasedFile(lease, WorkingPath);
                _closed = true;
                HasPendingChanges = false;
            }
            FinishRetirement();
        }

        internal void FinishRetirement()
        {
            // Retire only our known marker and then the empty session. Never recursively
            // delete: unexpected editor files and all parent/source directories survive.
            try
            {
                if (_sessionLease != null)
                    PdfWorkingSession.DeleteLeasedFile(_sessionLease, Path.Combine(_directory, PdfWorkingSession.LockName));
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            finally { _sessionLease?.Dispose(); _sessionLease = null; }
            try { Directory.Delete(_directory, false); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
