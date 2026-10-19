using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace InspectionEditor.Services.Reports
{
    /// <summary>
    /// F-022 §11: everything the owner supplies or approves. RED ships none of INSPECT's texts, logos or account lists:
    /// until a text is approved its box prints "text pending approval" (owner Q-49 default); an image that is not supplied
    /// prints nothing (Q-50); account groups that are not supplied are empty. Kept in RED's app data (report-settings.json).
    /// </summary>
    public sealed class ReportSettings
    {
        public const string PendingText = "text pending approval";

        public string? CompanyName { get; set; }          // LA5 caption "<COMPANY_NAME>:" and the Kyle page
        public string? CompanyPhone { get; set; }         // Kyle page
        public string? LogoPath { get; set; }             // LA5 / LA14
        public string? StampPath { get; set; }            // LA12 "Pre-Inspection" artwork
        public string? CityLogoPath { get; set; }         // K1
        public string? CityDeptStreet { get; set; }
        public string? CityDeptCityStateZip { get; set; }
        public string? CityDeptPhone { get; set; }
        public string? BuilderBName { get; set; }         // N5 "<BLDR-B> or Contracted Designee:"
        public Dictionary<string, string> ApprovedTexts { get; set; } = new(StringComparer.Ordinal); // "T1".."T12"
        public List<string> BuilderGroupAAccounts { get; set; } = new();   // N8 acknowledgement band
        public List<string> BuilderDfwEastAccounts { get; set; } = new();  // N7 table suppressed

        // Report viewer toggles (O1), remembered per device; first values come from INSPECT's UserConfig (F-003 profile).
        public bool? DefectsOnly { get; set; }
        public bool? Pictures { get; set; }
        public bool? Attachments { get; set; }

        public string Text(string id) => ApprovedTexts.TryGetValue(id, out var text) && !string.IsNullOrWhiteSpace(text) ? text : PendingText;

        public string JobCaption => string.IsNullOrWhiteSpace(CompanyName) ? "Job:" : CompanyName.Trim() + ":";

        private static string PathOf(string? root) => Path.Combine(root ?? AppIdentity.LocalAppDataPath, "report-settings.json");

        public static ReportSettings Load(string? root = null)
        {
            try { return File.Exists(PathOf(root)) ? JsonConvert.DeserializeObject<ReportSettings>(File.ReadAllText(PathOf(root))) ?? new() : new(); }
            catch (Exception) { return new(); }
        }

        public void Save(string? root = null)
        {
            string path = PathOf(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(this, Formatting.Indented));
            File.Move(temp, path, overwrite: true);
        }

        public static byte[]? ReadImage(string? path)
        {
            try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? File.ReadAllBytes(path) : null; }
            catch (Exception) { return null; }
        }
    }

    public sealed record ReportOptions(bool DefectsOnly, bool Pictures, bool Attachments);
}
