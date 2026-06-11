using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace InspectionEditor.Models
{
    public class InspectionFile
    {
        [JsonProperty("InspectVersion")]
        public string? InspectVersion { get; set; }

        [JsonProperty("WorkOrderId")]
        public string? WorkOrderId { get; set; }

        [JsonProperty("InspectionName")]
        public string? InspectionName { get; set; }

        [JsonProperty("InspectionNumber")]
        public string? InspectionNumber { get; set; }

        [JsonProperty("InspectionCode")]
        public string? InspectionCode { get; set; }

        [JsonProperty("FormId")]
        public int FormId { get; set; }

        [JsonProperty("ServiceId")]
        public int ServiceId { get; set; }

        [JsonProperty("Address")]
        public string? Address { get; set; }

        [JsonProperty("City")]
        public string? City { get; set; }

        [JsonProperty("State")]
        public string? State { get; set; }

        [JsonProperty("Contact")]
        public string? Contact { get; set; }

        [JsonProperty("BuilderName")]
        public string? BuilderName { get; set; }

        [JsonProperty("Project")]
        public string? Project { get; set; }

        [JsonProperty("DateInspected")]
        public string? DateInspected { get; set; }

        [JsonProperty("Sections")]
        public List<Section> Sections { get; set; } = new List<Section>();

        [JsonProperty("Events")]
        public List<Event>? Events { get; set; }

        [JsonProperty("Attachments")]
        public List<object>? Attachments { get; set; }

        // Keep all other properties as dynamic to preserve them
        [JsonExtensionData]
        public Dictionary<string, object>? ExtensionData { get; set; }
    }

    public class Section
    {
        [JsonProperty("SectionId")]
        public int SectionId { get; set; }

        [JsonProperty("Name")]
        public string? Name { get; set; }

        [JsonProperty("ControlName")]
        public string? ControlName { get; set; }

        [JsonProperty("Number")]
        public string? Number { get; set; }

        [JsonProperty("Items")]
        public List<Item> Items { get; set; } = new List<Item>();

        [JsonProperty("SortOrder")]
        public int SortOrder { get; set; }
    }

    public class Item
    {
        [JsonProperty("ItemId")]
        public int ItemId { get; set; }

        [JsonProperty("Name")]
        public string? Name { get; set; }

        [JsonProperty("Number")]
        public string? Number { get; set; }

        [JsonProperty("ControlName")]
        public string? ControlName { get; set; }

        [JsonProperty("DisplayLabel")]
        public string? DisplayLabel { get; set; }

        [JsonProperty("Value")]
        public object? Value { get; set; }

        [JsonProperty("Comments")]
        public string? Comments { get; set; }

        [JsonProperty("ValueList")]
        public List<string>? ValueList { get; set; }

        [JsonProperty("Pictures")]
        public List<Picture> Pictures { get; set; } = new List<Picture>();

        [JsonProperty("Required")]
        public bool Required { get; set; }

        [JsonProperty("IsPictureRequired")]
        public bool IsPictureRequired { get; set; }

        [JsonProperty("HidePicturesButton")]
        public bool HidePicturesButton { get; set; }

        [JsonProperty("HideCommentsButton")]
        public bool HideCommentsButton { get; set; }

        [JsonProperty("SortOrder")]
        public int SortOrder { get; set; }

        [JsonProperty("IsCopied")]
        public bool IsCopied { get; set; }

        [JsonProperty("ResultSortOrder")]
        public int ResultSortOrder { get; set; }

        // Keep all other properties to preserve them
        [JsonExtensionData]
        public Dictionary<string, object>? ExtensionData { get; set; }
    }

    public class Picture
    {
        [JsonProperty("PictureId")]
        public object? PictureId { get; set; } // Can be int or GUID string

        [JsonProperty("Title")]
        public string? Title { get; set; }

        [JsonProperty("Comment")]
        public string? Comment { get; set; }

        [JsonProperty("Filename")]
        public string? Filename { get; set; }

        [JsonProperty("Data")]
        public string? Data { get; set; } // Base64 image data

        [JsonProperty("ThumbnailData")]
        public string? ThumbnailData { get; set; }

        [JsonProperty("SortOrder")]
        public int SortOrder { get; set; }

        [JsonExtensionData]
        public Dictionary<string, object>? ExtensionData { get; set; }
    }

    public class Event
    {
        [JsonProperty("Date")]
        public string? Date { get; set; }

        [JsonProperty("EventTypeId")]
        public int EventTypeId { get; set; }

        [JsonProperty("Username")]
        public string? Username { get; set; }

        [JsonProperty("UserDisplayName")]
        public string? UserDisplayName { get; set; }

        [JsonProperty("AdditionalInfo")]
        public string? AdditionalInfo { get; set; }
    }
}
