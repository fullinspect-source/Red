using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace InspectionEditor.Services
{
    // F-014 L3: list rows read the header only, as INSPECT stops parsing at "Sections" (INV §2.7.1). Reads a prefix of the
    // file and walks the top-level properties until "Sections"; a header longer than the prefix is read in full.
    public sealed class InspectionHeader
    {
        public Dictionary<string, string?> Strings { get; } = new(StringComparer.Ordinal);
        public Dictionary<int, string> StatusNames { get; } = new();
        public int? StatusId { get; set; }
        public bool HasSignature { get; set; }
        public bool ReachedSections { get; set; }

        public string? this[string key] => Strings.TryGetValue(key, out var v) ? v : null;

        public string StatusName => StatusId is int id && StatusNames.TryGetValue(id, out var name) ? name : "";

        // L4 extra column: LastDBDate as MM/dd/yyyy when set.
        public string ProcessedDisplay
        {
            get
            {
                string? raw = this["LastDBDate"];
                if (string.IsNullOrWhiteSpace(raw)) return "";
                return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    ? d.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) : raw;
            }
        }
    }

    public static class InspectionHeaderReader
    {
        private const int Prefix = 96 * 1024; // header plus the ~35 KB signature fits; longer headers are read in full

        public static InspectionHeader? Read(string path)
        {
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                int length = (int)Math.Min(stream.Length, Prefix);
                bytes = new byte[length];
                int read = 0;
                while (read < length) { int r = stream.Read(bytes, read, length - read); if (r <= 0) break; read += r; }
                if (read < length) Array.Resize(ref bytes, read);
                var header = Parse(bytes, isFinal: stream.Length <= Prefix);
                if (header != null && (header.ReachedSections || stream.Length <= Prefix))
                    return header;
            }
            return Parse(File.ReadAllBytes(path), isFinal: true);
        }

        public static InspectionHeader? Parse(byte[] bytes, bool isFinal)
        {
            int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(bytes, start, bytes.Length - start), isFinal, default);
            var header = new InspectionHeader();
            try
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                    return null;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                        break;
                    if (reader.TokenType != JsonTokenType.PropertyName)
                        return header;
                    string name = reader.GetString() ?? "";
                    if (name == "Sections")
                    {
                        header.ReachedSections = true;
                        return header;
                    }
                    if (!reader.Read())
                        return header;
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.String:
                            if (name == "Signature")
                            {
                                // Only presence matters; never materialise the ~35 KB image string.
                                int length = reader.HasValueSequence ? (int)reader.ValueSequence.Length : reader.ValueSpan.Length;
                                header.HasSignature = length > 4 || (length > 0 && !string.Equals(reader.GetString(), "None", StringComparison.OrdinalIgnoreCase) &&
                                                                     !string.IsNullOrWhiteSpace(reader.GetString()));
                                break;
                            }
                            if ((reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length) > 4096)
                                break; // long values (images, attachments) are not list columns
                            header.Strings[name] = reader.GetString();
                            break;
                        case JsonTokenType.Number:
                            header.Strings[name] = Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan.ToArray());
                            if (name == "StatusId" && reader.TryGetInt32(out int status)) header.StatusId = status;
                            break;
                        case JsonTokenType.True:
                        case JsonTokenType.False:
                            header.Strings[name] = reader.TokenType == JsonTokenType.True ? "true" : "false";
                            break;
                        case JsonTokenType.Null:
                            header.Strings[name] = null;
                            break;
                        case JsonTokenType.StartArray when name == "StatusList":
                            ReadStatusList(ref reader, header);
                            break;
                        case JsonTokenType.StartArray:
                        case JsonTokenType.StartObject:
                            if (!reader.TrySkip()) return header;
                            break;
                    }
                }
            }
            catch (JsonException)
            {
                return header.Strings.Count > 0 ? header : null;
            }
            return header;
        }

        private static void ReadStatusList(ref Utf8JsonReader reader, InspectionHeader header)
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.TrySkip();
                    continue;
                }
                int? id = null; string? name = null;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    string key = reader.GetString() ?? "";
                    reader.Read();
                    if (key == "Id" && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value)) id = value;
                    else if (key == "Name" && reader.TokenType == JsonTokenType.String) name = reader.GetString();
                    else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.TrySkip();
                }
                if (id.HasValue) header.StatusNames[id.Value] = name ?? "";
            }
        }
    }
}
