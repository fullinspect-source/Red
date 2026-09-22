using InspectionEditor.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace InspectionEditor.Services
{
    /// <summary>Strict walk-document resolver. All editing/capture is owned by the general PDF monitor.</summary>
    public sealed class OrientationPdfSession
    {
        public sealed record Candidate(int Index, string Filename);
        public PdfEditMonitor Monitor { get; }
        public string WorkingPath => Monitor.WorkingPath;
        public ProcessStartInfo StartInfo => Monitor.StartInfo;
        private OrientationPdfSession(PdfEditMonitor monitor) => Monitor = monitor;

        public static bool IsApplicable(InspectionFile? inspection) => inspection != null &&
            (string.Equals(inspection.InspectionCode?.Trim(), "BWT", StringComparison.OrdinalIgnoreCase) ||
             (inspection.InspectionName?.StartsWith("New Home Orientation", StringComparison.OrdinalIgnoreCase) ?? false) ||
             string.Equals(inspection.InspectionName?.Trim(), "Buyer Walk", StringComparison.OrdinalIgnoreCase));

        public static string? TemplateName(InspectionFile inspection)
        {
            var items = inspection.Sections.SelectMany(s => s.Items)
                .Where(i => string.Equals(i.ControlName, "DocumentButton", StringComparison.OrdinalIgnoreCase)).ToList();
            if (items.Count != 1) return null;
            return items[0].ExtensionData?.TryGetValue("Template", out var template) == true && template is string name
                ? name : items[0].ExtensionData?.TryGetValue("Template", out template) == true && template is JValue value && value.Type == JTokenType.String
                    ? value.Value<string>() : null;
        }

        private static IOException Blocked(InspectionFile inspection, string detail) => new(
            $"RED could not use the exact walk document \"{TemplateName(inspection) ?? "Template not specified or ambiguous"}\" in Inspections/Documents. {detail} " +
            "RED will not substitute another region's form. Contact Trent to install the correct walk document.");

        private static IOException DamagedEmbedded(InspectionFile inspection, Exception ex) => new(
            $"The embedded walk document matching \"{TemplateName(inspection)}\" is damaged or unreadable. " +
            "Use ATTACHMENTS to remove the damaged row (confirm Delete checked), then reopen the walk document, " +
            "or contact Trent to repair the attachment. RED will not substitute a template or another PDF while this official attachment exists. " +
            ex.Message, ex);

        private static string RequireTemplate(InspectionFile inspection)
        {
            string? name = TemplateName(inspection);
            if (!IsApplicable(inspection) || !PdfAttachmentService.SafeFilename(name))
                throw Blocked(inspection, "The INS Template is missing, ambiguous, or unsafe.");
            return name!;
        }

        public static string? FindTemplate(InspectionFile inspection, string inspectionPath)
        {
            string? name = TemplateName(inspection);
            if (!PdfAttachmentService.SafeFilename(name)) return null;
            string? root = Directory.GetParent(Path.GetDirectoryName(Path.GetFullPath(inspectionPath))!)?.FullName;
            if (root == null) return null;
            string path = Path.Combine(root, "Documents", name!);
            return File.Exists(path) ? path : null;
        }

        private static bool Official(string name, string template)
        {
            if (!PdfAttachmentService.SafeFilename(name)) return false;
            string pattern = @"\A" + Regex.Escape(Path.GetFileNameWithoutExtension(template)) +
                @"(?: \([^()\r\n]+ - (?<date>[0-9]{8})\))?\.pdf\z";
            var match = Regex.Match(name, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success && (!match.Groups["date"].Success || DateTime.TryParseExact(match.Groups["date"].Value,
                "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
        }

        public static IReadOnlyList<Candidate> FindCandidates(InspectionFile inspection)
        {
            var result = new List<Candidate>();
            if (!IsApplicable(inspection)) return result;
            string template = RequireTemplate(inspection);
            for (int i = 0; i < (inspection.Attachments?.Count ?? 0); i++)
            {
                if (inspection.Attachments![i] is not JObject attachment) continue;
                string name = PdfAttachmentService.Filename(attachment) ?? "";
                if (Official(name, template)) result.Add(new Candidate(i, name));
            }
            return result;
        }

        public static int? PreferredCandidateIndex(InspectionFile inspection, IReadOnlyList<Candidate> candidates)
        {
            // Recompute from the authoritative model, never trust a caller-supplied broad candidate list.
            var official = FindCandidates(inspection);
            if (official.Count > 1) throw Blocked(inspection, "Multiple exact official attachments exist. Use ATTACHMENTS to inspect/delete extras.");
            if (official.Count == 1)
            {
                try { PdfAttachmentService.EmbeddedPdf(PdfAttachmentService.Attachment(inspection, official[0].Index)); }
                catch (IOException ex) { throw DamagedEmbedded(inspection, ex); }
            }
            return official.Count == 1 ? official[0].Index : null;
        }

        public static OrientationPdfSession OpenEmbedded(InspectionFile owner, string inspectionPath, int index, string workingRoot)
        {
            var candidates = FindCandidates(owner);
            if (PreferredCandidateIndex(owner, candidates) != index) throw Blocked(owner, "The selected attachment is not the exact official document.");
            try
            {
                var attachment = PdfAttachmentService.Attachment(owner, index);
                var bytes = PdfAttachmentService.EmbeddedPdf(attachment);
                var monitor = new PdfEditMonitor(owner, index, attachment.Value<string>("Filename")!, bytes, inspectionPath, workingRoot);
                try { monitor.PrepareOfficialForOpen(); }
                catch { monitor.AbandonFailedPreparation(); throw; }
                return new OrientationPdfSession(monitor);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { throw Blocked(owner, "The embedded document could not be opened: " + ex.Message); }
        }

        public static OrientationPdfSession OpenTemplate(InspectionFile owner, string inspectionPath, string workingRoot)
        {
            string name = RequireTemplate(owner);
            if (FindCandidates(owner).Count > 0) throw Blocked(owner, "An official attachment already exists. Use ATTACHMENTS to inspect it, remove a damaged row or extras, or contact Trent to repair it before using the exact template.");
            string source = FindTemplate(owner, inspectionPath) ?? throw Blocked(owner, "The exact file was not found.");
            try
            {
                var monitor = new PdfEditMonitor(owner, null, name, PdfAttachmentService.ReadPdf(source), inspectionPath, workingRoot);
                try { monitor.PrepareOfficialForOpen(); }
                catch { monitor.AbandonFailedPreparation(); throw; }
                return new OrientationPdfSession(monitor);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { throw Blocked(owner, "The exact template is not a valid readable PDF: " + ex.Message); }
        }
    }
}
