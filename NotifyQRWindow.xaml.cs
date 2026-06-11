using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;

namespace InspectionEditor
{
    public partial class NotifyQRWindow : Window
    {
        internal static string CustomMessageFilePath => Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
            "notify_custom_message.txt");

        private string _phoneDigits = "";
        private string _customPrefix = "";

        public NotifyQRWindow(InspectionFileInfo inspection)
        {
            InitializeComponent();
            BuildNotification(inspection);
        }

        private void BuildNotification(InspectionFileInfo inspection)
        {
            _phoneDigits = Regex.Replace(inspection.ContactPhone, @"[^\d]", "");

            string contactFirstName = GetFirstName(inspection.ContactName);
            string inspectorName = string.IsNullOrWhiteSpace(inspection.InspectorName)
                ? "Your inspector"
                : inspection.InspectorName;

            string lotPart = string.IsNullOrWhiteSpace(inspection.LotNumber)
                ? ""
                : $", (lot {inspection.LotNumber})";

            // Use friendly alias name instead of raw code
            string typeName = inspection.InspectionTypeName;
            string typePart = string.IsNullOrWhiteSpace(typeName)
                ? ""
                : $" the {typeName}";

            // ── Standard message ────────────────────────────────────────
            string standardMessage = $"Hey {contactFirstName}, I anticipate that I'll be at {inspection.Address}{lotPart} in about 20 minutes if you want to attend{typePart}. (no need to reply.) ~ {inspectorName}";
            StandardQRImage.Source = GenerateQRBitmap($"sms:{_phoneDigits}?body={Uri.EscapeDataString(standardMessage)}");
            MessagePreview.Text = standardMessage;

            // ── Custom message ───────────────────────────────────────────
            // Prefix is read-only; user appends their own note
            _customPrefix = $"RE: {typeName}, at {inspection.Address}: ";
            CustomMessagePrefix.Text = _customPrefix;

            // Load saved custom note, or default to blank
            string savedNote = LoadSavedCustomMessage();
            CustomMessageBox.Text = savedNote;

            // Contact info footer
            ContactInfo.Text = $"{inspection.ContactName}  ·  {inspection.ContactPhone}";
        }

        // Called on every keystroke — live-refreshes the custom QR
        private void CustomMessageBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            RefreshCustomQR();
            SavedIndicator.Visibility = Visibility.Collapsed;
        }

        private void RefreshCustomQR()
        {
            string body = _customPrefix + (CustomMessageBox.Text ?? "");
            CustomQRImage.Source = GenerateQRBitmap($"sms:{_phoneDigits}?body={Uri.EscapeDataString(body)}");
        }

        private void SaveCustomMessage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                File.WriteAllText(CustomMessageFilePath, CustomMessageBox.Text ?? "");
                SavedIndicator.Visibility = Visibility.Visible;
                // QR is already current (live updates on keystrokes)
            }
            catch { /* non-fatal */ }
        }

        private void ClearCustomMessage_Click(object sender, RoutedEventArgs e)
        {
            CustomMessageBox.Text = "";
            SavedIndicator.Visibility = Visibility.Collapsed;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ── Helpers ─────────────────────────────────────────────────────

        private static BitmapImage GenerateQRBitmap(string content)
        {
            using var qrGenerator = new QRCodeGenerator();
            var qrData = qrGenerator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
            using var qrCode = new PngByteQRCode(qrData);
            byte[] bytes = qrCode.GetGraphic(10);
            using var ms = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        internal static string LoadSavedCustomMessage()
        {
            try
            {
                if (File.Exists(CustomMessageFilePath))
                    return File.ReadAllText(CustomMessageFilePath);
            }
            catch { }
            return "";
        }

        internal static void SaveCustomMessage(string message)
        {
            File.WriteAllText(CustomMessageFilePath, message ?? "");
        }

        private static string GetFirstName(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return "there";
            var parts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : "there";
        }
    }
}
