using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace InspectionEditor.Services
{
    // RED's own copy of the inspector profile and the other INSPECT settings (F-003 §3). The Strand username and
    // password stay in StrandCredentialService (DPAPI); everything else is in %LOCALAPPDATA%\RED\inspector_profile.json,
    // apart from red_app_settings.json, which Settings rewrites whole and Reset RED Preferences deletes.
    public sealed class InspectorProfile
    {
        public string InspectorName { get; set; } = "";
        public string Phone { get; set; } = "";
        // Exactly as INSPECT stores it, untrimmed; RED never edits it (owner Q-22, F-003 O10).
        public string? UserType { get; set; }
        public string DefaultState { get; set; } = "";
        public string InspectionsFolder { get; set; } = "";
        // Imported for later features: F-004 number pad, F-017 PDF editor, F-011 signature, report viewer, sketches.
        public bool UseNumberPad { get; set; }
        public string PdfAppProgId { get; set; } = "";
        public string Signature { get; set; } = "";
        public bool ReportViewerDefectsOnly { get; set; }
        public bool ReportViewerPictures { get; set; } = true;
        public bool ReportViewerAttachments { get; set; } = true;
        public string SketchPenColor { get; set; } = "Black";
        public string ImportedFrom { get; set; } = "";
        public DateTime? ImportedAt { get; set; }

        public InspectorProfile Clone() => (InspectorProfile)MemberwiseClone();
    }

    // One read of INSPECT's settings for an import. Profile is null when INSPECT's FormConfig could not be read.
    public sealed class InspectSettingsRead
    {
        public InspectorProfile? Profile { get; init; }
        public StrandCredentials? Login { get; init; }
        public DateTime? BackupDate { get; init; }
        // The S4 backup or could-not-read message, when there is one.
        public string? Message { get; init; }
    }

    public sealed record ProfileChange(string Label, string Old, string New)
    {
        public override string ToString() => $"{Label}: {Old} -> {New}";
    }

    // A profile that passed the F-003 O9 guard: what Submit and Add need to file and stamp an inspection (O6).
    public sealed record CompleteProfile(string Folder, InspectUserKind Kind, string InspectorName, string Username, string Phone, string DefaultState);

    public static class InspectorProfileService
    {
        public const string MissingProfileMessage = "Please insert missing profile information before anything else.";
        public const string CompleteMessage = "Profile complete. My List and ORDERS are unlocked.";

        public static string ProfilePath => Path.Combine(AppIdentity.LocalAppDataPath, "inspector_profile.json");

        // INSPECT's StateList: the 50 states and DC.
        public static readonly IReadOnlyList<string> States = new[]
        {
            "AK", "AL", "AR", "AZ", "CA", "CO", "CT", "DC", "DE", "FL", "GA", "HI", "IA", "ID", "IL", "IN", "KS", "KY",
            "LA", "MA", "MD", "ME", "MI", "MN", "MO", "MS", "MT", "NC", "ND", "NE", "NH", "NJ", "NM", "NV", "NY", "OH",
            "OK", "OR", "PA", "RI", "SC", "SD", "TN", "TX", "UT", "VA", "VT", "WA", "WI", "WV", "WY"
        };

        // The message from the last automatic first-run import, for Settings to show once.
        public static string? FirstRunMessage { get; private set; }

        public static InspectorProfile? Load() => Load(ProfilePath);

        internal static InspectorProfile? Load(string path)
        {
            try
            {
                return File.Exists(path) ? JsonConvert.DeserializeObject<InspectorProfile>(File.ReadAllText(path)) : null;
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Log($"Could not read the inspector profile {path}", ex);
                return null;
            }
        }

        public static void Save(InspectorProfile profile) => Save(profile, ProfilePath);

        // Atomic: write and flush a temp file of its own (two RED processes may save at once), then replace (O3).
        internal static void Save(InspectorProfile profile, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(JsonConvert.SerializeObject(profile, Formatting.Indented));
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }
                File.Move(tempPath, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch (IOException) { }
            }
        }

        // Reads every value RED uses from INSPECT's files (S1 UserConfig, S2 FormConfig), read only. FormConfig comes from
        // RED's selected Inspections folder when that folder has INSPECT settings (S5, A-003-09); otherwise from the folder
        // INSPECT's UserConfig names, and the changes list then shows the folder change.
        public static InspectSettingsRead ReadFromInspect(string? selectedFolder = null) =>
            ReadFromInspect(StrandCredentialService.UserConfigPath, StrandCredentialService.DefaultInspectorFolders(),
                            StrandCredentialService.InspectSourceLabel(), selectedFolder);

        internal static InspectSettingsRead ReadFromInspect(string userConfigPath, IReadOnlyList<string> defaultFolders, string sourceLabel,
                                                            string? selectedFolder = null)
        {
            JObject? user = StrandCredentialService.ReadInspectUserConfig(userConfigPath);
            string configured = user == null ? "" : StrandCredentialService.GetString(user, "InspectorFolder");
            string? folder = HasInspectSettings(selectedFolder)
                ? selectedFolder!.Trim()
                : StrandCredentialService.SelectInspectorFolder(configured, defaultFolders);
            var read = StrandCredentialService.ReadInspectFormConfig(folder);
            if (read.Config is not { } form)
                return new InspectSettingsRead { Message = InspectUserType.UnreadableMessage };

            var profile = new InspectorProfile
            {
                InspectorName = StrandCredentialService.GetString(form, "InspectorName"),
                Phone = StrandCredentialService.GetString(form, "Phone"),
                UserType = InspectUserType.Resolve(form),
                // A missing key keeps INSPECT's FormConfig default (TX); a null or blank value stays blank.
                DefaultState = form.Property("DefaultState", StringComparison.OrdinalIgnoreCase) == null
                    ? "TX"
                    : StrandCredentialService.GetString(form, "DefaultState").ToUpperInvariant(),
                InspectionsFolder = folder!,
                UseNumberPad = GetBool(form, "UseNumberPad", false),
                PdfAppProgId = StrandCredentialService.GetString(form, "PdfAppProgID"),
                Signature = StrandCredentialService.GetString(form, "Signature"),
                ReportViewerDefectsOnly = GetBool(user, "ReportViewer_DefectsOnly", false),
                ReportViewerPictures = GetBool(user, "ReportViewer_Pictures", true),
                ReportViewerAttachments = GetBool(user, "ReportViewer_Attachments", true),
                SketchPenColor = (user == null ? "" : StrandCredentialService.GetString(user, "SketchPenColor")) switch
                {
                    "Red" => "Red",
                    "Blue" => "Blue",
                    "Green" => "Green",
                    _ => "Black"
                },
                ImportedFrom = read.Path,
            };
            return new InspectSettingsRead
            {
                Profile = profile,
                Login = StrandCredentialService.LoginFromConfig(form, sourceLabel),
                BackupDate = read.BackupDate,
                Message = read.BackupDate is { } date ? InspectUserType.BackupMessage(date) : null
            };
        }

        // Each value an import would change (O2), as "Label: old -> new". Passwords and signatures are never shown.
        public static List<ProfileChange> Diff(InspectorProfile current, StrandCredentials? currentLogin, InspectSettingsRead incoming)
        {
            var changes = new List<ProfileChange>();
            if (incoming.Profile is not { } next)
                return changes;

            void Text(string label, string? oldValue, string? newValue, StringComparison comparison = StringComparison.Ordinal)
            {
                if (!string.Equals(oldValue ?? "", newValue ?? "", comparison))
                    changes.Add(new ProfileChange(label, Show(oldValue), Show(newValue)));
            }
            void Flag(string label, bool oldValue, bool newValue)
            {
                if (oldValue != newValue)
                    changes.Add(new ProfileChange(label, oldValue ? "Yes" : "No", newValue ? "Yes" : "No"));
            }

            Text("Full name", current.InspectorName, next.InspectorName);
            Text("Phone", current.Phone, next.Phone);
            Text("User type", current.UserType, next.UserType);
            Text("Default state", current.DefaultState, next.DefaultState);
            Text("Inspections folder", current.InspectionsFolder, next.InspectionsFolder, StringComparison.OrdinalIgnoreCase);
            Flag("Use number pad", current.UseNumberPad, next.UseNumberPad);
            Text("PDF editor", PdfName(current.PdfAppProgId), PdfName(next.PdfAppProgId));
            if (current.Signature != next.Signature)
                changes.Add(new ProfileChange("Signature", current.Signature.Length > 0 ? "saved" : "none", next.Signature.Length > 0 ? "INSPECT's signature" : "none"));
            Flag("Report viewer: defects only", current.ReportViewerDefectsOnly, next.ReportViewerDefectsOnly);
            Flag("Report viewer: pictures", current.ReportViewerPictures, next.ReportViewerPictures);
            Flag("Report viewer: attachments", current.ReportViewerAttachments, next.ReportViewerAttachments);
            Text("Sketch pen colour", current.SketchPenColor, next.SketchPenColor);

            if (incoming.Login is { IsComplete: true } login)
            {
                Text("Strand username", currentLogin?.Username, login.Username, StringComparison.OrdinalIgnoreCase);
                if (currentLogin == null || currentLogin.Password != login.Password)
                    changes.Add(new ProfileChange("Strand password", currentLogin?.Password.Length > 0 ? "saved" : "none", "INSPECT's password"));
            }
            return changes;
        }

        // Saves an import as RED's copy (O2), then the Strand login when INSPECT has a complete one. Throws if the profile
        // can't be saved; returns a message (and keeps the saved profile) if only the login can't be saved.
        public static string? Apply(InspectSettingsRead incoming)
        {
            if (incoming.Profile is not { } next)
                return null;
            var profile = next.Clone();
            profile.ImportedAt = DateTime.Now;
            Save(profile);
            return incoming.Login is { IsComplete: true } login ? SaveLogin(login) : null;
        }

        // O1: the first time RED runs (no profile yet), import INSPECT's values once. A login already saved in RED is
        // kept, unless its password no longer decrypts and INSPECT has the same account. Nothing is created when
        // INSPECT's settings can't be read; the user can import or type them later.
        public static void EnsureImportedOnFirstRun()
        {
            if (File.Exists(ProfilePath))
                return;
            var read = ReadFromInspect();
            FirstRunMessage = read.Message;
            if (read.Profile is not { } next)
                return;
            try
            {
                var profile = next.Clone();
                profile.ImportedAt = DateTime.Now;
                Save(profile);
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Log("Could not save the imported inspector profile", ex);
                // Another RED process starting at the same moment may have saved the same import.
                if (Load() == null)
                {
                    FirstRunMessage = $"RED could not save the profile it imported from INSPECT: {ex.Message}";
                    return;
                }
            }

            var saved = StrandCredentialService.LoadSaved();
            if (read.Login is { IsComplete: true } login &&
                (saved == null || (!saved.IsComplete && string.Equals(saved.Username, login.Username, StringComparison.OrdinalIgnoreCase))) &&
                SaveLogin(login) is { } loginError)
            {
                FirstRunMessage = (FirstRunMessage == null ? "" : FirstRunMessage + " ") + loginError;
            }
        }

        private static string? SaveLogin(StrandCredentials login)
        {
            try
            {
                StrandCredentialService.Save(login);
                return null;
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Log("Could not save the Strand login imported from INSPECT", ex);
                return $"The profile was imported, but the Strand login could not be saved: {ex.Message}";
            }
        }

        // My List lives in the profile's Inspections folder (F-003 O7); without one, in the usual Dropbox folder.
        public static string MyListFolder()
        {
            string? folder = Load()?.InspectionsFolder;
            return !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)
                ? Path.Combine(folder, "MyList")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Dropbox", "Inspections", "MyList");
        }

        // True when path is folder itself or inside it.
        public static bool IsInside(string path, string folder)
        {
            try
            {
                string full = Path.GetFullPath(path).TrimEnd('\\') + "\\";
                string root = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool HasInspectSettings(string? folder) =>
            !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) &&
            (File.Exists(Path.Combine(folder, "config.json")) || File.Exists(Path.Combine(folder, "config.json.bak")));

        public static void ClearFirstRunMessage() => FirstRunMessage = null;

        // INSPECT's start-up check (owner, 2026-10-18): full name, Strand username and password, default state,
        // user type, and an existing Inspections folder. Each entry names what is missing.
        public static List<string> MissingItems(InspectorProfile? profile, StrandCredentials? login)
        {
            var p = profile ?? new InspectorProfile();
            var missing = new List<string>();
            if (ValidateName(p.InspectorName) != null)
                missing.Add("Full name (first and last)");
            if (login == null || ValidateUsername(login.Username) != null)
                missing.Add("Strand username (your email)");
            if (login == null || login.Password.Length == 0)
                missing.Add("Strand password");
            if (ValidateState(p.DefaultState) != null)
                missing.Add("Default state");
            if (InspectUserType.Classify(p.UserType) == InspectUserKind.Missing)
                missing.Add("User type (set Type in INSPECT Settings, then press Import from INSPECT)");
            if (ValidateFolder(p.InspectionsFolder) != null)
                missing.Add("Inspections folder");
            return missing;
        }

        public static bool IsComplete(InspectorProfile? profile, StrandCredentials? login) => MissingItems(profile, login).Count == 0;

        public static bool IsCompleteNow() => IsComplete(Load(), StrandCredentialService.LoadSaved());

        // The O9 profile guard. Submit (F-013) and every Add (F-001, F-021, F-025) call it, through
        // ProfileGuard.Require, before they act. Null means: refuse with InspectUserType.ProfileIncompleteMessage and open
        // Settings. It checks O9's three items only (owner, 2026-10-18): a Strand username with @, a two-word
        // InspectorName and a non-blank UserType. The start-up lock keeps its six (MissingItems); callers check the
        // folder they write to themselves.
        public static CompleteProfile? RequireComplete() => RequireComplete(Load(), StrandCredentialService.LoadSaved());

        internal static CompleteProfile? RequireComplete(InspectorProfile? profile, StrandCredentials? login)
        {
            if (profile == null || login == null || ValidateUsername(login.Username) != null || ValidateName(profile.InspectorName) != null ||
                InspectUserType.Classify(profile.UserType) == InspectUserKind.Missing)
                return null;
            return new CompleteProfile(profile.InspectionsFolder, InspectUserType.Classify(profile.UserType), profile.InspectorName.Trim(),
                                       login.Username.Trim(), profile.Phone, profile.DefaultState.Trim().ToUpperInvariant());
        }

        // O6, as INSPECT's InspectionFactory: the profile values a new inspection carries.
        public static void StampNewInspection(JObject form, CompleteProfile profile)
        {
            form["InspectorUsername"] = profile.Username;
            form["InspectorName"] = profile.InspectorName;
            form["InspectorPhone"] = profile.Phone;
            form["State"] = profile.DefaultState;
            form["IsPreinspection"] = InspectUserType.IsPreinspection(profile.Kind);
        }

        // Inspection::GetInitials (O6): the first letter of each whitespace-separated word, upper-cased.
        public static string Initials(string? inspectorName) =>
            string.Concat((inspectorName ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(word => word[0]))
                  .ToUpperInvariant();

        // Field messages, as INSPECT's Settings (O5).
        public static string? ValidateName(string? name) =>
            (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length >= 2 ? null : "Full name required.";

        public static string? ValidateUsername(string? username) =>
            !string.IsNullOrWhiteSpace(username) && username.Contains('@') ? null : "Not a valid email address.";

        public static string? ValidatePhone(string? phone) =>
            string.IsNullOrWhiteSpace(phone) || Regex.IsMatch(phone.Trim(), @"^\(\d{3}\) \d{3}-\d{4}$")
                ? null
                : "Use the form (555) 010-0100.";

        public static string? ValidateState(string? state) =>
            States.Contains((state ?? "").Trim().ToUpperInvariant()) ? null : "Choose your state.";

        public static string? ValidateFolder(string? folder) =>
            !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) ? null : "Folder does not exist.";

        public static string PdfName(string? progId) => string.IsNullOrWhiteSpace(progId) ? "(default)" : progId;

        private static string Show(string? value) => string.IsNullOrWhiteSpace(value) ? "(blank)" : value;

        private static bool GetBool(JObject? root, string name, bool fallback) =>
            root?.GetValue(name, StringComparison.OrdinalIgnoreCase) is { Type: JTokenType.Boolean } token ? token.Value<bool>() : fallback;
    }
}
