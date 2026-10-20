using System;
using System.IO;
using RedCamera.Core;

namespace InspectionEditor.Services.Camera
{
    /// <summary>
    /// F-027 C9 (owner Q-59 default): each still from RED's capture screen is also kept, as captured, in the camera roll as
    /// <c>RED_yyyyMMdd_HHmmss_fff.jpg</c>, as the Windows Camera app does. Dev builds never write the real Pictures folder:
    /// they use <c>RED_DEV_CAMERA_ROLL</c>, else a CameraRoll folder in the dev app-data folder.
    /// </summary>
    public static class CameraRollCopy
    {
        public const string DevFolderVariable = "RED_DEV_CAMERA_ROLL";

        public static string TargetFolder()
        {
            if (AppIdentity.IsDevBuild)
            {
                string? redirect = Environment.GetEnvironmentVariable(DevFolderVariable);
                return string.IsNullOrWhiteSpace(redirect)
                    ? Path.Combine(AppIdentity.LocalAppDataPath, "CameraRoll")
                    : redirect;
            }
            // INSPECT's resolved camera roll (never a new folder inside it); RED's own Pictures\Camera Roll only when
            // nothing resolves, as CameraService does.
            return CameraFiles.CameraRollFolderForSaving()
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Camera Roll");
        }

        /// <summary>Writes the copy; returns its path, or null (logged) when it could not be written.</summary>
        public static string? TrySave(byte[] jpeg, DateTime now, string? folder = null)
        {
            try
            {
                return CameraFiles.SaveCopy(jpeg, folder ?? TargetFolder(), now);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                DiagnosticLogService.Info($"Camera: camera roll copy not written ({ex.GetType().Name}).");
                return null;
            }
        }
    }
}
