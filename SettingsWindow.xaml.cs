using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace InspectionEditor
{
    public partial class SettingsWindow : Window
    {
        // Use exe's actual location (not temp extraction folder for single-file publish)
        private static readonly string SETTINGS_FILE = Path.Combine(
            Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppDomain.CurrentDomain.BaseDirectory,
            "settings.txt");
        private static readonly string APP_SETTINGS_FILE = Path.Combine(
            AppIdentity.LocalAppDataPath,
            "red_app_settings.json");

        public string? ApiKey { get; private set; }

        public SettingsWindow()
        {
            InitializeComponent();
            LoadApiKey();
            SuppressAdminToolsCheckBox.IsChecked = LoadSuppressAdministrativeTools();
            SelectDefaultAiTone(LoadDefaultAiTone());
        }

        private void LoadApiKey()
        {
            try
            {
                if (File.Exists(SETTINGS_FILE))
                {
                    ApiKeyTextBox.Text = File.ReadAllText(SETTINGS_FILE).Trim();
                }
            }
            catch
            {
                // Ignore errors loading settings
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            string apiKey = ApiKeyTextBox.Text.Trim();

            try
            {
                File.WriteAllText(SETTINGS_FILE, apiKey);
                SaveAppSettings(new RedAppSettings
                {
                    SuppressAdministrativeTools = SuppressAdminToolsCheckBox.IsChecked != false,
                    DefaultAiTone = GetSelectedDefaultAiTone()
                });
                ApiKey = apiKey;
                DialogResult = true;
                Close();
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Error saving API key: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        public static string? LoadApiKeyFromFile()
        {
            // Try multiple locations
            string[] searchPaths = new[]
            {
                SETTINGS_FILE,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.txt"),
                "settings.txt"
            };

            foreach (var path in searchPaths)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        var key = File.ReadAllText(path).Trim();
                        if (!string.IsNullOrEmpty(key) && !key.StartsWith("xai-", StringComparison.OrdinalIgnoreCase))
                            return key;
                    }
                }
                catch { }
            }

            return null;
        }

        public static bool LoadSuppressAdministrativeTools()
        {
            try
            {
                if (File.Exists(APP_SETTINGS_FILE))
                {
                    var settings = JsonSerializer.Deserialize<RedAppSettings>(File.ReadAllText(APP_SETTINGS_FILE));
                    return settings?.SuppressAdministrativeTools ?? true;
                }
            }
            catch { }

            return true;
        }

        public static string LoadDefaultAiTone()
        {
            try
            {
                if (File.Exists(APP_SETTINGS_FILE))
                {
                    var settings = JsonSerializer.Deserialize<RedAppSettings>(File.ReadAllText(APP_SETTINGS_FILE));
                    return NormalizeAiTone(settings?.DefaultAiTone);
                }
            }
            catch { }

            return "Technical";
        }

        private string GetSelectedDefaultAiTone()
        {
            if (DefaultAiToneComboBox?.SelectedItem is ComboBoxItem item)
                return NormalizeAiTone(item.Content?.ToString());

            return "Technical";
        }

        private void SelectDefaultAiTone(string tone)
        {
            string normalized = NormalizeAiTone(tone);
            for (int i = 0; i < DefaultAiToneComboBox.Items.Count; i++)
            {
                if (DefaultAiToneComboBox.Items[i] is ComboBoxItem item &&
                    string.Equals(item.Content?.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    DefaultAiToneComboBox.SelectedIndex = i;
                    return;
                }
            }

            DefaultAiToneComboBox.SelectedIndex = 0;
        }

        private static string NormalizeAiTone(string? tone)
        {
            return (tone ?? "").Trim().ToLowerInvariant() switch
            {
                "nice" => "Nice",
                "strict" => "Strict",
                _ => "Technical"
            };
        }

        private static void SaveAppSettings(RedAppSettings settings)
        {
            Directory.CreateDirectory(AppIdentity.LocalAppDataPath);
            File.WriteAllText(APP_SETTINGS_FILE, JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }

        private sealed class RedAppSettings
        {
            public bool SuppressAdministrativeTools { get; set; } = true;
            public string DefaultAiTone { get; set; } = "Technical";
        }
    }
}
