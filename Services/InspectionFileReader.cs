using System;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InspectionEditor.Services
{
    /// <summary>The .ins file is held by another process (usually a Dropbox sync) after all read retries.</summary>
    public sealed class InspectionFileBusyException : IOException
    {
        public InspectionFileBusyException(Exception inner) : base(InspectionFileReader.BusyMessage, inner) { }
    }

    /// <summary>The .ins content is empty, starts with NUL characters or is not a JSON object. Never edited or deleted.</summary>
    public sealed class InspectionFileDamagedException : IOException
    {
        public InspectionFileDamagedException(Exception? inner = null) : base(InspectionFileReader.DamagedMessage, inner) { }
    }

    /// <summary>
    /// F-002 R2-R5: tolerant reading of .ins files. UTF-8 with or without BOM, retries on sharing violations,
    /// and a damaged-file stop that never modifies the file.
    /// </summary>
    public static class InspectionFileReader
    {
        public const string BusyMessage = "The inspection file is busy (probably syncing). Try again in a moment.";
        public const string DamagedMessage = "This inspection file is damaged and cannot be opened.";

        // Per-call seam so harnesses can exercise the retry loop without real locks.
        internal sealed class Operations
        {
            internal Func<string, byte[]> ReadAllBytes = File.ReadAllBytes;
            internal Action<int> Delay = Thread.Sleep;
        }

        private static readonly int[] RetryDelays = { 100, 200, 400 };

        public static byte[] ReadBytes(string path) => ReadBytes(path, new Operations());

        internal static byte[] ReadBytes(string path, Operations operations)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return operations.ReadAllBytes(path); }
                catch (IOException ex) when (IsSharingViolation(ex))
                {
                    if (attempt >= RetryDelays.Length) throw new InspectionFileBusyException(ex);
                    operations.Delay(RetryDelays[attempt]);
                }
            }
        }

        /// <summary>Reads, decodes and parses an .ins file. Date strings stay verbatim; numbers keep their decimal text.</summary>
        public static JObject ReadDocument(string path, out byte[] bytes, out string text)
        {
            bytes = ReadBytes(path);
            return ParseDocument(bytes, out text);
        }

        public static JObject ParseDocument(byte[] bytes, out string text)
        {
            text = Decode(bytes);
            if (text.Length == 0 || text[0] == '\0' || string.IsNullOrWhiteSpace(text))
                throw new InspectionFileDamagedException();
            try
            {
                using var reader = CreateReader(text);
                if (JToken.ReadFrom(reader) is not JObject document)
                    throw new InspectionFileDamagedException();
                while (reader.Read())
                {
                    if (reader.TokenType != JsonToken.Comment) throw new InspectionFileDamagedException();
                }
                return document;
            }
            catch (JsonException ex) { throw new InspectionFileDamagedException(ex); }
        }

        public static string Decode(byte[] bytes)
        {
            using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        public static JsonTextReader CreateReader(string text) => new JsonTextReader(new StringReader(text))
        {
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Decimal
        };

        private static bool IsSharingViolation(IOException exception)
        {
            uint value = unchecked((uint)exception.HResult);
            return value == 0x80070020 || value == 0x80070021;
        }
    }
}
