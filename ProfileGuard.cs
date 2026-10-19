using System.Windows;
using System.Windows.Interop;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // F-003 O9: the one call Submit (F-013), Approve (F-018) and every Add (F-001, F-021, F-025) make before they act.
    // It returns the complete profile, or refuses with "Complete your inspector profile in RED Settings first." and opens
    // Settings. Editing is never blocked.
    internal static class ProfileGuard
    {
        public static CompleteProfile? Require(Window owner, string caption)
        {
            if (InspectorProfileService.RequireComplete() is { } profile)
                return profile;
            MessageBox.Show(owner, InspectUserType.ProfileIncompleteMessage, caption, MessageBoxButton.OK, MessageBoxImage.Warning);
            OpenSettings(owner);
            return null;
        }

        private static void OpenSettings(Window owner)
        {
            switch (owner)
            {
                case InspectionPickerWindow picker:
                    picker.OpenSettingsTab();
                    break;
                case { Owner: { } parent } when ComponentDispatcher.IsThreadModal:
                    // A dialog: Settings opens as it closes, so what was typed in it is kept.
                    owner.Closed += (_, _) => OpenSettings(parent);
                    break;
                default:
                    App.ShowHomeSettings();
                    break;
            }
        }
    }
}
