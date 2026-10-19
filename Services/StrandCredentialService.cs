using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace InspectionEditor.Services
{
    public sealed class StrandCredentials
    {
        public string Username { get; init; } = "";
        public string Password { get; init; } = "";
        public string ApiUrl { get; init; } = "";
        // Where the login came from, for the Settings label: "RED", "Inspect 2026" or "Inspect 2022".
        public string Source { get; init; } = "";
        public bool IsComplete => Username.Length > 0 && Password.Length > 0;
    }

    // Strand login for ORDERS. RED keeps its own copy (DPAPI, current Windows user) and,
    // when it has none, imports the login saved in Inspect 2026 / Inspect 2022 settings.
    public static class StrandCredentialService
    {
        private static string AccountFilePath => Path.Combine(AppIdentity.LocalAppDataPath, "strand_account.json");
        private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("RED.StrandAccount.v1");

        private sealed class StoredAccount
        {
            public string Username { get; set; } = "";
            public string ProtectedPassword { get; set; } = "";
            public string ApiUrl { get; set; } = "";
            public string Source { get; set; } = "";
        }

        // RED's saved login first, then whatever Inspect has saved.
        public static StrandCredentials? GetEffective() => LoadSaved() ?? ImportFromInspect();

        public static StrandCredentials? LoadSaved()
        {
            try
            {
                if (!File.Exists(AccountFilePath))
                    return null;

                var stored = JsonConvert.DeserializeObject<StoredAccount>(File.ReadAllText(AccountFilePath));
                if (stored == null || string.IsNullOrWhiteSpace(stored.Username))
                    return null;

                // If the password can't be decrypted (e.g. after a Windows password reset), keep the
                // username so Inspect's login is only used for the same account.
                string password = "";
                if (!string.IsNullOrEmpty(stored.ProtectedPassword))
                {
                    try
                    {
                        byte[] clear = ProtectedData.Unprotect(Convert.FromBase64String(stored.ProtectedPassword), DpapiEntropy, DataProtectionScope.CurrentUser);
                        password = Encoding.UTF8.GetString(clear);
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLogService.Log("Could not decrypt the saved Strand password", ex);
                    }
                }

                return new StrandCredentials
                {
                    Username = stored.Username,
                    Password = password,
                    ApiUrl = stored.ApiUrl ?? "",
                    Source = string.IsNullOrWhiteSpace(stored.Source) ? "RED" : stored.Source
                };
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Log("Could not read the saved Strand login", ex);
                return null;
            }
        }

        public static void Save(StrandCredentials credentials)
        {
            byte[] protectedPassword = ProtectedData.Protect(Encoding.UTF8.GetBytes(credentials.Password), DpapiEntropy, DataProtectionScope.CurrentUser);
            var stored = new StoredAccount
            {
                Username = credentials.Username,
                ProtectedPassword = Convert.ToBase64String(protectedPassword),
                ApiUrl = credentials.ApiUrl,
                Source = credentials.Source
            };

            // Write then move, so a crash mid-save never leaves a truncated file. Each save has its own temp file, as two
            // RED processes may save at once.
            Directory.CreateDirectory(AppIdentity.LocalAppDataPath);
            string tempPath = $"{AccountFilePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(tempPath, JsonConvert.SerializeObject(stored, Formatting.Indented));
                File.Move(tempPath, AccountFilePath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch (IOException) { }
            }
        }

        public static void Clear()
        {
            try { File.Delete(AccountFilePath); }
            catch (Exception ex) { DiagnosticLogService.Log("Could not clear the saved Strand login", ex); }
        }

        // Reads the login from Inspect's settings file (the config.json in its Inspect folder).
        public static StrandCredentials? ImportFromInspect()
        {
            // Shared read access with retries (F-003 S3), so INSPECT's own writes are never blocked.
            foreach (var (configPath, source) in FindInspectConfigFiles())
            {
                if (TryReadInspectConfig(configPath, out var root) && LoginFromConfig(root!, source) is { } login)
                    return login;
            }

            return null;
        }

        // The login in one INSPECT FormConfig, or null when it has no username. A password that can't be decrypted
        // comes back blank.
        internal static StrandCredentials? LoginFromConfig(JObject root, string source)
        {
            string username = GetString(root, "Username");
            if (string.IsNullOrWhiteSpace(username))
                return null;

            string encrypted = GetString(root, "Password");
            string password = "";
            if (!string.IsNullOrEmpty(encrypted))
            {
                try { password = InspectCipher.Decrypt(encrypted); }
                catch (Exception ex) { DiagnosticLogService.Log($"Could not read the {source} password", ex); }
            }

            return new StrandCredentials
            {
                Username = username.Trim(),
                Password = password,
                ApiUrl = GetString(root, "Apps2ApiURL"),
                Source = source
            };
        }

        // "Inspect 2026" or "Inspect 2022", for labels.
        internal static string InspectSourceLabel() =>
            Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Strand.Inspect2026"))
                ? "Inspect 2026"
                : "Inspect 2022";

        // The Inspect folder (MyList, Outbox, Archive, Review live under it), as Inspect itself resolves it:
        // the folder named in INSPECT's UserConfig wins, whether or not it holds a config.json (F-003 S5), so its
        // config.json.bak is used before any other folder's settings (S4). Only without one does RED fall back to a
        // default Dropbox Inspections folder, preferring one that has INSPECT settings in it.
        internal static string? SelectInspectorFolder(string? configured, IReadOnlyList<string> defaults)
        {
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;
            return defaults.FirstOrDefault(folder => File.Exists(Path.Combine(folder, "config.json")) ||
                                                     File.Exists(Path.Combine(folder, "config.json.bak")))
                ?? defaults.FirstOrDefault(Directory.Exists);
        }

        // Non-secret settings from Inspect's config.json (e.g. URL, UserType, InspectorName). Never the password.
        public static string ReadInspectSetting(string name)
        {
            if (string.Equals(name, "Password", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Use ImportFromInspect for the login.", nameof(name));

            foreach (var (configPath, _) in FindInspectConfigFiles())
            {
                if (TryReadInspectConfig(configPath, out var root) && GetString(root!, name) is { Length: > 0 } value)
                    return value;
            }

            return "";
        }

        // INSPECT's FormConfig from {inspectorFolder}\config.json, then config.json.bak, then nothing, as INSPECT loads it
        // (F-003 S3-S5). Config is null when neither file can be read, parsed or even found (S4 "could not read").
        public static InspectConfigRead ReadInspectFormConfig(string? inspectorFolder)
        {
            if (string.IsNullOrWhiteSpace(inspectorFolder))
                return new InspectConfigRead(null, "", null);

            string configPath = Path.Combine(inspectorFolder, "config.json");
            string backupPath = configPath + ".bak";
            if (TryReadInspectConfig(configPath, out var config))
                return new InspectConfigRead(config, configPath, null);
            // TryReadInspectConfig has already logged why config.json could not be read.
            if (TryReadInspectConfig(backupPath, out var backup))
                return new InspectConfigRead(backup, backupPath, File.GetLastWriteTime(backupPath));
            return new InspectConfigRead(null, configPath, null);
        }

        // F-002 R4 retry delays, used for sharing violations (S3) and for a file caught half-written.
        private static readonly int[] InspectConfigRetryDelaysMs = { 100, 200, 400 };

        // Shared read/write access, as INSPECT opens its config. INSPECT rewrites the file in place on every launch,
        // so a sharing violation or a half-written file is retried briefly before giving up. Never writes.
        internal static bool TryReadInspectConfig(string path, out JObject? config)
        {
            config = null;
            if (!File.Exists(path))
                return false;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    config = JObject.Parse(reader.ReadToEnd());
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
                {
                    if (attempt >= InspectConfigRetryDelaysMs.Length || ex is FileNotFoundException or DirectoryNotFoundException)
                    {
                        DiagnosticLogService.Log($"Could not read {path}", ex);
                        return false;
                    }
                    System.Threading.Thread.Sleep(InspectConfigRetryDelaysMs[attempt]);
                }
            }
        }

        // Inspect 2026 records its Inspect folder in %LocalAppData%\Strand\Inspect\config.json (then its .bak); both
        // versions keep the account in config.json inside that folder (usually Dropbox\Inspections).
        private static IEnumerable<string> CandidateInspectorFolders()
        {
            if (ConfiguredInspectorFolder() is { } configured)
                yield return configured;
            foreach (string folder in DefaultInspectorFolders())
                yield return folder;
        }

        // INSPECT 2026's per-machine UserConfig (F-003 S1).
        internal static string UserConfigPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Strand", "Inspect", "config.json");

        // UserConfig, then its .bak; null when neither can be read.
        internal static JObject? ReadInspectUserConfig(string userConfigPath) =>
            TryReadInspectConfig(userConfigPath, out var userSettings) || TryReadInspectConfig(userConfigPath + ".bak", out userSettings)
                ? userSettings
                : null;

        private static string? ConfiguredInspectorFolder() =>
            ReadInspectUserConfig(UserConfigPath) is { } userSettings && GetString(userSettings, "InspectorFolder") is { Length: > 0 } inspectorFolder
                ? inspectorFolder
                : null;

        internal static IReadOnlyList<string> DefaultInspectorFolders()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new[]
            {
                Path.Combine(userProfile, "Dropbox", "Inspections"),
                Path.Combine(userProfile, "Dropbox (Personal)", "Inspections"),
                Path.Combine(userProfile, "Dropbox (Strand Systems)", "Inspections"),
            };
        }

        private static IEnumerable<(string Path, string Source)> FindInspectConfigFiles()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string source = InspectSourceLabel();

            foreach (string folder in CandidateInspectorFolders())
            {
                string path = Path.Combine(folder, "config.json");
                if (seen.Add(path) && File.Exists(path))
                    yield return (path, source);
            }
        }

        internal static string GetString(JObject root, string name) =>
            root.GetValue(name, StringComparison.OrdinalIgnoreCase)?.Type == JTokenType.String
                ? root.GetValue(name, StringComparison.OrdinalIgnoreCase)!.Value<string>()?.Trim() ?? ""
                : "";

    }

    // Inspect's encrypted-string format (stored password, *_Secure fee fields): base64 of
    // [int32 IV length][IV][AES-256-CBC ciphertext], key = PBKDF2-SHA1(1000 iterations)
    // over Inspect's built-in secret and salt. Text is UTF-8 without a BOM.
    // The secret and salt come from the private, gitignored InspectCipherSecrets.Generated.cs (like the AI key in
    // EmbeddedApiKeyProvider), so the public repository never contains them. Release builds require that file.
    internal static partial class InspectCipher
    {
        private static readonly string SharedSecret;
        private static readonly byte[] Salt;

        static InspectCipher()
        {
            string secret = "", salt = "";
            GetGeneratedSecret(ref secret, ref salt);
            SharedSecret = secret;
            Salt = Encoding.ASCII.GetBytes(salt);
        }

        static partial void GetGeneratedSecret(ref string secret, ref string salt);

        public static bool IsAvailable => SharedSecret.Length > 0 && Salt.Length > 0;

        private static void EnsureAvailable()
        {
            if (!IsAvailable)
                throw new InvalidOperationException("This RED build does not include Inspect's encryption settings.");
        }

        public static string Encrypt(string plainText)
        {
            EnsureAvailable();
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = Rfc2898DeriveBytes.Pbkdf2(SharedSecret, Salt, 1000, HashAlgorithmName.SHA1, 32);
            aes.GenerateIV();

            using var output = new MemoryStream();
            output.Write(BitConverter.GetBytes(aes.IV.Length), 0, sizeof(int));
            output.Write(aes.IV, 0, aes.IV.Length);
            using (var encryptor = aes.CreateEncryptor())
            using (var crypto = new CryptoStream(output, encryptor, CryptoStreamMode.Write, leaveOpen: true))
            using (var writer = new StreamWriter(crypto, new UTF8Encoding(false)))
            {
                writer.Write(plainText);
            }
            return Convert.ToBase64String(output.ToArray());
        }

        public static string Decrypt(string cipherText)
        {
            EnsureAvailable();
            byte[] data = Convert.FromBase64String(cipherText);
            using var stream = new MemoryStream(data);
            byte[] iv = ReadByteArray(stream);

            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = Rfc2898DeriveBytes.Pbkdf2(SharedSecret, Salt, 1000, HashAlgorithmName.SHA1, 32);
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            using var crypto = new CryptoStream(stream, decryptor, CryptoStreamMode.Read);
            using var reader = new StreamReader(crypto);
            return reader.ReadToEnd();
        }

        private static byte[] ReadByteArray(Stream stream)
        {
            byte[] lengthBytes = new byte[sizeof(int)];
            if (stream.Read(lengthBytes, 0, lengthBytes.Length) != lengthBytes.Length)
                throw new InvalidDataException("Inspect password is not in the expected format.");

            int length = BitConverter.ToInt32(lengthBytes, 0);
            if (length <= 0 || length > 64)
                throw new InvalidDataException("Inspect password is not in the expected format.");

            byte[] buffer = new byte[length];
            if (stream.Read(buffer, 0, length) != length)
                throw new InvalidDataException("Inspect password is not in the expected format.");
            return buffer;
        }
    }
}
