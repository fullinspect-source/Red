using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InspectionEditor.Services
{
    /// <summary>
    /// F-024: INSPECT's comment libraries, read only (owner Q-34). {IF}\Data\CompanyComments.cmt and UserComments.cmt, each
    /// read from its .temp.cmt mirror first and the main file when the mirror is missing or unreadable (F1). Content: a JSON
    /// object keyed by ItemId, each value a list of {"Selected": bool, "Value": text} (F2). Other .cmt names are ignored
    /// (F3). RED never writes these files (B3); a missing or unreadable library is empty and its section hidden (B5).
    /// </summary>
    public static class InspectCommentLibrary
    {
        public enum Kind { Company, User }

        public const string CompanyTitle = "Company comments";
        public const string UserTitle = "My INSPECT comments";

        private sealed record Cached(string Path, DateTime WriteUtc, long Length, Dictionary<string, List<string>> Entries);
        private static readonly Dictionary<string, Cached> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object Gate = new();

        public static string FileName(Kind kind, bool temp) => (kind == Kind.Company ? "CompanyComments" : "UserComments") + (temp ? ".temp.cmt" : ".cmt");

        /// <summary>The entries for one item, in file order (blank values skipped).</summary>
        public static IReadOnlyList<string> Entries(string? inspectionsFolder, Kind kind, int itemId)
        {
            if (string.IsNullOrWhiteSpace(inspectionsFolder)) return Array.Empty<string>();
            var library = Load(Path.Combine(inspectionsFolder, "Data"), kind);
            return library.TryGetValue(itemId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var list) ? list : Array.Empty<string>();
        }

        // B2: INSPECT's "{0}, {1}"; an empty comment becomes the entry itself.
        public static string Append(string? existing, string entry) =>
            string.IsNullOrWhiteSpace(existing) ? entry : $"{existing}, {entry}";

        private static Dictionary<string, List<string>> Load(string dataFolder, Kind kind)
        {
            foreach (bool temp in new[] { true, false })
            {
                string path = Path.Combine(dataFolder, FileName(kind, temp));
                var entries = Read(path);
                if (entries != null) return entries;
            }
            return new Dictionary<string, List<string>>();
        }

        // Null when the file is missing or cannot be read as a library (the caller then tries the next file).
        private static Dictionary<string, List<string>>? Read(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                lock (Gate)
                {
                    if (Cache.TryGetValue(path, out var cached) && cached.WriteUtc == info.LastWriteTimeUtc && cached.Length == info.Length)
                        return cached.Entries;
                }
                string text;
                // Read only, sharing with INSPECT or Dropbox writing the file at the same time.
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                    text = reader.ReadToEnd();
                var entries = Parse(text);
                if (entries == null) return null;
                lock (Gate) Cache[path] = new Cached(path, info.LastWriteTimeUtc, info.Length, entries);
                return entries;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        public static Dictionary<string, List<string>>? Parse(string text)
        {
            JObject root;
            try
            {
                using var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None };
                if (JToken.ReadFrom(reader) is not JObject parsed) return null;
                root = parsed;
            }
            catch (JsonException)
            {
                return null;
            }
            var entries = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var property in root.Properties())
            {
                if (property.Value is not JArray list) continue;
                var values = list.OfType<JObject>()
                    .Select(e => e["Value"]?.Type == JTokenType.String ? (string?)e["Value"] : null)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v!)
                    .ToList();
                if (values.Count > 0) entries[property.Name.Trim()] = values;
            }
            return entries;
        }
    }
}
