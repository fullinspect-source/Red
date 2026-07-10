using System;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace InspectionEditor
{
    internal static class AppIdentity
    {
        public static string Version => GetAppVersion();
        public static string VersionDisplay => $"v{Version}";
        public static DateTime PublishedDate => new DateTime(2026, 7, 10);
        public static string PublishedDateText => PublishedDate.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        public static string VersionWithPublishedDate => $"{VersionDisplay} - Published {PublishedDateText}";
        public static bool IsDevBuild => Assembly.GetExecutingAssembly().GetName().Name?.Contains("Dev", StringComparison.OrdinalIgnoreCase) == true;
        public static string DisplayName => "RED";
        public static string WindowTitle => IsDevBuild
            ? $"RED 2.0 Dev {VersionDisplay} - The Inspection Editor"
            : $"RED {VersionDisplay} - The Inspection Editor";
        public static string MyListWindowTitle => IsDevBuild
            ? $"RED 2.0 Dev {VersionDisplay} - My List"
            : $"RED {VersionDisplay} - My List";
        public static string AppDataFolderName => IsDevBuild ? "RED-2.0-Dev" : "RED";
        public static string LegacyAppDataFolderName => "InspectionEditor";

        public static string LocalAppDataPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppDataFolderName);

        private static string GetAppVersion()
        {
            string? version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (string.IsNullOrWhiteSpace(version))
            {
                version = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
            }

            return (version ?? "0.0.0").Split('+')[0];
        }
    }
}
