using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace InspectionEditor.Models
{
    /// <summary>
    /// Represents a saved inspection template containing default values for items.
    /// Templates are keyed by FormId to ensure checklist structure compatibility.
    /// </summary>
    public class InspectionTemplate
    {
        [JsonProperty("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("formId")]
        public int FormId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; } = "Default";

        [JsonProperty("description")]
        public string? Description { get; set; }

        [JsonProperty("created")]
        public DateTime Created { get; set; } = DateTime.Now;

        [JsonProperty("modified")]
        public DateTime Modified { get; set; } = DateTime.Now;

        /// <summary>
        /// Maps ItemId to its default Value (pass/fail/NI status or a selectable lookup value).
        /// Only stores items that have a value set and are eligible for templates.
        /// </summary>
        [JsonProperty("itemValues")]
        public Dictionary<int, object?> ItemValues { get; set; } = new Dictionary<int, object?>();

        /// <summary>
        /// Maps normalized checklist prompt text to its default Value. This makes
        /// templates survive checklist number/id drift between Inspect versions.
        /// </summary>
        [JsonProperty("itemValuesByPrompt")]
        public Dictionary<string, object?> ItemValuesByPrompt { get; set; } = new Dictionary<string, object?>();
    }

    /// <summary>
    /// Container for all templates, stored as a single JSON file.
    /// </summary>
    public class TemplateStore
    {
        [JsonProperty("version")]
        public int Version { get; set; } = 1;

        [JsonProperty("templates")]
        public List<InspectionTemplate> Templates { get; set; } = new List<InspectionTemplate>();
    }
}
