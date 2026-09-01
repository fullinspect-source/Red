using Newtonsoft.Json.Linq;
using System.Collections.Generic;

namespace InspectionEditor.Models
{
    public enum PlanCheckState
    {
        Incomplete,
        Confirmed,
        Deficient
    }

    public sealed class PlanPdfAttachment
    {
        public JObject Source { get; init; } = new JObject();
        public byte[] PdfBytes { get; init; } = System.Array.Empty<byte>();
        public string Filename { get; init; } = "Embedded plan.pdf";
    }

    public sealed class PlanCheckFinding
    {
        public string Id { get; init; } = "";
        public string Label { get; init; } = "";
        public string SearchDescription { get; init; } = "";
        public PlanCheckState State { get; set; }
        public int PageIndex { get; set; }
        public double X { get; set; } = 0.5;
        public double Y { get; set; } = 0.5;
        public bool IsSuggested { get; set; }
        public string SuggestionText { get; set; } = "No text suggestion found — place marker manually.";
        public bool AddScreenshotToItem { get; set; } = true;
    }

    public sealed class PlanCheckScreenshot
    {
        public byte[] PngBytes { get; init; } = System.Array.Empty<byte>();
        public string Filename { get; init; } = "plan-check.png";
        public string Caption { get; init; } = "Plan check deficiency";
        public bool AddToCurrentItem { get; init; }
    }

    public sealed class PlanCheckResult
    {
        public List<PlanCheckScreenshot> Screenshots { get; } = new();
        public List<PlanCheckFinding> Findings { get; } = new();
        public JObject? AnnotatedAttachment { get; set; }
    }
}
