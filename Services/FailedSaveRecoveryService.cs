using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace InspectionEditor.Services
{
    /// <summary>Independent, local full snapshots of failed saves. Never replaces a report.</summary>
    public sealed class FailedSaveRecoveryService
    {
        private readonly string? _recoveryRoot;
        private string? _lastVerifiedPath;
        private readonly Func<string, byte[]> _readAllBytes;

        public FailedSaveRecoveryService() : this(null, File.ReadAllBytes) { }

        // Per-instance test seams; production always uses RED's local application data.
        internal FailedSaveRecoveryService(string? recoveryRoot, Func<string, byte[]>? readAllBytes = null)
        {
            _recoveryRoot = recoveryRoot;
            _readAllBytes = readAllBytes ?? File.ReadAllBytes;
        }

        public string Preserve(string candidateJson)
        {
            JObject.Parse(candidateJson);
            string root = Path.GetFullPath(_recoveryRoot ?? Path.Combine(AppIdentity.LocalAppDataPath, "Recovery"));
            Directory.CreateDirectory(root);
            // No address/filename in the name, bounded length, never overwrite an earlier snapshot.
            string path = Path.Combine(root, $"failed-save-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.ins");
            string temp = path + ".tmp";
            byte[] bytes = new UTF8Encoding(false).GetBytes(candidateJson);
            // Repeated focus/retry events must not duplicate an unchanged full photo report.
            // Only reuse after a fresh byte verification. Missing/changed snapshots are never overwritten.
            if (_lastVerifiedPath != null)
            {
                try
                {
                    if (_readAllBytes(_lastVerifiedPath).SequenceEqual(bytes))
                        return _lastVerifiedPath;
                }
                catch { /* Preserve a new independent snapshot if the old one cannot be verified. */ }
            }
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }
                if (!_readAllBytes(temp).SequenceEqual(bytes))
                    throw new IOException("Local recovery temporary file verification failed.");
                File.Move(temp, path); // Same-directory atomic publish, no Replace or overwrite fallback.
                if (!_readAllBytes(path).SequenceEqual(bytes))
                    throw new IOException("Local recovery snapshot verification failed.");
                _lastVerifiedPath = path;
                return path;
            }
            finally
            {
                // An unverified final snapshot is not advertised as a successful recovery.
                // Do not delete it: it may still be useful for manual support recovery.
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
    }
}
