using System.Windows;
using System.Windows.Controls;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // F-011 G3: Settings button "Add Signature" / "Change Signature" opening the "Signature" capture window.
    public partial class InspectionPickerWindow
    {
        private Button? _profileSignatureButton;
        private TextBlock? _profileSignatureText;
        private readonly InspectorSignatureStore _signatureStore = new();

        private void UpdateSignatureButton()
        {
            if (_profileSignatureButton == null)
            {
                if (ProfileOtherText.Parent is not Panel panel)
                    return;
                _profileSignatureButton = new Button { VerticalAlignment = VerticalAlignment.Top };
                if (TryFindResource("QuietButtonStyle") is Style style)
                    _profileSignatureButton.Style = style;
                _profileSignatureButton.Click += (_, _) =>
                {
                    var window = new SignatureCaptureWindow(_signatureStore) { Owner = this };
                    window.ShowDialog();
                    UpdateSignatureButton();
                };
                _profileSignatureText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
                var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
                row.Children.Add(_profileSignatureButton);
                row.Children.Add(_profileSignatureText);
                panel.Children.Insert(panel.Children.IndexOf(ProfileOtherText) + 1, row);
            }
            bool saved = _signatureStore.HasSaved;
            _profileSignatureButton.Content = saved ? "Change Signature" : "Add Signature";
            _profileSignatureText!.Text = saved
                ? (_signatureStore.HasRedCapture ? "Saved signature: captured in RED." : "Saved signature: imported from INSPECT.")
                : "No saved signature. \"Use Saved Signature\" in a report asks you to add one.";
        }
    }
}
