using InspectionEditor.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InspectionEditor.Services
{
    /// <summary>
    /// Provides surgical save functionality that preserves the original .INS file structure
    /// while only modifying the specific fields that were changed (Comments, Pictures).
    /// </summary>
    public class SurgicalSaveService
    {
        private JObject? _originalJson;
        private string? _filePath;

        /// <summary>
        /// Loads an .INS file, storing the original JSON for surgical patching.
        /// Returns the deserialized InspectionFile for the UI to work with.
        /// </summary>
        public InspectionFile Load(string filePath)
        {
            string jsonText = File.ReadAllText(filePath);

            // Store original JSON structure for surgical saves
            _originalJson = JObject.Parse(jsonText);
            _filePath = filePath;

            // Deserialize to model for UI
            var inspection = JsonConvert.DeserializeObject<InspectionFile>(jsonText);
            if (inspection == null)
            {
                throw new Exception("Failed to parse inspection file");
            }

            // Map the actual INS picture fields to our model properties
            MapPictureFieldsFromIns(inspection);

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
                foreach (var item in section.Items)
                {
                    foreach (var picture in item.Pictures)
                    {
                        // Map from ExtensionData (where JSON.NET puts unrecognized fields)
                        if (picture.ExtensionData != null)
                        {
                            if (picture.ExtensionData.TryGetValue("Image", out var image))
                                picture.Data = image?.ToString();

                            if (picture.ExtensionData.TryGetValue("Caption", out var caption))
                                picture.Title = caption?.ToString();

                            if (picture.ExtensionData.TryGetValue("Description", out var description))
                                picture.Comment = description?.ToString();
                        }
                    }

                    // Remove empty picture shells (placeholders with no image data)
                    // These come from Strand downloads and cause "Parameter is not valid" in INSPECT2022
                    item.Pictures.RemoveAll(p => string.IsNullOrEmpty(p.Data));
                }
            }
        }

        /// <summary>
        /// Performs a surgical save - only patches the Comments and Pictures fields
        /// in the original JSON, preserving everything else exactly.
        /// </summary>
        public void Save(InspectionFile inspection, string? saveAsPath = null)
        {
            if (_originalJson == null)
            {
                throw new InvalidOperationException("No file has been loaded. Call Load() first.");
            }

            string targetPath = saveAsPath ?? _filePath
                ?? throw new InvalidOperationException("No file path specified.");

            // Ensure required top-level fields exist (Strand/INSPECT2022 compatibility)
            EnsureRequiredTopLevelFields(targetPath);

            // Ensure every item has an ItemResultId (required by INSPECT2022)
            EnsureItemResultIds();

            // Patch only the fields we modify
            PatchInspection(inspection);

            // Write with minimal formatting to match original style
            string json = _originalJson.ToString(Formatting.None);
            File.WriteAllText(targetPath, json);

            // If saved to a new path, update our reference
            if (saveAsPath != null)
            {
                _filePath = saveAsPath;
            }
        }

        /// <summary>
        /// Patches the original JSON with only the changed fields from the model.
        /// Properly handles duplicates by matching items on ItemId+IsCopied+ResultSortOrder.
        /// </summary>
        private void PatchInspection(InspectionFile inspection)
        {
            var sectionsArray = _originalJson!["Sections"] as JArray;
            if (sectionsArray == null || inspection.Sections == null) return;

            for (int sIdx = 0; sIdx < inspection.Sections.Count && sIdx < sectionsArray.Count; sIdx++)
            {
                var section = inspection.Sections[sIdx];
                var sectionJson = sectionsArray[sIdx] as JObject;
                if (sectionJson == null) continue;

                var originalItemsArray = sectionJson["Items"] as JArray;
                
                // Build a NEW items array from the model, preserving original JSON for existing items
                var newItemsArray = new JArray();
                
                foreach (var item in section.Items)
                {
                    JObject itemJson;
                    
                    // Try to find matching original item by ItemId + IsCopied + ResultSortOrder
                    JObject? matchingOriginal = FindMatchingOriginalItem(originalItemsArray, item);
                    
                    if (matchingOriginal != null)
                    {
                        // Use the original as base, then patch
                        itemJson = matchingOriginal;
                        PatchComments(item, itemJson);
                        PatchValue(item, itemJson);
                        PatchPictures(item, itemJson);
                        itemJson["IsCopied"] = item.IsCopied;
                        itemJson["ResultSortOrder"] = item.ResultSortOrder;
                    }
                    else
                    {
                        // NEW ITEM (duplicate) - fully serialize it
                        itemJson = SerializeNewItem(item);
                    }
                    
                    newItemsArray.Add(itemJson);
                }
                
                // Replace the items array with our properly ordered one
                sectionJson["Items"] = newItemsArray;
            }
        }
        
        /// <summary>
        /// Finds a matching item in the original JSON array by ItemId + IsCopied + ResultSortOrder
        /// </summary>
        private JObject? FindMatchingOriginalItem(JArray? originalItems, Item item)
        {
            if (originalItems == null) return null;
            
            foreach (var orig in originalItems)
            {
                if (orig is JObject origObj)
                {
                    int origItemId = origObj["ItemId"]?.Value<int>() ?? -1;
                    bool origIsCopied = origObj["IsCopied"]?.Value<bool>() ?? false;
                    int origResultSortOrder = origObj["ResultSortOrder"]?.Value<int>() ?? 0;
                    
                    if (origItemId == item.ItemId && 
                        origIsCopied == item.IsCopied && 
                        origResultSortOrder == item.ResultSortOrder)
                    {
                        return origObj;
                    }
                }
            }
            
            return null;
        }
        
        /// <summary>
        /// Serializes a new item (duplicate) to JSON with proper INS format
        /// </summary>
        private JObject SerializeNewItem(Item item)
        {
            var itemJson = new JObject
            {
                ["ItemId"] = item.ItemId,
                ["ItemResultId"] = Guid.NewGuid().ToString(),
                ["Name"] = item.Name,
                ["Number"] = item.Number,
                ["ControlName"] = item.ControlName,
                ["ControlFormatString"] = null,
                ["ItemGroupId"] = "",
                ["ItemGroupControlName"] = null,
                ["PictureSizeId"] = 0,
                ["IsCopied"] = item.IsCopied,
                ["Hide"] = false,
                ["MaxIncidents"] = null,
                ["SortOrder"] = item.SortOrder,
                ["Required"] = item.Required,
                ["AlwaysPrint"] = false,
                ["OnlyPrint"] = "",
                ["Template"] = null,
                ["ButtonText"] = null,
                ["HideCommentsButton"] = item.HideCommentsButton,
                ["HidePicturesButton"] = item.HidePicturesButton,
                ["HideAddButton"] = false,
                ["CommentList"] = new JArray(), // Duplicates start with empty comment list
                ["ValueList"] = item.ValueList != null ? JArray.FromObject(item.ValueList) : new JArray(),
                ["IsPictureRequired"] = item.IsPictureRequired,
                ["Pictures"] = new JArray(),
                ["DisplayLabel"] = item.DisplayLabel,
                ["Value"] = item.Value != null ? JToken.FromObject(item.Value) : null,
                ["ResultSortOrder"] = item.ResultSortOrder,
                ["Comments"] = item.Comments,
                ["Incidents"] = null
            };
            
            // Add pictures if any
            if (item.Pictures.Count > 0)
            {
                var picturesArray = new JArray();
                foreach (var picture in item.Pictures)
                {
                    var picJson = new JObject
                    {
                        ["Image"] = picture.Data,
                        ["Caption"] = picture.Title ?? "Picture",
                        ["Description"] = picture.Comment,
                        ["Path"] = null,
                        ["Filename"] = picture.Filename,
                        ["SortOrder"] = picture.SortOrder
                    };
                    picturesArray.Add(picJson);
                }
                itemJson["Pictures"] = picturesArray;
            }
            
            return itemJson;
        }

        /// <summary>
        /// Patches the Comments field in the JSON.
        /// </summary>
        private void PatchComments(Item item, JObject itemJson)
        {
            // Always sync comments - null or empty means cleared
            if (item.Comments == null || item.Comments == "")
            {
                // Comment was cleared - write empty string (not null, to avoid INSPECT2022 issues)
                itemJson["Comments"] = "";
            }
            else
            {
                itemJson["Comments"] = item.Comments;
            }
        }

        /// <summary>
        /// Patches the Value field (Pass/Fail status) in the JSON.
        /// </summary>
        private void PatchValue(Item item, JObject itemJson)
        {
            if (item.Value != null)
            {
                itemJson["Value"] = JToken.FromObject(item.Value);
            }
        }

        /// <summary>
        /// Patches the Pictures array in the JSON, using the correct INS field names.
        /// </summary>
        private void PatchPictures(Item item, JObject itemJson)
        {
            var picturesArray = itemJson["Pictures"] as JArray;

            // Filter out empty picture shells (no image data) before saving
            // These can come from Strand downloads where a picture placeholder exists but has no data
            var validPictures = item.Pictures
                .Where(p => !string.IsNullOrEmpty(p.Data))
                .ToList();

            if (validPictures.Count == 0)
            {
                // No valid pictures - clear the array
                itemJson["Pictures"] = new JArray();
                return;
            }

            if (picturesArray == null || picturesArray.Count == 0)
            {
                // Need to add pictures where none existed
                picturesArray = new JArray();
                itemJson["Pictures"] = picturesArray;
            }

            // Rebuild the pictures array from scratch with only valid pictures
            picturesArray = new JArray();
            itemJson["Pictures"] = picturesArray;

            for (int pIdx = 0; pIdx < validPictures.Count; pIdx++)
            {
                var picture = validPictures[pIdx];
                var pictureJson = new JObject();
                picturesArray.Add(pictureJson);

                // Map model fields to INS field names
                // Image = base64 image data
                if (picture.Data != null)
                {
                    pictureJson["Image"] = picture.Data;
                }

                // Description = the comment/description text
                if (picture.Comment != null)
                {
                    pictureJson["Description"] = picture.Comment;
                }

                // Caption = picture title (default to "Picture" if not set)
                if (picture.Title != null)
                {
                    pictureJson["Caption"] = picture.Title;
                }
                else if (pictureJson["Caption"] == null)
                {
                    pictureJson["Caption"] = "Picture";
                }

                // Ensure Path exists (INS format expects it)
                if (pictureJson["Path"] == null)
                {
                    pictureJson["Path"] = null;
                }

                // Filename for the picture (required by INSPECT 2022)
                if (picture.Filename != null)
                {
                    pictureJson["Filename"] = picture.Filename;
                }

                // SortOrder for picture ordering
                pictureJson["SortOrder"] = picture.SortOrder;
            }
        }

        /// <summary>
        /// Ensures required top-level fields exist for INSPECT2022/Strand compatibility.
        /// Files that were never uploaded (Version=0) may be missing these.
        /// </summary>
        private void EnsureRequiredTopLevelFields(string targetPath)
        {
            if (_originalJson!["Filename"] == null || _originalJson["Filename"]!.Type == JTokenType.Null)
            {
                _originalJson["Filename"] = Path.GetFileName(targetPath);
            }
            if (_originalJson["ErrorCount"] == null)
            {
                _originalJson["ErrorCount"] = 0;
            }
            if (_originalJson["Rejected"] == null)
            {
                _originalJson["Rejected"] = false;
            }
            if (_originalJson["ElectricProvider"] == null)
            {
                _originalJson["ElectricProvider"] = null;
            }
            if (_originalJson["GasProvider"] == null)
            {
                _originalJson["GasProvider"] = null;
            }
            if (_originalJson["WaterProvider"] == null)
            {
                _originalJson["WaterProvider"] = null;
            }
        }

        /// <summary>
        /// Ensures every item in the original JSON has an ItemResultId.
        /// Strand assigns these on first upload, but INSPECT2022 requires them to open files.
        /// For items missing them (Version=0 files), generate new GUIDs.
        /// </summary>
        private void EnsureItemResultIds()
        {
            var sectionsArray = _originalJson!["Sections"] as JArray;
            if (sectionsArray == null) return;

            foreach (var section in sectionsArray)
            {
                var itemsArray = section["Items"] as JArray;
                if (itemsArray == null) continue;

                foreach (var item in itemsArray)
                {
                    if (item is JObject itemObj)
                    {
                        if (itemObj["ItemResultId"] == null || 
                            itemObj["ItemResultId"]!.Type == JTokenType.Null ||
                            string.IsNullOrEmpty(itemObj["ItemResultId"]!.ToString()))
                        {
                            itemObj["ItemResultId"] = Guid.NewGuid().ToString();
                        }
                    }
                }
            }
        }

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
