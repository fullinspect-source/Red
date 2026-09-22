using InspectionEditor.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace InspectionEditor.Services
{
    public enum OrientationPdfDecision { Save, Discard, Cancel }

    /// <summary>
    /// Owns one inspection's external PDF editing copy. Never follows INS EditPath/ServerPath,
    /// never rewrites the template, and never infers editor completion from process exit.
    /// A caller must capture, save through SurgicalSaveService, then explicitly Complete.
    /// </summary>
    public sealed class OrientationPdfSession
    {
        public sealed record Candidate(int Index, string Filename);
        private readonly InspectionFile _owner;
        private readonly int _index;
        private JObject? _modelSnapshot;
        private byte[]? _captured;
        public string WorkingPath { get; }
        public ProcessStartInfo StartInfo => new() { FileName = WorkingPath, UseShellExecute = true };
        private const int MaxPdfBytes = 100 * 1024 * 1024;

        private OrientationPdfSession(InspectionFile owner, string inspectionPath, int index,
            byte[] bytes, string workingRoot, bool embedded)
        {
            _owner = owner;
            _index = index;
            _modelSnapshot = index < (owner.Attachments?.Count ?? 0)
                ? (JObject)Attachment(owner, index).DeepClone() : null;
            _captured = embedded ? bytes : null;
            string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                Path.GetFullPath(inspectionPath).ToUpperInvariant())));
            // Fixed basename also protects Windows shell execution from extensions/arguments in INS metadata.
            string directory = Path.Combine(Path.GetFullPath(workingRoot), identity, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            WorkingPath = Path.Combine(directory, "Orientation.pdf");
            try { File.WriteAllBytes(WorkingPath, bytes); }
            catch { try { Directory.Delete(directory, true); } catch { } throw; }
        }

        public static bool IsApplicable(InspectionFile? inspection) => inspection != null &&
            (string.Equals(inspection.InspectionCode?.Trim(), "BWT", StringComparison.OrdinalIgnoreCase) ||
             (inspection.InspectionName?.StartsWith("New Home Orientation", StringComparison.OrdinalIgnoreCase) ?? false) ||
             string.Equals(inspection.InspectionName?.Trim(), "Buyer Walk", StringComparison.OrdinalIgnoreCase));

        private static Item? DocumentItem(InspectionFile inspection) => inspection.Sections
            .SelectMany(s => s.Items).FirstOrDefault(i =>
                string.Equals(i.ControlName, "DocumentButton", StringComparison.OrdinalIgnoreCase) &&
                ((i.Name?.Contains("orientation", StringComparison.OrdinalIgnoreCase) ?? false) ||
                 (i.ExtensionData?.TryGetValue("ButtonText", out var label) == true &&
                  label?.ToString()?.Contains("orientation", StringComparison.OrdinalIgnoreCase) == true)));

        public static string? TemplateName(InspectionFile inspection)
        {
            var item = DocumentItem(inspection);
            return item?.ExtensionData?.TryGetValue("Template", out var template) == true ? template?.ToString() : null;
        }

        public static string? FindTemplate(InspectionFile inspection, string inspectionPath)
        {
            string? name = TemplateName(inspection);
            // Template is a filename, not permission to fetch URLs or open arbitrary paths.
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '/', '\\', ':', '"', '<', '>', '|', '?', '*' }) >= 0 ||
                name.Any(char.IsControl) || !name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return null;
            string? root = Directory.GetParent(Path.GetDirectoryName(Path.GetFullPath(inspectionPath))!)?.FullName;
            if (root == null) return null;
            string path = Path.Combine(root, "Documents", name);
            return File.Exists(path) ? path : null;
        }

        private static string Basename(string value) => value.Replace('\\', '/').Split('/').Last();
        public static IReadOnlyList<Candidate> FindCandidates(InspectionFile inspection)
        {
            var result = new List<Candidate>();
            if (!IsApplicable(inspection)) return result;
            string stem = Path.GetFileNameWithoutExtension(Basename(TemplateName(inspection) ?? ""));
            for (int i = 0; i < (inspection.Attachments?.Count ?? 0); i++)
            {
                if (inspection.Attachments![i] is not JObject attachment) continue;
                string filename = Basename(attachment.Value<string>("Filename") ?? "");
                bool tagged = attachment["RedOrientationPdf"]?.Type == JTokenType.Boolean && attachment.Value<bool>("RedOrientationPdf");
                bool templateMatch = stem.Length > 0 &&
                    (filename.Equals(stem + ".pdf", StringComparison.OrdinalIgnoreCase) ||
                     filename.StartsWith(stem + " (", StringComparison.OrdinalIgnoreCase));
                if (tagged || (filename.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) &&
                    (templateMatch || filename.Contains("orientation", StringComparison.OrdinalIgnoreCase))))
                    result.Add(new Candidate(i, filename));
            }
            return result;
        }

        private static JObject Attachment(InspectionFile owner, int index)
        {
            if (index < 0 || index >= (owner.Attachments?.Count ?? 0) || owner.Attachments![index] is not JObject attachment)
                throw new IOException("The selected Orientation attachment is no longer available.");
            return attachment;
        }

        private static void Validate(byte[] bytes)
        {
            if (bytes.Length < 8 || bytes.Length > MaxPdfBytes ||
                !bytes.AsSpan(0, 5).SequenceEqual(Encoding.ASCII.GetBytes("%PDF-")) ||
                !Encoding.ASCII.GetString(bytes, Math.Max(0, bytes.Length - 2048), Math.Min(bytes.Length, 2048)).Contains("%%EOF"))
                throw new IOException("The Orientation PDF is incomplete or invalid (100 MB maximum). Save and close it in your PDF editor before trying again.");
        }

        private static byte[] ReadPdf(string path)
        {
            // Deny concurrent writers on Windows; a missing/locked/truncated file must block saving, not erase edits.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxPdfBytes) throw new IOException("Orientation PDF exceeds the 100 MB limit.");
            using var memory = new MemoryStream(); stream.CopyTo(memory);
            byte[] bytes = memory.ToArray(); Validate(bytes); return bytes;
        }

        public static OrientationPdfSession OpenEmbedded(InspectionFile owner, string inspectionPath, int index, string workingRoot)
        {
            if (!IsApplicable(owner) || !FindCandidates(owner).Any(c => c.Index == index))
                throw new IOException("Select an Orientation PDF attachment for this inspection.");
            string encoded = Attachment(owner, index).Value<string>("FileData") ?? "";
            if (encoded.StartsWith("data:application/pdf;base64,", StringComparison.OrdinalIgnoreCase))
                encoded = encoded[(encoded.IndexOf(',') + 1)..];
            if (encoded.Length > MaxPdfBytes * 4L / 3 + 1024) throw new IOException("Embedded Orientation PDF exceeds the limit.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(encoded); }
            catch (FormatException ex) { throw new IOException("The embedded Orientation PDF is damaged. The original is preserved; restore the correct attachment before trying again.", ex); }
            Validate(bytes);
            return new OrientationPdfSession(owner, inspectionPath, index, bytes, workingRoot, true);
        }

        public static OrientationPdfSession OpenTemplate(InspectionFile owner, string inspectionPath, string workingRoot)
        {
            if (!IsApplicable(owner)) throw new IOException("This inspection does not use an Orientation PDF.");
            if (FindCandidates(owner).Count > 0)
                throw new IOException("Open the existing Orientation attachment instead of replacing it with a blank template.");
            string source = FindTemplate(owner, inspectionPath) ?? throw new IOException(
                $"No Orientation PDF is embedded and the exact INS template is missing or unsafe: {TemplateName(owner) ?? "not specified"}. Restore that PDF in Inspections/Documents.");
            byte[] filled = OrientationPdfForm.Fill(ReadPdf(source), owner);
            return new OrientationPdfSession(owner, inspectionPath, owner.Attachments?.Count ?? 0, filled, workingRoot, false);
        }

        /// <summary>Only explicit PDF save stages bytes. Failed saves roll back PDF staging,
        /// not ordinary report edits; the working file remains available for a retry.</summary>
        public bool TryFinish(OrientationPdfDecision decision, Func<bool> saveInspection)
        {
            if (decision == OrientationPdfDecision.Cancel) return false;
            if (decision == OrientationPdfDecision.Discard) { Discard(); return true; }
            if (decision != OrientationPdfDecision.Save) throw new ArgumentOutOfRangeException(nameof(decision));
            var previousAttachments = _owner.Attachments?.ToList();
            var previousEdit = _owner.OrientationEdit;
            var previousSnapshot = _modelSnapshot;
            var previousCaptured = _captured;
            bool saved = false;
            try
            {
                Capture(_owner);
                saved = saveInspection();
                if (!saved) return false;
            }
            finally
            {
                if (!saved)
                {
                    _owner.Attachments = previousAttachments;
                    _owner.OrientationEdit = previousEdit;
                    _modelSnapshot = previousSnapshot;
                    _captured = previousCaptured;
                }
            }
            Complete(); // Late edits or a failed read still retain the session and block leaving.
            return true;
        }

        public void Discard() => Discard(directory => Directory.Delete(directory, true));

        internal void Discard(Action<string> cleanup)
        {
            // Never read/lock the working PDF on discard. An external editor may still own it.
            try { cleanup(Path.GetDirectoryName(WorkingPath)!); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        public bool Capture(InspectionFile inspection)
        {
            if (!ReferenceEquals(inspection, _owner)) throw new IOException("Orientation PDF belongs to a different inspection.");
            byte[] bytes = ReadPdf(WorkingPath);
            if (_captured != null && bytes.SequenceEqual(_captured)) return false;
            if (_modelSnapshot != null && !JToken.DeepEquals(_modelSnapshot, Attachment(inspection, _index)))
                throw new IOException("Orientation attachment changed during editing. The working PDF is retained.");
            var replacement = _modelSnapshot != null ? (JObject)_modelSnapshot.DeepClone() : new JObject
            {
                ["Id"] = Guid.NewGuid().ToString(), ["Filename"] = "Orientation.pdf", ["EditPath"] = "Orientation.pdf",
                ["FileType"] = "pdf", ["RedOrientationPdf"] = true, ["ReducedFileData"] = null,
                ["AnnotatedPages"] = new JArray(), ["IncludedPages"] = new JArray(), ["ReferenceOnly"] = false,
                ["CanExcludePages"] = false, ["ServerPath"] = null
            };
            replacement["FileData"] = Convert.ToBase64String(bytes);
            // A reduced derivative cannot represent a newly edited full PDF; other metadata stays untouched.
            if (replacement["ReducedFileData"]?.Type != JTokenType.Null && replacement["ReducedFileData"] != null)
                replacement["ReducedFileData"] = null;
            inspection.Attachments ??= new List<object>();
            if (_index == inspection.Attachments.Count && _modelSnapshot == null) inspection.Attachments.Add(replacement);
            else if (_index < inspection.Attachments.Count && _modelSnapshot != null) inspection.Attachments[_index] = replacement;
            else throw new IOException("Attachment list changed during PDF editing; no unrelated attachment was overwritten.");
            var expected = inspection.OrientationEdit?.Index == _index
                ? inspection.OrientationEdit.Expected : _modelSnapshot;
            inspection.OrientationEdit = new OrientationAttachmentEdit
            {
                Index = _index, Expected = expected == null ? null : (JObject)expected.DeepClone(), Replacement = replacement
            };
            _modelSnapshot = (JObject)replacement.DeepClone();
            _captured = bytes;
            return true;
        }

        public void Complete()
        {
            // Explicit editor-close acknowledgement AND successful guarded save are caller prerequisites.
            if (_captured == null || !ReadPdf(WorkingPath).SequenceEqual(_captured))
                throw new IOException("The PDF changed again. Save/close your PDF editor and save the Orientation PDF again.");
            try { Directory.Delete(Path.GetDirectoryName(WorkingPath)!, true); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
