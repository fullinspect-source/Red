using InspectionEditor.Models;
using Newtonsoft.Json.Linq;
using PdfSharp.Pdf.IO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace InspectionEditor.Services
{
    /// <summary>Full-packet attachment transactions. No INS metadata path is ever followed.</summary>
    public sealed class PdfAttachmentService
    {
        public const long WarningBytes = 15L * 1024 * 1024;
        public const long MaxPdfBytes = 100L * 1024 * 1024;
        public sealed record Row(int Index, string Filename, long SizeBytes, string Status, int? PageCount = null, bool CanOpen = true);
        private readonly InspectionFile _owner;
        public PdfAttachmentService(InspectionFile owner) => _owner = owner;

        public static bool SafeFilename(string? name) => !string.IsNullOrWhiteSpace(name) &&
            name == name.Trim() && name.Length <= 240 &&
            name.IndexOfAny(new[] { '/', '\\', ':', '"', '<', '>', '|', '?', '*' }) < 0 &&
            !name.Any(char.IsControl) && name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) &&
            !Regex.IsMatch(name, @"\A(?:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", RegexOptions.IgnoreCase);

        public static void Validate(byte[] bytes) => ValidateAndCount(bytes);

        private static int ValidateAndCount(byte[] bytes)
        {
            if (bytes.LongLength < 8 || bytes.LongLength > MaxPdfBytes ||
                !bytes.AsSpan(0, 5).SequenceEqual(Encoding.ASCII.GetBytes("%PDF-")) ||
                !Encoding.ASCII.GetString(bytes, Math.Max(0, bytes.Length - 2048), Math.Min(bytes.Length, 2048))
                    .TrimEnd('\0', ' ', '\r', '\n', '\t', '\f').EndsWith("%%EOF", StringComparison.Ordinal))
                throw new IOException("PDF is incomplete or invalid (100 MiB maximum). Finish saving in the PDF editor and retry.");
            return PageCount(bytes);
        }

        public static int PageCount(byte[] bytes)
        {
            try
            {
                using var input = new MemoryStream(bytes, writable: false);
                using var document = PdfReader.Open(input, PdfDocumentOpenMode.Import);
                if (document.PageCount < 1) throw new IOException("PDF has no pages.");
                // Force page-tree traversal, not just a plausible header or declared count.
                foreach (var page in document.Pages) { _ = page.Width; _ = page.Height; }
                return document.PageCount;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { throw new IOException("PDF could not be parsed as a complete readable packet.", ex); }
        }

        public static byte[] ReadPdf(string path)
        {
            var bytes = ReadBytes(path); Validate(bytes); return bytes;
        }

        internal static byte[] ReadBytes(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxPdfBytes) throw new IOException("PDF exceeds the 100 MiB safety limit.");
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (memory.Length + read > MaxPdfBytes) throw new IOException("PDF exceeds the 100 MiB safety limit.");
                memory.Write(buffer, 0, read);
            }
            return memory.ToArray();
        }

        internal static JObject Attachment(InspectionFile owner, int index)
        {
            if (index < 0 || index >= (owner.Attachments?.Count ?? 0) || owner.Attachments![index] is not JObject token)
                throw new IOException("The selected attachment is no longer available.");
            return token;
        }

        public static byte[] EmbeddedPdf(JObject attachment) => EmbeddedPdf(attachment, out _);

        private static byte[] EmbeddedPdf(JObject attachment, out int pages)
        {
            if (attachment["FileData"]?.Type != JTokenType.String)
                throw new IOException("Embedded PDF data is missing or is not a base64 string.");
            string encoded = attachment.Value<string>("FileData")!;
            const string prefix = "data:application/pdf;base64,";
            if (encoded.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) encoded = encoded[prefix.Length..];
            if (encoded.Length > MaxPdfBytes * 4 / 3 + 1024) throw new IOException("Embedded PDF exceeds the safety limit.");
            try { var bytes = Convert.FromBase64String(encoded); pages = ValidateAndCount(bytes); return bytes; }
            catch (FormatException ex) { throw new IOException("Embedded PDF contains invalid base64 data.", ex); }
        }

        public IReadOnlyList<Row> Enumerate()
        {
            var rows = new List<Row>();
            for (int i = 0; i < (_owner.Attachments?.Count ?? 0); i++)
            {
                if (_owner.Attachments![i] is not JObject attachment) continue;
                string? name = Filename(attachment);
                if (!SafeFilename(name)) continue;
                try { var bytes = EmbeddedPdf(attachment, out int pages); rows.Add(new Row(i, name!, bytes.LongLength, "Embedded PDF", pages)); }
                catch (IOException)
                {
                    // Filename identity is enough to list/remove a damaged packet, never to open it.
                    // A negative size means unknown, not a fabricated zero-byte PDF.
                    rows.Add(new Row(i, name!, -1, "Damaged / unreadable PDF. Remove or repair; cannot open.", CanOpen: false));
                }
            }
            return rows;
        }

        internal static string? Filename(JObject attachment) => attachment["Filename"]?.Type == JTokenType.String
            ? attachment.Value<string>("Filename") : null;

        internal static JArray Snapshot(InspectionFile owner) => owner.Attachments == null ? new JArray() : JArray.FromObject(owner.Attachments);
        internal static JObject Create(string name, byte[] bytes) => new()
        {
            ["Id"] = Guid.NewGuid().ToString(), ["Filename"] = name, ["EditPath"] = name,
            ["FileType"] = "pdf", ["FileData"] = Convert.ToBase64String(bytes), ["ReducedFileData"] = null,
            ["AnnotatedPages"] = new JArray(), ["IncludedPages"] = new JArray(), ["ReferenceOnly"] = false,
            ["CanExcludePages"] = false, ["ServerPath"] = null
        };

        internal bool Transact(JArray expectedModel, JArray replacement, Func<bool> save)
        {
            if (!JToken.DeepEquals(Snapshot(_owner), expectedModel))
                throw new IOException("Attachment collection changed concurrently. Working files are retained.");
            if (_owner.AttachmentEdit != null || _owner.OrientationEdit != null)
                throw new IOException("Another attachment transaction is pending. Retry after it finishes.");
            var previous = _owner.Attachments;
            var savedBaseline = _owner.SavedAttachments;
            bool success = false;
            _owner.Attachments = replacement.Select(t => (object)t.DeepClone()).ToList();
            _owner.AttachmentEdit = new AttachmentCollectionEdit
            {
                Expected = (JArray)(_owner.SavedAttachments ?? expectedModel).DeepClone(),
                Replacement = (JArray)replacement.DeepClone()
            };
            try
            {
                success = save();
                if (success)
                {
                    _owner.SavedAttachments = (JArray)replacement.DeepClone();
                    _owner.AttachmentEdit = null;
                }
                return success;
            }
            finally
            {
                if (!success)
                {
                    _owner.Attachments = previous;
                    _owner.AttachmentEdit = null;
                    _owner.SavedAttachments = savedBaseline;
                }
            }
        }

        public bool Add(string path, Func<long, bool> approveLarge, Func<bool> save)
        {
            string name = Path.GetFileName(path);
            if (!SafeFilename(name)) throw new IOException("Select a PDF with a safe filename.");
            var bytes = ReadPdf(path);
            var expected = Snapshot(_owner);
            if (bytes.LongLength > WarningBytes && !approveLarge(bytes.LongLength)) return false;
            string stem = Path.GetFileNameWithoutExtension(name);
            int suffix = 2;
            while (expected.OfType<JObject>().Any(a => string.Equals(Filename(a), name, StringComparison.OrdinalIgnoreCase)))
                name = $"{stem} ({suffix++}).pdf";
            if (!SafeFilename(name)) throw new IOException("The disambiguated PDF filename is too long. Rename the source and retry.");
            var replacement = (JArray)expected.DeepClone(); replacement.Add(Create(name, bytes));
            return Transact(expected, replacement, save);
        }

        public PdfEditMonitor? PrepareAdd(string path, Func<long, bool> approveLarge,
            string inspectionPath, string workingRoot, IEnumerable<string>? reservedNames = null)
        {
            string name = Path.GetFileName(path);
            if (!SafeFilename(name)) throw new IOException("Select a PDF with a safe filename.");
            var bytes = ReadPdf(path);
            if (bytes.LongLength > WarningBytes && !approveLarge(bytes.LongLength)) return null;
            var names = Snapshot(_owner).OfType<JObject>().Select(Filename)
                .Concat(reservedNames ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string stem = Path.GetFileNameWithoutExtension(name);
            int suffix = 2;
            while (names.Contains(name)) name = $"{stem} ({suffix++}).pdf";
            return new PdfEditMonitor(_owner, null, name, bytes, inspectionPath, workingRoot, pendingAdd: true);
        }

        public bool Delete(IReadOnlyCollection<int> indices, Func<bool> save,
            IReadOnlyCollection<PdfEditMonitor> monitors)
        {
            int[] removed = indices.Distinct().OrderBy(i => i).ToArray();
            var active = monitors.Where(m => m.BelongsTo(_owner)).ToArray();
            var selected = active.Where(m => m.AttachmentIndex.HasValue && removed.Contains(m.AttachmentIndex.Value)).ToArray();
            var locks = new List<FileStream>();
            try
            {
                // Keep one exclusive lease through comparison, INS commit and file retirement.
                // There is no close/reopen window for a late save after successful deletion.
                foreach (var monitor in selected)
                {
                    locks.Add(monitor.LeaseForRetirement());
                }
                if (!Delete(indices, save)) return false;
                foreach (var monitor in active.Except(selected)) monitor.RebaseAfterDeletion(removed);
                for (int i = 0; i < selected.Length; i++) selected[i].CompleteDeletion(locks[i]);
            }
            finally { foreach (var lease in locks) lease.Dispose(); }
            foreach (var monitor in selected) monitor.FinishRetirement();
            return true;
        }

        public bool Delete(IReadOnlyCollection<int> indices, Func<bool> save)
        {
            if (indices.Count == 0) return false;
            var valid = Enumerate().Select(r => r.Index).ToHashSet();
            if (indices.Any(i => !valid.Contains(i))) throw new IOException("Delete only selected safe-named PDF attachment rows. Refresh the attachment list.");
            var expected = Snapshot(_owner); var replacement = (JArray)expected.DeepClone();
            foreach (int i in indices.Distinct().OrderByDescending(i => i)) replacement.RemoveAt(i);
            return Transact(expected, replacement, save);
        }

        /// <summary>Re-evaluate official identity even for an already indexed/rebased monitor.</summary>
        public void PrepareForOpen(PdfEditMonitor monitor)
        {
            if (!monitor.BelongsTo(_owner)) throw new IOException("This PDF belongs to another report.");
            // Reused monitors must not hide newly damaged embedded data behind a healthy copy.
            if (monitor.AttachmentIndex is int index)
                EmbeddedPdf(Attachment(_owner, index));
            if (!OrientationPdfSession.IsApplicable(_owner)) return;
            bool official;
            try
            {
                var candidates = OrientationPdfSession.FindCandidates(_owner);
                official = candidates.Count == 1 && candidates[0].Index == monitor.AttachmentIndex;
            }
            catch (IOException) { return; } // Invalid declaration still permits general inspection.
            if (official) monitor.PrepareOfficialForOpen();
        }

        public PdfEditMonitor Open(int index, string inspectionPath, string workingRoot)
        {
            // The manager and walk button must share blank-only autofill for a unique official form.
            // Ambiguous/missing walk declarations still allow inspecting arbitrary manager attachments.
            if (OrientationPdfSession.IsApplicable(_owner))
            {
                bool official = false;
                try
                {
                    var candidates = OrientationPdfSession.FindCandidates(_owner);
                    official = candidates.Count == 1 && candidates[0].Index == index;
                }
                catch (IOException) { /* Invalid declaration: general attachment inspection remains available. */ }
                if (official) return OrientationPdfSession.OpenEmbedded(_owner, inspectionPath, index, workingRoot).Monitor;
            }
            var attachment = Attachment(_owner, index);
            string? name = Filename(attachment);
            if (!SafeFilename(name)) throw new IOException("The attachment filename is unsafe; it was not opened.");
            return new PdfEditMonitor(_owner, index, name!, EmbeddedPdf(attachment), inspectionPath, workingRoot);
        }
    }
}
