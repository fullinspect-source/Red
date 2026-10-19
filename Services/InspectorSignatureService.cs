using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace InspectionEditor.Services
{
    // F-011 saved signature (account level, G1-G2). RED's own store keeps a lossless PNG master and the JPEG; saving is
    // immediate and atomic. Until the inspector captures one in RED, the signature F-003 imported from INSPECT's
    // FormConfig (InspectorProfile.Signature) is used. INSPECT's config is never written.
    public sealed class InspectorSignatureStore
    {
        public const string NoSavedSignatureQuestion = "You do not have a saved signature. Add one now?";
        public const string UnreadableSavedSignature = "The saved signature could not be read. Add a new one in Settings.";
        public const int SavedWidth = 770, SavedHeight = 91;   // G3 capture canvas
        public const int PadWidth = 770, PadHeight = 105;      // P2 footer pad

        private readonly string _root;
        private readonly Func<string?> _importedSignature;
        private readonly Func<string?> _inspector;

        public InspectorSignatureStore(string? root = null, Func<string?>? importedSignature = null, Func<string?>? inspector = null)
        {
            _root = root ?? Path.Combine(AppIdentity.LocalAppDataPath, "Signature");
            _importedSignature = importedSignature ?? (() => InspectorProfileService.Load()?.Signature);
            _inspector = inspector ?? (() => InspectorProfileService.Load()?.InspectorName);
        }

        public string JpegPath => Path.Combine(_root, "saved-signature.jpg");
        public string PngPath => Path.Combine(_root, "saved-signature.png");
        // P8: a capture belongs to the inspector who made it. The owner file holds a hash of the profile name only.
        public string OwnerPath => Path.Combine(_root, "saved-signature.owner");

        private string OwnerKey() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes((_inspector() ?? "").Trim().ToUpperInvariant())));

        // RED's capture counts only for the inspector who saved it; after another inspector's profile import RED falls
        // back to that inspector's imported signature.
        public bool HasRedCapture
        {
            get
            {
                try { return File.Exists(JpegPath) && File.Exists(OwnerPath) && File.ReadAllText(OwnerPath).Trim() == OwnerKey(); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return File.Exists(JpegPath); }
            }
        }

        // The saved signature's JPEG bytes: RED's capture, else the imported one; null when there is none. A capture
        // that cannot be read counts as present but unreadable (E1).
        public byte[]? LoadJpeg()
        {
            if (HasRedCapture)
            {
                try { return File.ReadAllBytes(JpegPath); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return Array.Empty<byte>(); }
            }
            string? imported = _importedSignature();
            if (string.IsNullOrWhiteSpace(imported) || imported.Trim().Equals("None", StringComparison.OrdinalIgnoreCase))
                return null;
            try { return Convert.FromBase64String(imported.Trim()); }
            catch (FormatException) { return Array.Empty<byte>(); } // present but unreadable (E1)
        }

        // A present but unreadable signature still counts as saved; drawing it then reports E1.
        public bool HasSaved => LoadJpeg() != null;

        // Decodes the saved signature for drawing; throws InvalidDataException (E1) when it is not a readable JPEG.
        public BitmapSource? LoadImage()
        {
            byte[]? bytes = LoadJpeg();
            if (bytes == null)
                return null;
            if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
                throw new InvalidDataException(UnreadableSavedSignature);
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(bytes);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new InvalidDataException(UnreadableSavedSignature, ex);
            }
        }

        // G2: write both files to temps of their own, flush, then replace, so a crash never leaves half a signature.
        public void Save(byte[] png, byte[] jpeg)
        {
            Directory.CreateDirectory(_root);
            WriteAtomic(PngPath, png);
            WriteAtomic(JpegPath, jpeg);
            WriteAtomic(OwnerPath, System.Text.Encoding.UTF8.GetBytes(OwnerKey()));
        }

        private static void WriteAtomic(string path, byte[] bytes)
        {
            string temp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            }
        }
    }
}
