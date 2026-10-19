using InspectionEditor.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace InspectionEditor.Services
{
    /// <summary>
    /// F-002 Mode A (preserving save): keeps the original .INS document and writes back only what the user changed.
    /// Items and pictures are tracked by in-memory identity since load, never by ItemId/ResultSortOrder or Number.
    /// No keys are added that the file does not have (owner Q-02). Mode B (the INSPECT projection) lives in
    /// <see cref="InspectProjectionWriter"/>.
    /// </summary>
    public class SurgicalSaveService
    {
        private JObject? _originalJson;
        private string? _filePath;
        private byte[]? _lastSavedBytes;
        private readonly FailedSaveRecoveryService _recovery;
        private readonly Func<string, string, byte[]?, byte[]> _write;
        private readonly string? _saveRegistryRoot;
        // Last loaded/saved JSON of each model item and picture. Weak keys: removed copies simply disappear.
        private ConditionalWeakTable<Item, ItemOrigin> _items = new();
        private ConditionalWeakTable<Picture, PictureOrigin> _pictures = new();
        // F-004 V1: items whose stored "NA" became "NI" on open. The NI goes to disk only with another change.
        private HashSet<Item> _naRewrites = new(ReferenceEqualityComparer.Instance);

        private sealed class ItemOrigin
        {
            public ItemOrigin(JObject json, List<Picture> pictures) { Json = json; Pictures = pictures; }
            public JObject Json;
            // The model's picture list (empty shells excluded) as of the last load/save.
            public List<Picture> Pictures;
        }

        private sealed class PictureOrigin
        {
            public PictureOrigin(JObject json, Picture model) { Json = json; Data = model.Data; Title = model.Title; Comment = model.Comment; }
            public JObject Json;
            public readonly string? Data, Title, Comment;
        }

        // INSPECT Item.Clone does not copy these (SPEC A4, RTC B60); a new copy starts with the default value.
        private static readonly string[] CloneResetToNull = { "Value", "Comments", "Template", "ButtonText" };
        private static readonly string[] CloneResetToFalse =
            { "Hide", "HideCommentsButton", "HidePicturesButton", "HideAddButton", "IsPictureRequired" };

        public SurgicalSaveService() : this(new FailedSaveRecoveryService(), AtomicInspectionWriter.Write) { }

        internal SurgicalSaveService(FailedSaveRecoveryService recovery,
            Func<string, string, byte[]?, byte[]>? write = null, string? saveRegistryRoot = null)
        {
            _recovery = recovery;
            _saveRegistryRoot = saveRegistryRoot;
            _write = write ?? AtomicInspectionWriter.Write;
        }

        /// <summary>
        /// Loads an .INS file, storing the original JSON for surgical patching.
        /// Returns the deserialized InspectionFile for the UI to work with.
        /// Throws <see cref="InspectionFileBusyException"/> or <see cref="InspectionFileDamagedException"/> (SPEC R4-R5).
        /// </summary>
        public InspectionFile Load(string filePath)
        {
            JObject originalJson = InspectionFileReader.ReadDocument(filePath, out byte[] loadedBytes, out string jsonText);

            // Deserialize to model for UI
            var inspection = JsonConvert.DeserializeObject<InspectionFile>(jsonText,
                new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
            if (inspection == null)
            {
                throw new Exception("Failed to parse inspection file");
            }

            // Map the actual INS picture fields to our model properties
            MapPictureFieldsFromIns(inspection);
            var items = new ConditionalWeakTable<Item, ItemOrigin>();
            var pictures = new ConditionalWeakTable<Picture, PictureOrigin>();
            RegisterOrigins(inspection, originalJson, items, pictures);
            RemoveEmptyPictureShells(inspection);
            var naRewrites = ItemControls.RewriteNaOnOpen(inspection);

            _originalJson = originalJson;
            _filePath = filePath;
            _lastSavedBytes = loadedBytes;
            _items = items;
            _pictures = pictures;
            _naRewrites = new HashSet<Item>(naRewrites, ReferenceEqualityComparer.Instance);
            inspection.SavedAttachments = originalJson["Attachments"] is JArray loadedAttachments
                ? (JArray)loadedAttachments.DeepClone() : new JArray();

            return inspection;
        }

        /// <summary>
        /// Maps INS file picture fields (Image, Caption, Description) to model fields (Data, Title, Comment)
        /// </summary>
        private void MapPictureFieldsFromIns(InspectionFile inspection)
        {
            if (inspection.Sections == null) return;

            foreach (var section in inspection.Sections)
            {
                if (section == null) continue;
                section.Items ??= new List<Item>(); // "Items": null stays null in the file (nothing to rebuild)
                foreach (var item in section.Items)
                {
                    if (item == null) continue;
                    item.Pictures ??= new List<Picture>(); // likewise "Pictures": null
                    foreach (var picture in item.Pictures)
                    {
                        // Map from ExtensionData (where JSON.NET puts unrecognized fields)
                        if (picture?.ExtensionData != null)
                        {
                            if (picture.ExtensionData.TryGetValue("Image", out var image))
                                picture.Data = image?.ToString();

                            if (picture.ExtensionData.TryGetValue("Caption", out var caption))
                                picture.Title = caption?.ToString();

                            if (picture.ExtensionData.TryGetValue("Description", out var description))
                                picture.Comment = description?.ToString();
                        }
                    }
                }
            }
        }

        // Model and JSON are positionally aligned straight after deserialization.
        private static void RegisterOrigins(InspectionFile inspection, JObject document,
            ConditionalWeakTable<Item, ItemOrigin> items, ConditionalWeakTable<Picture, PictureOrigin> pictures)
        {
            if (inspection.Sections == null || document["Sections"] is not JArray sections) return;
            for (int s = 0; s < inspection.Sections.Count && s < sections.Count; s++)
            {
                if (sections[s] is not JObject sectionJson || sectionJson["Items"] is not JArray itemsJson ||
                    inspection.Sections[s] == null) continue;
                var modelItems = inspection.Sections[s].Items;
                for (int i = 0; i < modelItems.Count && i < itemsJson.Count; i++)
                {
                    if (itemsJson[i] is not JObject itemJson || modelItems[i] == null) continue;
                    var item = modelItems[i];
                    var kept = new List<Picture>();
                    var picturesJson = itemJson["Pictures"] as JArray;
                    for (int p = 0; p < item.Pictures.Count; p++)
                    {
                        var picture = item.Pictures[p];
                        if (picture == null || string.IsNullOrEmpty(picture.Data)) continue;
                        if (picturesJson != null && p < picturesJson.Count && picturesJson[p] is JObject pictureJson)
                            pictures.AddOrUpdate(picture, new PictureOrigin(pictureJson, picture));
                        kept.Add(picture);
                    }
                    items.AddOrUpdate(item, new ItemOrigin(itemJson, kept));
                }
            }
        }

        // SPEC R6: shells (no image data) are hidden from the UI but stay in the file until that item's pictures change.
        // They came from Strand downloads and reportedly caused "Parameter is not valid" in INSPECT2022.
        private static void RemoveEmptyPictureShells(InspectionFile inspection)
        {
            if (inspection.Sections == null) return;
            foreach (var section in inspection.Sections.Where(s => s != null))
                foreach (var item in section.Items)
                    item?.Pictures.RemoveAll(p => p == null || string.IsNullOrEmpty(p.Data));
        }

        /// <summary>
        /// Performs a surgical save - only patches what changed in the original JSON, preserving everything else
        /// exactly. Writes nothing when the result equals the bytes on disk (SPEC V2). Before any write, the file on
        /// disk must still be the version RED loaded or last saved (SPEC E1); otherwise
        /// <see cref="InspectionChangedOnDiskException"/> is thrown and nothing is written.
        /// </summary>
        public void Save(InspectionFile inspection, string? saveAsPath = null)
        {
            if (_originalJson == null)
            {
                throw new InvalidOperationException("No file has been loaded. Call Load() first.");
            }

            string targetPath = saveAsPath ?? _filePath
                ?? throw new InvalidOperationException("No file path specified.");
            bool sameTarget = string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(_filePath!),
                StringComparison.OrdinalIgnoreCase);
            SaveCore(inspection, targetPath, sameTarget, bindToTarget: true);
        }

        /// <summary>
        /// Writes the current edits to a new file (never over an existing one) and keeps editing the original:
        /// RED's "Save As Copy" after a changed-on-disk stop (SPEC E1).
        /// </summary>
        public void SaveCopy(InspectionFile inspection, string copyPath)
        {
            if (_originalJson == null)
                throw new InvalidOperationException("No file has been loaded. Call Load() first.");
            if (string.Equals(Path.GetFullPath(copyPath), Path.GetFullPath(_filePath!), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A copy cannot replace the report it was made from.");
            SaveCore(inspection, copyPath, sameTarget: false, bindToTarget: false);
        }

        // Fee fields RED never shows or changes (owner Q-21): an overwrite keeps the newer file's values.
        private static readonly string[] FeeKeys =
            { "InspectionFee_Secure", "InspectorOtherFee1_Secure", "InspectorOtherFee2_Secure", "OtherFeeDescription1", "OtherFeeDescription2" };

        /// <summary>
        /// Owner decision 2026-10-18 (SPEC E1): after a changed-on-disk stop and a second warning in the UI, writes RED's
        /// copy over the newer file. The fee fields are taken from the newer file (Q-21), so fee automation is not undone.
        /// Still refuses if the file changes again between this read and the replacement, and never recreates a moved or
        /// deleted report. The newer file is first kept byte for byte in RED's local Recovery folder (never trimmed);
        /// returns that copy's path. If it cannot be kept, nothing is overwritten.
        /// </summary>
        public string OverwriteChangedFile(InspectionFile inspection)
        {
            if (_originalJson == null)
                throw new InvalidOperationException("No file has been loaded. Call Load() first.");
            string path = _filePath!;
            byte[] onDisk;
            try { onDisk = InspectionFileReader.ReadBytes(path); }
            catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
            {
                throw MissingReport(path, ex);
            }
            JObject? newer = null;
            try { newer = InspectionFileReader.ParseDocument(onDisk, out _); }
            catch (InspectionFileDamagedException) { /* a damaged newer file has no fee values to keep */ }
            string keptCopy;
            try { keptCopy = _recovery.PreserveReplaced(onDisk); }
            catch (Exception ex)
            {
                throw new IOException("RED could not keep a copy of the newer report, so it did not overwrite it. " + ex.Message, ex);
            }
            SaveCore(inspection, path, sameTarget: true, bindToTarget: true, expectedOnDisk: onDisk, feesFrom: newer);
            return keptCopy;
        }

        private void SaveCore(InspectionFile inspection, string targetPath, bool sameTarget, bool bindToTarget,
            byte[]? expectedOnDisk = null, JObject? feesFrom = null)
        {
            // The bytes the target must still hold: what RED loaded or last saved, or (overwrite) what it just read.
            byte[]? baseline = expectedOnDisk ?? _lastSavedBytes;
            // Patch a candidate, not the last successfully saved JSON. Failed writes are retryable.
            JObject previousJson = _originalJson!;
            var commits = new List<Action>();
            try
            {
                // F-004 V1: the open-time NA→NI rewrite alone is not an edit (F-002 A-002-01), so it is held back first.
                bool deferNaRewrites = sameTarget && _naRewrites.Count > 0;
                string json = BuildCandidate(inspection, previousJson, commits, deferNaRewrites, feesFrom);

                if (sameTarget && baseline != null &&
                    new UTF8Encoding(false).GetBytes(json).AsSpan().SequenceEqual(baseline))
                {
                    // Nothing changed: no write, no backup, the file's bytes and LastWriteTime stay as they are.
                    _lastSavedBytes = baseline;
                    CompleteSave(inspection, commits);
                    return;
                }
                if (deferNaRewrites)
                {
                    // Something else changed, so this save also carries the NI (INSPECT marked those items dirty).
                    commits.Clear();
                    json = BuildCandidate(inspection, previousJson, commits, deferNaRewrites: false, feesFrom);
                }

                byte[] savedBytes;
                try
                {
                    if (sameTarget) EnsureUnchangedOnDisk(targetPath, baseline);
                    savedBytes = _write(targetPath, json, sameTarget ? baseline : null);
                }
                catch (Exception saveError)
                {
                    string recoveryMessage;
                    string? recoveryPath = null;
                    try
                    {
                        recoveryPath = _recovery.Preserve(json);
                        recoveryMessage = $"A full local recovery copy of these edits was verified at:\n{recoveryPath}\n" +
                            "The original save still failed. Keep RED open and retry when the file conflict is resolved. " +
                            "This recovery copy is not opened automatically.";
                    }
                    catch (Exception recoveryError)
                    {
                        recoveryMessage = "Local recovery also failed; no verified recovery copy is available. " +
                            $"Keep RED open with your edits. Recovery error: {recoveryError.Message}";
                    }
                    if (saveError is InspectionChangedOnDiskException changed)
                        throw new InspectionChangedOnDiskException($"{saveError.Message}\n\n{recoveryMessage}", changed, changed.TargetMissing)
                        {
                            RecoveryPath = recoveryPath
                        };
                    throw new IOException($"{saveError.Message}\n\n{recoveryMessage}", saveError);
                }
                if (!bindToTarget)
                {
                    // A copy leaves the open report, its origins and its pending attachment edits untouched.
                    _originalJson = previousJson;
                    return;
                }
                if (_saveRegistryRoot == null) LastEditTime.RecordSuccessfulSave(targetPath, savedBytes);
                else LastEditTime.RecordSuccessfulSave(targetPath, savedBytes, _saveRegistryRoot);
                _lastSavedBytes = savedBytes;
                _filePath = targetPath;
                CompleteSave(inspection, commits);
                _naRewrites.Clear();
            }
            catch
            {
                _originalJson = previousJson;
                throw;
            }
        }

        // Builds the Mode A candidate from previousJson into _originalJson and returns its compact JSON.
        private string BuildCandidate(InspectionFile inspection, JObject previousJson, List<Action> commits,
            bool deferNaRewrites, JObject? feesFrom = null)
        {
            _originalJson = previousJson;
            _originalJson = BuildDocument(inspection, commits, deferNaRewrites);
            if (inspection.AttachmentEdit != null) PatchAttachmentCollection(inspection);
            else
            {
                PatchAddedAttachments(inspection);
                PatchOrientationAttachment(inspection);
            }
            PatchPlanCheckMetadata(inspection);
            if (feesFrom != null)
            {
                foreach (string key in FeeKeys)
                    if (feesFrom.TryGetValue(key, out JToken? fee)) _originalJson[key] = fee.DeepClone();
            }
            return _originalJson.ToString(Formatting.None);
        }

        /// <summary>The exact JSON a save would write now (a forced Mode A save), without writing or committing.</summary>
        internal string RenderPreservingSave(InspectionFile inspection) => RenderPreservingSave(inspection, deferNaRewrites: false);

        /// <summary>
        /// As above; with <paramref name="deferNaRewrites"/> the open-time NA→NI rewrite (F-004 V1) is held back, which is
        /// what a save writes when nothing else changed.
        /// </summary>
        internal string RenderPreservingSave(InspectionFile inspection, bool deferNaRewrites)
        {
            if (_originalJson == null)
                throw new InvalidOperationException("No file has been loaded. Call Load() first.");
            JObject previousJson = _originalJson;
            try { return BuildCandidate(inspection, previousJson, new List<Action>(), deferNaRewrites); }
            finally { _originalJson = previousJson; }
        }

        /// <summary>F-004 V1: true while an open-time NA→NI rewrite is still waiting for a save with another change.</summary>
        public bool HasPendingNaRewrites => _naRewrites.Count > 0;

        private void CompleteSave(InspectionFile inspection, List<Action> commits)
        {
            foreach (var commit in commits) commit();
            inspection.SavedAttachments = _originalJson!["Attachments"] is JArray savedAttachments
                ? (JArray)savedAttachments.DeepClone() : new JArray();
            inspection.AttachmentEdit = null;
            if (inspection.OrientationEdit != null)
                inspection.OrientationEdit.Expected = (JObject)inspection.OrientationEdit.Replacement.DeepClone();
        }

        /// <summary>
        /// SPEC E1: throws <see cref="InspectionChangedOnDiskException"/> when the open report on disk is no longer the
        /// version RED loaded or last saved. Submit (F-013) calls this immediately before it writes or moves anything.
        /// </summary>
        public void EnsureUnchangedOnDisk() => EnsureUnchangedOnDisk(_filePath
            ?? throw new InvalidOperationException("No file has been loaded. Call Load() first."), _lastSavedBytes);

        private static void EnsureUnchangedOnDisk(string path, byte[]? expected)
        {
            string name = SafeFileName(path);
            byte[] current;
            try { current = InspectionFileReader.ReadBytes(path); }
            catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
            {
                throw MissingReport(path, ex);
            }
            if (expected == null || !current.AsSpan().SequenceEqual(expected))
                throw new InspectionChangedOnDiskException(
                    $"Save stopped for '{name}'. The report changed on disk after RED opened it (INSPECT, or a Dropbox " +
                    "sync from another computer). RED did not write over the newer file.", null, false);
        }

        // A report gone from a folder that is still there was moved or deleted (E1, never recreated). A missing folder
        // or drive (unplugged, share dropped) is only unavailable: a plain, retryable failure that keeps the edits open.
        private static IOException MissingReport(string path, Exception cause) =>
            AtomicInspectionWriter.ReportFolderAvailable(path)
                ? new InspectionChangedOnDiskException(
                    $"Save stopped for '{SafeFileName(path)}'. The report was moved or deleted outside RED; it was not recreated.", cause, true)
                : new IOException(
                    $"Save stopped for '{SafeFileName(path)}'. The report's folder or drive is not available right now; " +
                    "nothing was written. Keep RED open with your edits and save again when it is back.", cause);

        /// <summary>
        /// A verified local recovery copy of the edits as they are now, without saving the report. Used before RED
        /// discards edits (Reload, close without saving) and while saving is paused after a changed-on-disk stop.
        /// </summary>
        public string PreserveUnsavedEdits(InspectionFile inspection) => _recovery.Preserve(RenderPreservingSave(inspection));

        /// <summary>While saving is paused: one rolling recovery copy of the edits, replaced as they change.</summary>
        public string PreserveUnsavedEditsRolling(InspectionFile inspection) => _recovery.PreserveRolling(RenderPreservingSave(inspection));

        private static string SafeFileName(string path) =>
            new string(Path.GetFileName(path).Select(c => char.IsControl(c) ? '_' : c).ToArray());

        /// <summary>
        /// SPEC Mode B: INSPECT's own bytes for the report as last loaded or saved. Callers save first (Mode A), then
        /// project, so the projection reflects every edit and the recomputed ResultSortOrder. A save that only had the
        /// F-004 V1 NA→NI rewrite pending writes nothing, so this projection still says "NA": Submit (F-013) uses
        /// <see cref="ProjectCurrent"/>.
        /// </summary>
        public InspectProjection ProjectSavedDocument()
        {
            if (_originalJson == null)
                throw new InvalidOperationException("No file has been loaded. Call Load() first.");
            return InspectProjectionWriter.Project(_originalJson);
        }

        /// <summary>
        /// The Mode B projection of the report as it stands in memory, including an open-time NA→NI rewrite that no
        /// save has written yet (F-004 V1: Submit carries the NI). For F-013, after its save.
        /// </summary>
        public InspectProjection ProjectCurrent(InspectionFile inspection)
        {
            if (_originalJson == null)
                throw new InvalidOperationException("No file has been loaded. Call Load() first.");
            JObject previousJson = _originalJson;
            try
            {
                BuildCandidate(inspection, previousJson, new List<Action>(), deferNaRewrites: false);
                return InspectProjectionWriter.Project(_originalJson);
            }
            finally { _originalJson = previousJson; }
        }

        /// <summary>
        /// Append only attachments created during this RED session. Existing attachment JSON
        /// remains represented by the original parsed tokens.
        /// </summary>
        private void PatchAttachmentCollection(InspectionFile inspection)
        {
            var edit = inspection.AttachmentEdit!;
            var original = _originalJson!["Attachments"];
            if (original != null && original.Type != JTokenType.Null && original is not JArray)
                throw new IOException("Unsupported attachment collection; save stopped.");
            var current = original as JArray ?? new JArray();
            var model = inspection.Attachments == null ? new JArray() : JArray.FromObject(inspection.Attachments);
            if (!JToken.DeepEquals(current, edit.Expected) || !JToken.DeepEquals(model, edit.Replacement))
                throw new IOException("Attachment collection changed concurrently; save stopped to preserve all documents.");
            _originalJson["Attachments"] = edit.Replacement.DeepClone();
        }

        private void PatchAddedAttachments(InspectionFile inspection)
        {
            if (_originalJson == null || inspection.Attachments == null) return;
            if (inspection.ExtensionData == null ||
                !inspection.ExtensionData.TryGetValue("RedPlanCheck", out object? metadata) || metadata == null)
                return;

            var originalAttachments = _originalJson["Attachments"] as JArray;
            if (originalAttachments == null)
            {
                if (inspection.Attachments.Count > 0)
                    _originalJson["Attachments"] = JArray.FromObject(inspection.Attachments);
                return;
            }

            for (int i = originalAttachments.Count; i < inspection.Attachments.Count; i++)
            {
                object attachment = inspection.Attachments[i];
                originalAttachments.Add(attachment is JToken token
                    ? token.DeepClone()
                    : JToken.FromObject(attachment));
            }
        }

        private void PatchOrientationAttachment(InspectionFile inspection)
        {
            var edit = inspection.OrientationEdit;
            if (edit == null) return;
            var attachments = _originalJson!["Attachments"] as JArray;
            if (attachments == null)
            {
                if (_originalJson["Attachments"] != null && _originalJson["Attachments"]!.Type != JTokenType.Null)
                    throw new IOException("Unsupported attachment collection; original data was not replaced.");
                attachments = new JArray();
                _originalJson["Attachments"] = attachments;
            }
            if (edit.Index == attachments.Count && edit.Expected == null)
                attachments.Add(edit.Replacement.DeepClone());
            else if (edit.Index >= 0 && edit.Index < attachments.Count &&
                (JToken.DeepEquals(attachments[edit.Index], edit.Expected) ||
                 JToken.DeepEquals(attachments[edit.Index], edit.Replacement)))
                attachments[edit.Index] = edit.Replacement.DeepClone();
            else
                throw new IOException("Orientation attachment changed unexpectedly; save stopped to preserve other documents.");
        }

        private void PatchPlanCheckMetadata(InspectionFile inspection)
        {
            if (_originalJson == null || inspection.ExtensionData == null ||
                !inspection.ExtensionData.TryGetValue("RedPlanCheck", out object? metadata) || metadata == null)
                return;

            // RED-only key (SPEC A8): allowed in Mode A, always dropped by Mode B.
            _originalJson["RedPlanCheck"] = metadata is JToken token
                ? token.DeepClone()
                : JToken.FromObject(metadata);
        }

        /// <summary>
        /// Builds the Mode A candidate: every top-level and section key copied in its original order; only the
        /// Items arrays are rebuilt from the model. Origin updates are queued in <paramref name="commits"/> and run
        /// only after the candidate is safely on disk.
        /// </summary>
        private JObject BuildDocument(InspectionFile inspection, List<Action> commits, bool deferNaRewrites = false)
        {
            if (inspection.Sections != null) RecomputeResultSortOrder(inspection);
            var document = new JObject();
            foreach (var property in _originalJson!.Properties())
            {
                if (property.Name == "Sections" && property.Value is JArray sections && inspection.Sections != null)
                    document.Add("Sections", BuildSections(sections, inspection, commits, deferNaRewrites));
                else
                    document.Add(property.Name, property.Value.DeepClone());
            }
            return document;
        }

        // SPEC A6: sections are matched by position.
        private JArray BuildSections(JArray sections, InspectionFile inspection, List<Action> commits, bool deferNaRewrites)
        {
            var result = new JArray();
            for (int s = 0; s < sections.Count; s++)
            {
                if (s >= inspection.Sections.Count || sections[s] is not JObject sectionJson || inspection.Sections[s] == null)
                {
                    result.Add(sections[s].DeepClone());
                    continue;
                }
                var section = inspection.Sections[s];
                var newSection = new JObject();
                foreach (var property in sectionJson.Properties())
                {
                    if (property.Name == "Items" && (property.Value is JArray || section.Items.Count > 0))
                        newSection.Add("Items", BuildItems(section, inspection, commits, deferNaRewrites));
                    else
                        newSection.Add(property.Name, property.Value.DeepClone());
                }
                if (newSection["Items"] == null && section.Items.Count > 0)
                    newSection.Add("Items", BuildItems(section, inspection, commits, deferNaRewrites));
                result.Add(newSection);
            }
            return result;
        }

        private JArray BuildItems(Section section, InspectionFile inspection, List<Action> commits, bool deferNaRewrites)
        {
            var items = new JArray();
            foreach (var item in section.Items)
            {
                if (item == null) { items.Add(JValue.CreateNull()); continue; }
                JObject itemJson;
                if (_items.TryGetValue(item, out var origin))
                {
                    itemJson = (JObject)origin.Json.DeepClone();
                    DeriveControlValue(item, origin.Json, origin.Pictures);
                    if (!(deferNaRewrites && IsPendingNaRewrite(item, origin.Json)))
                        PatchValue(item, itemJson);
                    PatchComments(item, itemJson);
                    PatchCopyFields(item, itemJson);
                    PatchPictures(item, itemJson, origin, commits);
                    PatchControlData(item, itemJson);
                }
                else
                {
                    // NEW ITEM (a copy made in RED)
                    itemJson = CreateCopyJson(item, section, inspection);
                    DeriveControlValue(item, null, null);
                    PatchValue(item, itemJson);
                    PatchComments(item, itemJson);
                    PatchPictures(item, itemJson, null, commits);
                    PatchControlData(item, itemJson);
                }
                items.Add(itemJson);
                var committed = itemJson;
                var pictures = item.Pictures.Where(p => p != null && !string.IsNullOrEmpty(p.Data)).ToList();
                commits.Add(() => _items.AddOrUpdate(item, new ItemOrigin(committed, pictures)));
            }
            return items;
        }

        /// <summary>
        /// SPEC A5: INSPECT's SortResults over all items of all sections as one sequence (no reset at section
        /// boundaries): the first item of a run of equal ItemId gets 0, each following item previous + 1, capped at 255.
        /// </summary>
        public static void RecomputeResultSortOrder(InspectionFile inspection)
        {
            int previousItemId = -1;
            int order = 0;
            foreach (var section in inspection.Sections)
            {
                if (section?.Items == null) continue;
                foreach (var item in section.Items)
                {
                    if (item == null) continue;
                    order = item.ItemId == previousItemId ? Math.Min(order + 1, 255) : 0;
                    item.ResultSortOrder = order;
                    previousItemId = item.ItemId;
                }
            }
        }

        /// <summary>
        /// SPEC A4, model side: makes a copy of <paramref name="source"/> the way INSPECT's Item.Clone does and inserts
        /// it directly after the source. Value, Comments, Pictures, the Hide* flags and IsPictureRequired start empty;
        /// the next save clones every other key from the source item's JSON.
        /// </summary>
        public static Item InsertCopy(InspectionFile inspection, Section section, Item source)
        {
            int index = section.Items.IndexOf(source);
            if (index < 0) throw new ArgumentException("The source item is not in this section.", nameof(source));
            var copy = new Item
            {
                ItemId = source.ItemId,
                Name = source.Name,
                Number = source.Number,
                ControlName = source.ControlName,
                DisplayLabel = source.DisplayLabel,
                ValueList = source.ValueList != null ? new List<string>(source.ValueList) : null,
                Required = source.Required,
                SortOrder = source.SortOrder,
                HidePicturesButton = false,
                HideCommentsButton = false,
                IsPictureRequired = false,
                IsCopied = true,
                CopiedFrom = source,
                Value = null,
                Comments = null,
                Pictures = new List<Picture>(),
                // Copy ExtensionData for any other properties (not serialized; JSON comes from the source's origin)
                ExtensionData = source.ExtensionData != null
                    ? new Dictionary<string, object>(source.ExtensionData)
                    : new Dictionary<string, object>()
            };
            // Item.Clone does not copy Template or ButtonText either (the save resets them in the copy's JSON).
            foreach (string key in new[] { "Template", "ButtonText" })
                if (copy.ExtensionData.ContainsKey(key)) copy.ExtensionData[key] = JValue.CreateNull();
            section.Items.Insert(index + 1, copy);
            RecomputeResultSortOrder(inspection);
            return copy;
        }

        /// <summary>
        /// SPEC A4: a copy starts from the source item's full JSON, changed exactly as INSPECT Item.Clone does.
        /// </summary>
        private JObject CreateCopyJson(Item item, Section section, InspectionFile inspection)
        {
            JObject? sourceJson = FindCopySourceJson(item, section, inspection);
            JObject itemJson;
            if (sourceJson != null)
            {
                itemJson = (JObject)sourceJson.DeepClone();
                itemJson.Remove("ItemResultId");
                foreach (string key in CloneResetToNull)
                    if (itemJson.ContainsKey(key)) itemJson[key] = JValue.CreateNull();
                foreach (string key in CloneResetToFalse)
                    if (itemJson.ContainsKey(key)) itemJson[key] = false;
                if (itemJson.ContainsKey("Pictures")) itemJson["Pictures"] = new JArray();
            }
            else
            {
                // No JSON to clone from (should not happen for copies made in the editor): INSPECT-local item keys
                // with Item.Clone's values, without the server-only ItemResultId.
                itemJson = new JObject
                {
                    ["ItemId"] = item.ItemId,
                    ["Name"] = item.Name,
                    ["Number"] = item.Number,
                    ["ControlName"] = item.ControlName,
                    ["ControlFormatString"] = null,
                    ["ItemGroupId"] = "",
                    ["ItemGroupControlName"] = null,
                    ["PictureSizeId"] = 0,
                    ["IsCopied"] = true,
                    ["Hide"] = false,
                    ["MaxIncidents"] = null,
                    ["SortOrder"] = item.SortOrder,
                    ["Required"] = item.Required,
                    ["AlwaysPrint"] = false,
                    ["OnlyPrint"] = "",
                    ["Template"] = null,
                    ["ButtonText"] = null,
                    ["HideCommentsButton"] = false,
                    ["HidePicturesButton"] = false,
                    ["HideAddButton"] = false,
                    ["CommentList"] = new JArray(),
                    ["ValueList"] = item.ValueList != null ? JArray.FromObject(item.ValueList) : JValue.CreateNull(),
                    ["IsPictureRequired"] = false,
                    ["Pictures"] = new JArray(),
                    ["DisplayLabel"] = item.DisplayLabel,
                    ["Value"] = null,
                    ["ResultSortOrder"] = item.ResultSortOrder,
                    ["Comments"] = null,
                    ["Incidents"] = null
                };
            }
            itemJson["IsCopied"] = true;
            itemJson["ResultSortOrder"] = item.ResultSortOrder;
            return itemJson;
        }

        private JObject? FindCopySourceJson(Item item, Section section, InspectionFile inspection)
        {
            var seen = new HashSet<Item>(ReferenceEqualityComparer.Instance);
            for (var source = item.CopiedFrom; source != null && seen.Add(source); source = source.CopiedFrom)
                if (_items.TryGetValue(source, out var origin)) return origin.Json;
            // Older callers that do not set CopiedFrom: nearest earlier item with the same ItemId, then any.
            int index = section.Items.IndexOf(item);
            for (int i = index - 1; i >= 0; i--)
                if (section.Items[i]?.ItemId == item.ItemId && _items.TryGetValue(section.Items[i], out var origin))
                    return origin.Json;
            foreach (var other in inspection.Sections.SelectMany(s => s.Items))
                if (other != null && other.ItemId == item.ItemId && _items.TryGetValue(other, out var origin))
                    return origin.Json;
            return null;
        }

        /// <summary>
        /// Value is written only when it differs from the last saved value (an explicit clear writes null).
        /// </summary>
        private static void PatchValue(Item item, JObject itemJson)
        {
            JToken value = item.Value switch
            {
                null => JValue.CreateNull(),
                JToken token => token.DeepClone(),
                _ => JToken.FromObject(item.Value)
            };
            JToken? saved = itemJson["Value"];
            if (saved == null ? value.Type != JTokenType.Null : !JToken.DeepEquals(saved, value))
                itemJson["Value"] = value;
        }

        // F-004 V1: the item still holds only the open-time "NI" for a stored "NA".
        private bool IsPendingNaRewrite(Item item, JObject originJson) =>
            _naRewrites.Contains(item) &&
            string.Equals(ItemControls.ValueText(item), "NI", StringComparison.Ordinal) &&
            originJson["Value"] is JValue { Type: JTokenType.String } saved && (string?)saved == "NA";

        /// <summary>
        /// F-004 values INSPECT derives from other storage, set when that storage changed since the last load or save:
        /// Memo "ok"/null from its Comments text (RTC B33), PictureUpload "[Uploaded]"/"" from its pictures (B46).
        /// </summary>
        private static void DeriveControlValue(Item item, JObject? originJson, List<Picture>? originPictures)
        {
            switch (ItemControls.For(item).Kind)
            {
                case ItemControlKind.Memo:
                {
                    JToken? saved = originJson?["Comments"];
                    string savedText = saved == null || saved.Type == JTokenType.Null ? "" : saved.ToString();
                    if (!string.Equals(savedText, item.Comments ?? "", StringComparison.Ordinal))
                        item.Value = ItemControls.MemoValueFor(item.Comments);
                    break;
                }
                case ItemControlKind.PictureUpload:
                {
                    var current = item.Pictures.Where(p => p != null && !string.IsNullOrEmpty(p.Data)).ToList();
                    bool changed = originPictures == null
                        ? current.Count > 0
                        : originPictures.Count != current.Count || current.Where((p, i) => !ReferenceEquals(p, originPictures[i])).Any();
                    if (changed) ItemControls.SyncPictureUploadValue(item);
                    break;
                }
            }
        }

        /// <summary>
        /// F-004 storage beyond Value (F-002 A3): a SearchLookup "Add New" grows ValueList (RTC B36); a DocumentUpload
        /// stores the file name in Template (B47). Written only when different from the item's JSON.
        /// </summary>
        private static void PatchControlData(Item item, JObject itemJson)
        {
            switch (ItemControls.For(item).Kind)
            {
                case ItemControlKind.SearchLookup:
                {
                    JToken list = item.ValueList == null ? JValue.CreateNull() : JArray.FromObject(item.ValueList);
                    JToken? saved = itemJson["ValueList"];
                    if (saved == null ? item.ValueList is { Count: > 0 } : !JToken.DeepEquals(saved, list))
                        itemJson["ValueList"] = list;
                    break;
                }
                case ItemControlKind.DocumentUpload:
                {
                    if (item.ExtensionData == null || !item.ExtensionData.TryGetValue("Template", out object? template)) break;
                    JToken value = template switch
                    {
                        null => JValue.CreateNull(),
                        JToken token => token.DeepClone(),
                        _ => JToken.FromObject(template)
                    };
                    JToken? saved = itemJson["Template"];
                    if (saved == null ? value.Type != JTokenType.Null : !JToken.DeepEquals(saved, value))
                        itemJson["Template"] = value;
                    break;
                }
            }
        }

        /// <summary>
        /// Comments are written only when the text changed. Null and "" count as the same empty comment, so an
        /// untouched null stays null (SPEC A3, INSPECT writes null comments as null). A cleared comment is "".
        /// </summary>
        private static void PatchComments(Item item, JObject itemJson)
        {
            JToken? saved = itemJson["Comments"];
            string savedText = saved == null || saved.Type == JTokenType.Null ? "" : saved.ToString();
            string currentText = item.Comments ?? "";
            if (string.Equals(savedText, currentText, StringComparison.Ordinal)) return;
            // Comment was changed or cleared - write empty string (not null, to avoid INSPECT2022 issues)
            itemJson["Comments"] = currentText;
        }

        // IsCopied and ResultSortOrder: written when different; added only when the value is not INSPECT's default.
        private static void PatchCopyFields(Item item, JObject itemJson)
        {
            JToken? copied = itemJson["IsCopied"];
            if (copied == null ? item.IsCopied : copied.Type != JTokenType.Boolean || (bool)copied != item.IsCopied)
                itemJson["IsCopied"] = item.IsCopied;
            JToken? order = itemJson["ResultSortOrder"];
            if (order == null ? item.ResultSortOrder != 0 : order.Type != JTokenType.Integer || (long)order != item.ResultSortOrder)
                itemJson["ResultSortOrder"] = item.ResultSortOrder;
        }

        /// <summary>
        /// SPEC A3/R6: unchanged pictures keep their original objects byte for byte (Path, ServerPath, PictureId,
        /// SortOrder, unknown keys) and the array keeps any empty shells. Once the item's pictures change, the array is
        /// rebuilt from the model: unchanged pictures still copied verbatim, changed ones patched in place, new ones in
        /// INSPECT's picture format, shells dropped.
        /// </summary>
        private void PatchPictures(Item item, JObject itemJson, ItemOrigin? origin, List<Action> commits)
        {
            var current = item.Pictures.Where(p => p != null && !string.IsNullOrEmpty(p.Data)).ToList();
            bool unchanged = origin != null && origin.Pictures.Count == current.Count;
            for (int i = 0; unchanged && i < current.Count; i++)
                unchanged = ReferenceEquals(origin!.Pictures[i], current[i]) && !PictureChanged(current[i]);

            if (unchanged)
            {
                // Re-point origins at the cloned objects so later saves never reach into an older document.
                if (origin!.Json["Pictures"] is JArray before && itemJson["Pictures"] is JArray after)
                {
                    foreach (var picture in current)
                    {
                        if (!_pictures.TryGetValue(picture, out var pictureOrigin)) continue;
                        int index = before.IndexOf(pictureOrigin.Json);
                        if (index >= 0 && index < after.Count && after[index] is JObject cloned)
                            commits.Add(() => _pictures.AddOrUpdate(picture, new PictureOrigin(cloned, picture)));
                    }
                }
                return;
            }

            if (origin == null && current.Count == 0) return; // new copy keeps the cloned (empty) picture array

            var array = new JArray();
            foreach (var picture in current)
            {
                JObject pictureJson;
                if (_pictures.TryGetValue(picture, out var pictureOrigin))
                {
                    pictureJson = (JObject)pictureOrigin.Json.DeepClone();
                    if (!string.Equals(pictureOrigin.Data, picture.Data, StringComparison.Ordinal)) pictureJson["Image"] = picture.Data;
                    if (!string.Equals(pictureOrigin.Title, picture.Title, StringComparison.Ordinal)) pictureJson["Caption"] = picture.Title;
                    if (!string.Equals(pictureOrigin.Comment, picture.Comment, StringComparison.Ordinal)) pictureJson["Description"] = picture.Comment;
                }
                else
                {
                    // INSPECT's picture keys, in INSPECT's order; no RED-only keys (owner Q-02).
                    pictureJson = new JObject
                    {
                        ["Path"] = null,
                        ["Caption"] = picture.Title ?? "Picture",
                        ["Description"] = picture.Comment,
                        ["Image"] = picture.Data
                    };
                }
                array.Add(pictureJson);
                commits.Add(() => _pictures.AddOrUpdate(picture, new PictureOrigin(pictureJson, picture)));
            }
            itemJson["Pictures"] = array;
        }

        private bool PictureChanged(Picture picture) =>
            !_pictures.TryGetValue(picture, out var origin) ||
            !string.Equals(origin.Data, picture.Data, StringComparison.Ordinal) ||
            !string.Equals(origin.Title, picture.Title, StringComparison.Ordinal) ||
            !string.Equals(origin.Comment, picture.Comment, StringComparison.Ordinal);

        /// <summary>
        /// Sets the inspection result fields (StatusId, NextActionId, NextActionText) directly
        /// on the original JSON so they are included in the next Save() call.
        /// </summary>
        public void SetResult(int statusId, int nextActionId, string? nextActionText)
        {
            if (_originalJson == null)
                throw new InvalidOperationException("No file has been loaded. Call Load() first.");

            _originalJson["StatusId"] = statusId;
            _originalJson["NextActionId"] = nextActionId;
            _originalJson["NextActionText"] = nextActionText != null ? (JToken)nextActionText : JValue.CreateNull();
        }

        /// <summary>
        /// Gets the current StatusId from the original JSON (0 or 1 = not yet set).
        /// </summary>
        public int GetCurrentStatusId()
        {
            if (_originalJson == null) return 0;
            return _originalJson["StatusId"]?.Value<int>() ?? 0;
        }

        /// <summary>
        /// Gets the current file path.
        /// </summary>
        public string? FilePath => _filePath;

        /// <summary>
        /// Checks if a file has been loaded.
        /// </summary>
        public bool HasFile => _originalJson != null;
    }
}
