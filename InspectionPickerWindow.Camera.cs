using System.Windows;
using InspectionEditor.Services.Camera;

namespace InspectionEditor
{
    /// <summary>F-027 C1: the Settings panel's Camera card (RED camera or the Windows Camera app), per device.</summary>
    public partial class InspectionPickerWindow
    {
        private bool _loadingCameraMode;

        private void CameraSettingsCard_Loaded(object sender, RoutedEventArgs e)
        {
            _loadingCameraMode = true;
            try
            {
                bool windowsApp = new CameraSettingsService().UsesWindowsCameraApp;
                WindowsCameraAppRadioButton.IsChecked = windowsApp;
                RedCameraRadioButton.IsChecked = !windowsApp;
            }
            finally
            {
                _loadingCameraMode = false;
            }
        }

        private void CameraModeRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            if (_loadingCameraMode) return;
            new CameraSettingsService().SetMode(windowsCameraApp: WindowsCameraAppRadioButton.IsChecked == true);
        }
    }
}
