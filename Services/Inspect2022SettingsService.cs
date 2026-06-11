using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;

namespace InspectionEditor.Services
{
    public sealed class Inspect2022Settings
    {
        public string InspectorName { get; init; } = "";
        public string InspectorPhone { get; init; } = "";
        public string SignatureBase64 { get; init; } = "";
    }

    public static class Inspect2022SettingsService
    {
        public static Inspect2022Settings LoadNearInspectionFile(string? inspectionFilePath)
        {
            string? inspectionsRoot = FindInspectionsRoot(inspectionFilePath);
            if (string.IsNullOrWhiteSpace(inspectionsRoot))
                return new Inspect2022Settings();

            return LoadFromConfig(Path.Combine(inspectionsRoot, "config.json"));
        }

        private static Inspect2022Settings LoadFromConfig(string configPath)
        {
            try
            {
                if (!File.Exists(configPath))
                    return new Inspect2022Settings();

                var root = JObject.Parse(File.ReadAllText(configPath));
                string inspectorName = FirstString(root, "InspectorName", "User.DisplayName");
                string inspectorPhone = FirstString(root, "Phone", "User.Phone");
                string signature = FirstString(root, "Signature");

                if (!LooksLikeJpegBase64(signature))
                    signature = "";

                return new Inspect2022Settings
                {
                    InspectorName = inspectorName,
                    InspectorPhone = inspectorPhone,
                    SignatureBase64 = signature
                };
            }
            catch
            {
                return new Inspect2022Settings();
            }
        }

        private static string? FindInspectionsRoot(string? inspectionFilePath)
        {
            if (!string.IsNullOrWhiteSpace(inspectionFilePath))
            {
                var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(inspectionFilePath)) ?? "");
                while (dir != null)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "config.json")) &&
                        Directory.Exists(Path.Combine(dir.FullName, "Data")))
                    {
                        return dir.FullName;
                    }

                    dir = dir.Parent;
                }
            }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] candidates =
            {
                Path.Combine(userProfile, "Dropbox", "Inspections"),
                Path.Combine(userProfile, "Dropbox (Personal)", "Inspections"),
                Path.Combine(userProfile, "Dropbox (Strand Systems)", "Inspections")
            };

            return candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "config.json")));
        }

        private static string FirstString(JObject root, params string[] paths)
        {
            foreach (string path in paths)
            {
                string value = root.SelectToken(path)?.Value<string>()?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return "";
        }

        private static bool LooksLikeJpegBase64(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            try
            {
                byte[] bytes = Convert.FromBase64String(value);
                return bytes.Length > 4 &&
                       bytes[0] == 0xFF &&
                       bytes[1] == 0xD8 &&
                       bytes[2] == 0xFF;
            }
            catch
            {
                return false;
            }
        }
    }
}
