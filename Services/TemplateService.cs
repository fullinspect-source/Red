using InspectionEditor.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace InspectionEditor.Services
{
    /// <summary>
    /// Manages inspection templates - saving, loading, and applying default values.
    /// Templates are stored locally in the app-specific LocalAppData folder.
    /// </summary>
    public class TemplateService
    {
        private static readonly string AppDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppIdentity.AppDataFolderName);
        
        private static readonly string TemplateFilePath = Path.Combine(AppDataPath, "templates.json");
        
        private TemplateStore _store;

        public TemplateService()
        {
            _store = LoadStore();
        }

        /// <summary>
        /// Gets all templates for a specific FormId.
        /// </summary>
        public List<InspectionTemplate> GetTemplatesForForm(int formId)
        {
            return _store.Templates
                .Where(t => t.FormId == formId)
                .OrderBy(t => t.Name)
                .ToList();
        }

        /// <summary>
        /// Gets all templates grouped by FormId.
        /// </summary>
        public Dictionary<int, List<InspectionTemplate>> GetAllTemplatesGrouped()
        {
            return _store.Templates
                .GroupBy(t => t.FormId)
                .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Name).ToList());
        }

        /// <summary>
        /// Normalizes a candidate value for template storage/application.
        /// </summary>
        private string? NormalizeTemplateValue(object? value)
        {
            var str = value?.ToString()?.Trim();
            return string.IsNullOrEmpty(str) ? null : str;
        }

        /// <summary>
        /// Checks if a value is allowed in templates.
        /// Only "Pass" and "NI" are stored/applied — all other status values
        /// are excluded so templates only drive the "pass" or "NI" defaults.
        /// </summary>
        private bool IsAllowedTemplateValue(string value)
        {
            return string.Equals(value, "Pass", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "NI", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsLookupTemplateValue(Item item, string normalizedValue)
        {
            if (item.ValueList == null || item.ValueList.Count == 0)
                return false;

            foreach (var option in item.ValueList)
            {
                if (string.IsNullOrWhiteSpace(option))
                    continue;

                if (string.Equals(option.Trim(), normalizedValue, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private bool TryAddTemplateValue(InspectionTemplate template, Item item)
        {
            var normalized = NormalizeTemplateValue(item.Value);
            if (normalized == null)
                return false;

            if (!IsAllowedTemplateValue(normalized) &&
                !IsLookupTemplateValue(item, normalized) &&
                !IsYesNoTemplateValue(item, normalized))
                return false;

            template.ItemValues[item.ItemId] = normalized;
            var promptKey = GetTemplatePromptKey(item);
            if (!string.IsNullOrWhiteSpace(promptKey))
                template.ItemValuesByPrompt[promptKey] = normalized;
            return true;
        }

        private static string GetTemplatePromptKey(Item item)
        {
            var prompt = item.DisplayLabel ?? item.Name ?? "";
            if (string.IsNullOrWhiteSpace(prompt))
                return "";

            string normalized = Regex.Replace(prompt.ToLowerInvariant(), @"\s+", " ").Trim();
            return Regex.Replace(normalized, @"[^\p{L}\p{Nd}\s?]", "");
        }

        private bool TryApplyTemplateValue(Item item, object? templateValue)
        {
            var normalized = NormalizeTemplateValue(templateValue);
            if (normalized == null)
                return false;

            if (!IsAllowedTemplateValue(normalized) &&
                !IsLookupTemplateValue(item, normalized) &&
                !IsYesNoTemplateValue(item, normalized))
                return false;

            item.Value = normalized;
            return true;
        }

        /// <summary>
        /// Checks if a section should be excluded from template operations.
        /// No longer skips entire sections — use IsAdministrativeItem instead
        /// to allow yes/no checklist items in section 1 to be templated.
        /// </summary>
        private bool IsAdministrativeSection(Section section, string? inspectionCode)
        {
            return false; // item-level filtering replaces section-level skip
        }

        /// <summary>
        /// Checks if an individual item within section 1 (Administrative) is a
        /// header field that should NOT be templated (e.g. superintendent name,
        /// customer name/phone/email). Yes/No and PassFail items in section 1
        /// are allowed through — they are checklist content, not report headers.
        /// </summary>
        private bool IsAdministrativeItem(Section section, Item item, string? inspectionCode)
        {
            bool isSCI = (inspectionCode ?? "").Equals("SCI", StringComparison.OrdinalIgnoreCase);
            bool isAdminSection = !isSCI &&
                (section.Number == "1" ||
                 section.Name?.Trim().Equals("Administrative", StringComparison.OrdinalIgnoreCase) == true);

            if (!isAdminSection) return false;

            // YesNo, PassFail, and Lookup items are checklist/config content — allow them.
            string controlName = (item.ControlName ?? "").ToLower();
            if (controlName is "yesno" or "yesnonani" or "passfail" or "passfailnani" or "lookup")
                return false;

            // Everything else in section 1 is a header field — skip it.
            return true;
        }

        /// <summary>
        /// Returns true if the value is a valid yes/no answer stored by a YesNo control.
        /// These are not Pass/NI and have no ValueList, so they need their own check.
        /// </summary>
        private static bool IsYesNoTemplateValue(Item item, string value)
        {
            string controlName = (item.ControlName ?? "").ToLower();
            if (controlName is not ("yesno" or "yesnonani")) return false;
            return string.Equals(value, "Yes", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "No",  StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "NA",  StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Creates a new template from the current inspection's values.
        /// Excludes Section 1 (Administrative) to avoid saving report-specific
        /// header fields like superintendent, builder, etc.
        /// Only Pass and NI values are saved — all other values are excluded
        /// to prevent overwriting pre-filled data from Inspect 2022.
        /// </summary>
        public InspectionTemplate CreateTemplate(InspectionFile inspection, int formId, string name, string? description = null, string? inspectionCode = null)
        {
            var template = new InspectionTemplate
            {
                FormId = formId,
                Name = name,
                Description = description
            };

            // Extract values from checklist items (skip free-text administrative fields)
            foreach (var section in inspection.Sections)
            {
                foreach (var item in section.Items)
                {
                    if (IsAdministrativeItem(section, item, inspectionCode)) continue;
                    TryAddTemplateValue(template, item);
                }
            }

            _store.Templates.Add(template);
            SaveStore();

            return template;
        }

        /// <summary>
        /// Updates an existing template with current inspection values.
        /// Excludes Section 1 (Administrative) to avoid saving report-specific header fields.
        /// Only Pass and NI values are saved.
        /// </summary>
        public void UpdateTemplate(string templateId, InspectionFile inspection, string? inspectionCode = null)
        {
            var template = _store.Templates.FirstOrDefault(t => t.Id == templateId);
            if (template == null) return;

            template.ItemValues.Clear();
            template.ItemValuesByPrompt.Clear();
            template.Modified = DateTime.Now;

            foreach (var section in inspection.Sections)
            {
                foreach (var item in section.Items)
                {
                    if (IsAdministrativeItem(section, item, inspectionCode)) continue;
                    TryAddTemplateValue(template, item);
                }
            }

            SaveStore();
        }

        /// <summary>
        /// Applies a template to an inspection, setting default values.
        /// Only applies Pass and NI values, not comments or pictures.
        /// Skips Section 1 (Administrative) to protect report-specific header fields.
        /// </summary>
        /// <returns>Number of items updated</returns>
        public int ApplyTemplate(InspectionTemplate template, InspectionFile inspection, string? inspectionCode = null)
        {
            int updatedCount = 0;

            foreach (var section in inspection.Sections)
            {
                foreach (var item in section.Items)
                {
                    if (IsAdministrativeItem(section, item, inspectionCode)) continue;
                    object? templateValue = null;
                    bool foundValue = template.ItemValues.TryGetValue(item.ItemId, out templateValue);

                    if (!foundValue)
                    {
                        var promptKey = GetTemplatePromptKey(item);
                        foundValue = !string.IsNullOrWhiteSpace(promptKey) &&
                                     template.ItemValuesByPrompt.TryGetValue(promptKey, out templateValue);
                    }

                    if (foundValue)
                    {
                        if (TryApplyTemplateValue(item, templateValue))
                            updatedCount++;
                    }
                }
            }

            return updatedCount;
        }

        /// <summary>
        /// Renames a template.
        /// </summary>
        public void RenameTemplate(string templateId, string newName)
        {
            var template = _store.Templates.FirstOrDefault(t => t.Id == templateId);
            if (template != null)
            {
                template.Name = newName;
                template.Modified = DateTime.Now;
                SaveStore();
            }
        }

        /// <summary>
        /// Deletes a template.
        /// </summary>
        public void DeleteTemplate(string templateId)
        {
            _store.Templates.RemoveAll(t => t.Id == templateId);
            SaveStore();
        }

        /// <summary>
        /// Checks if any templates exist for a given FormId.
        /// </summary>
        public bool HasTemplatesForForm(int formId)
        {
            return _store.Templates.Any(t => t.FormId == formId);
        }

        /// <summary>
        /// Gets the count of templates for a FormId.
        /// </summary>
        public int GetTemplateCountForForm(int formId)
        {
            return _store.Templates.Count(t => t.FormId == formId);
        }

        private TemplateStore LoadStore()
        {
            try
            {
                if (File.Exists(TemplateFilePath))
                {
                    var json = File.ReadAllText(TemplateFilePath);
                    return JsonConvert.DeserializeObject<TemplateStore>(json) ?? new TemplateStore();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading templates: {ex.Message}");
            }

            return new TemplateStore();
        }

        private static readonly object _templateWriteLock = new object();
        
        private void SaveStore()
        {
            try
            {
                // Ensure directory exists
                Directory.CreateDirectory(AppDataPath);

                var json = JsonConvert.SerializeObject(_store, Formatting.Indented);
                lock (_templateWriteLock)
                {
                    File.WriteAllText(TemplateFilePath, json);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving templates: {ex.Message}");
                throw;
            }
        }
    }
}
