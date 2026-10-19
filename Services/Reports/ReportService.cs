using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InspectionEditor.Services.Reports
{
    /// <summary>
    /// F-022 without UI: the report PDF for an inspection, the option defaults (O1), the file name (X2) and the e-mail draft
    /// (X3). The input is the INSPECT-dialect document that a save would write (so a pending F-004 V1 NA→NI rewrite shows).
    /// </summary>
    public static class ReportService
    {
        public const string TempFolderName = "RED-Reports";

        public sealed record Rendered(byte[] Pdf, int ReportPages, int AttachmentsAdded);

        public static JObject ParseDocument(byte[] utf8Json)
        {
            using var reader = new JsonTextReader(new StringReader(Encoding.UTF8.GetString(utf8Json))) { DateParseHandling = DateParseHandling.None };
            return JObject.Load(reader);
        }

        public static Rendered Render(JObject inspectDocument, ReportSettings settings, ReportOptions options, DateTime printDate)
        {
            var report = ReportBuilder.Build(inspectDocument, options);
            byte[] pdf = ReportRenderer.Render(report, settings, options, printDate, out int reportPages);
            int attachments = options.Attachments ? report.Attachments.OfType<JObject>().Count(a => !(a["ReferenceOnly"]?.Type == JTokenType.Boolean && (bool)a["ReferenceOnly"]!)) : 0;
            return new Rendered(pdf, reportPages, attachments);
        }

        // O1: the device's remembered toggles, else INSPECT's UserConfig values (F-003 profile), else the defaults.
        public static ReportOptions InitialOptions(ReportSettings settings, InspectorProfile? profile) => new(
            settings.DefectsOnly ?? profile?.ReportViewerDefectsOnly ?? false,
            settings.Pictures ?? profile?.ReportViewerPictures ?? true,
            settings.Attachments ?? profile?.ReportViewerAttachments ?? true);

        public static void Remember(ReportSettings settings, ReportOptions options, string? root = null)
        {
            settings.DefectsOnly = options.DefectsOnly;
            settings.Pictures = options.Pictures;
            settings.Attachments = options.Attachments;
            settings.Save(root);
        }

        // X2 / X3: "{InspectionNumber}.pdf"; a number that is not a plain file name falls back to "Inspection Report.pdf".
        public static string PdfFileName(string? inspectionNumber) =>
            SubmitService.IsSafeReportName(inspectionNumber) ? inspectionNumber + ".pdf" : "Inspection Report.pdf";

        public static string EmailSubject(string address, string inspectionName) => $"{address} - {inspectionName} Inspection Report";

        public static string EmailBody(string inspectionName, string address, string? inspectorName)
        {
            var body = new StringBuilder("<div style='font-size: 12pt; color: #373737;'>");
            body.Append("Attached is the ").Append(inspectionName).Append(" inspection report for ").Append(address).Append(".<br /><br />");
            if (!string.IsNullOrWhiteSpace(inspectorName))
                body.Append("Thank you,<br />").Append(inspectorName.Trim());
            body.Append("</div>");
            return body.ToString();
        }

        /// <summary>X3 Windows: an unsent RFC 5322 draft (X-Unsent: 1) that the default mail app opens for editing.</summary>
        public static string BuildEml(string subject, string htmlBody, string? from, string attachmentName, byte[] attachment)
        {
            string boundary = "----=_RED_" + Guid.NewGuid().ToString("N");
            var eml = new StringBuilder();
            eml.Append("X-Unsent: 1\r\n");
            if (!string.IsNullOrWhiteSpace(from) && from.Contains('@')) eml.Append("From: ").Append(from.Trim()).Append("\r\n");
            eml.Append("To: \r\n");
            eml.Append("Subject: ").Append(EncodeHeader(subject)).Append("\r\n");
            eml.Append("MIME-Version: 1.0\r\n");
            eml.Append("Content-Type: multipart/mixed; boundary=\"").Append(boundary).Append("\"\r\n\r\n");
            eml.Append("--").Append(boundary).Append("\r\n");
            eml.Append("Content-Type: text/html; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n");
            eml.Append(Base64Lines(Encoding.UTF8.GetBytes(htmlBody)));
            eml.Append("--").Append(boundary).Append("\r\n");
            string name = EncodeHeader(attachmentName);
            eml.Append("Content-Type: application/pdf; name=\"").Append(name).Append("\"\r\n");
            eml.Append("Content-Transfer-Encoding: base64\r\n");
            eml.Append("Content-Disposition: attachment; filename=\"").Append(name).Append("\"\r\n\r\n");
            eml.Append(Base64Lines(attachment));
            eml.Append("--").Append(boundary).Append("--\r\n");
            return eml.ToString();
        }

        private static string EncodeHeader(string value) =>
            value.All(c => c >= 32 && c < 127 && c != '"') ? value : "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "?=";

        private static string Base64Lines(byte[] bytes)
        {
            string text = Convert.ToBase64String(bytes);
            var lines = new StringBuilder(text.Length + text.Length / 76 * 2 + 2);
            for (int i = 0; i < text.Length; i += 76)
                lines.Append(text, i, Math.Min(76, text.Length - i)).Append("\r\n");
            return lines.ToString();
        }

        // X4: one folder per preview under %TEMP%\RED-Reports, removed when the preview closes; old leftovers are removed too.
        public static string NewTempFolder()
        {
            string root = Path.Combine(Path.GetTempPath(), TempFolderName);
            try
            {
                if (Directory.Exists(root))
                    foreach (var old in Directory.GetDirectories(root).Where(d => Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddDays(-1)))
                        TryDelete(old);
            }
            catch (Exception) { }
            string folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        public static void TryDelete(string folder)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (Exception) { }
        }
    }
}
