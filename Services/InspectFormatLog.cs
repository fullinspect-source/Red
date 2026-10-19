using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using InspectionEditor.Models;
using Newtonsoft.Json.Linq;

namespace InspectionEditor.Services
{
    /// <summary>
    /// F-019 L7: the INSPECT-format lines RED writes when it performs the action INSPECT would log (exact text, leading
    /// spaces included). Values render as INSPECT renders them (L1): strings bare, GUIDs lowercase, integers plain.
    /// </summary>
    public static class InspectFormatLog
    {
        public const string Rule = "------------------------------------";

        // Tests point this at a sandbox writer.
        public static InspectLogWriter Writer { get; set; } = InspectLogWriter.Default;

        public static void Launch(string product, string version)
        {
            Writer.Info(Rule);
            Writer.Info($"Starting {product} version {version}");
            Writer.Info(Rule);
        }

        public static void Exit(string product)
        {
            Writer.Info($"Exiting {product}");
            Writer.FlushPending(); // L3: queued entries go out at exit when INSPECT has released its file
        }

        public static void Opening(Func<string, JToken?> header, InspectionFile model)
        {
            string number = Text(header("InspectionNumber"));
            Writer.Info($"Opening inspection {number}");
            Details(header, model);
        }

        // The save lines; "Creating file" / "File created" only when RED actually wrote the file.
        public static void Saving(Func<string, JToken?> header, InspectionFile model, string path, bool written)
        {
            foreach (string line in SavingLines(header, model, path, written))
                Writer.Info(line);
        }

        public static IEnumerable<string> SavingLines(Func<string, JToken?> header, InspectionFile model, string path, bool written)
        {
            yield return $"Saving inspection {Text(header("InspectionNumber"))}";
            foreach (string line in DetailLines(header, model))
                yield return line;
            if (!written) yield break;
            yield return $"Creating file: {path}";
            yield return $"File created: {path}";
        }

        public static void SaveFailed(string path, Exception exception) =>
            Writer.Warn($"File was not created: {path}. Exception: {exception}");

        public static void Closing(string number) => Writer.Info($"Closing Inspection {number}");

        // LOG §7.8 Print and PDF (F-022 X1): the preview's export, then the printed page count.
        public static void ReportExport(string number, bool defectsOnly, bool pictures, bool attachments, int attachmentCount, bool first = true)
        {
            if (first) Writer.Info($"Printing inspection {number}"); // once per preview (InspectionWindow::PrintPreview)
            Writer.Info("Exporting to PDF");
            Writer.Info($"   Defaults only is {defectsOnly}");
            Writer.Info($"   Include pictures is {pictures}");
            Writer.Info($"   Include attachments is {attachments}");
            if (!attachments) return;
            Writer.Info($"Getting attachments for PDF export... {attachmentCount.ToString(CultureInfo.InvariantCulture)} attachments found");
            Writer.Info($"Adding attachments to PDF... {attachmentCount.ToString(CultureInfo.InvariantCulture)} attachments found");
        }

        public static void Printed(int pages) => Writer.Info($"Printed inspection... {pages.ToString(CultureInfo.InvariantCulture)} pages");

        public static void ListReadFailed(string path, Exception exception) =>
            Writer.Error($"Could not get inspection info from {path}. Exception: {exception}");

        public static void LoadFailed(string path, Exception exception) =>
            Writer.Error($"Could not load inspection {path}. Exception: {exception}");

        // The detail block (LOG §7.4).
        public static void Details(Func<string, JToken?> header, InspectionFile model)
        {
            foreach (string line in DetailLines(header, model))
                Writer.Info(line);
        }

        public static IEnumerable<string> DetailLines(Func<string, JToken?> header, InspectionFile model)
        {
            yield return $"   Number {Text(header("InspectionNumber"))}";
            yield return $"   Address {Text(header("Address"))}";
            yield return $"   Work Order Id {Guid(header("WorkOrderId"))}";
            yield return $"   Version {Text(header("Version"))}";
            yield return $"   Status {(string.IsNullOrWhiteSpace(Text(header("LastDBDate"))) ? "Not Submitted" : "Submitted")}";
            int pictures = (model.Sections ?? new()).Where(s => s?.Items != null).SelectMany(s => s.Items).Where(i => i != null)
                .Sum(i => i.Pictures?.Count ?? 0);
            yield return $"   Picture Count {pictures.ToString(CultureInfo.InvariantCulture)}";
        }

        private static string Text(JToken? token) => token == null || token.Type == JTokenType.Null ? "" :
            token.Type == JTokenType.Integer ? ((long)token).ToString(CultureInfo.InvariantCulture) :
            token.Type == JTokenType.Boolean ? ((bool)token ? "True" : "False") : token.ToString();

        private static string Guid(JToken? token) =>
            System.Guid.TryParse(Text(token), out var id) ? id.ToString("D") : Text(token);
    }
}
