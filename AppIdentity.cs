using System;
using System.IO;
using System.Reflection;

namespace InspectionEditor
{
    internal static class AppIdentity
    {
        public static string Version => GetAppVersion();
        public static bool IsDevBuild => Assembly.GetExecutingAssembly().GetName().Name?.Contains("Dev", StringComparison.OrdinalIgnoreCase) == true;
        public static string DisplayName => "RED";
        public static string WindowTitle => IsDevBuild
            ? $"RED 2.0 Dev - The Inspection Editor - v{Version}"
            : $"RED - The Inspection Editor - v{Version}";
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
