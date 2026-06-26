using System;
using System.Text;

namespace InspectionEditor
{
    internal static partial class EmbeddedApiKeyProvider
    {
        /// <summary>
        /// Returns the release-embedded AI key when a private generated partial class was included at build time.
        /// The generated partial is intentionally gitignored so the public source repo never contains the key material.
        /// </summary>
        public static string? Load()
        {
            string payload = GetObfuscatedPayload();
            string mask = GetObfuscationMask();

            if (string.IsNullOrWhiteSpace(payload) || string.IsNullOrWhiteSpace(mask))
                return null;

            try
            {
                byte[] encrypted = Convert.FromBase64String(payload);
                byte[] maskBytes = Convert.FromBase64String(mask);
                if (maskBytes.Length == 0)
                    return null;

                byte[] keyBytes = new byte[encrypted.Length];
                for (int i = 0; i < encrypted.Length; i++)
                {
                    keyBytes[i] = (byte)(encrypted[i] ^ maskBytes[i % maskBytes.Length] ^ 0x5A);
                }

                Array.Reverse(keyBytes);
                string key = Encoding.UTF8.GetString(keyBytes).Trim();
                return string.IsNullOrWhiteSpace(key) ? null : key;
            }
            catch
            {
                return null;
            }
        }

        static partial void GetGeneratedKeyParts(ref string payload, ref string mask);

        private static string GetObfuscatedPayload()
        {
            string payload = string.Empty;
            string mask = string.Empty;
            GetGeneratedKeyParts(ref payload, ref mask);
            return payload;
        }

        private static string GetObfuscationMask()
        {
            string payload = string.Empty;
            string mask = string.Empty;
            GetGeneratedKeyParts(ref payload, ref mask);
            return mask;
        }
    }
}
