using InspectionEditor.Models;
using System.Linq;

namespace InspectionEditor.Services
{
    internal static class ChecklistItemVisibility
    {
        public static bool ShouldShow(Item item)
        {
            if (!item.HidePicturesButton || !item.HideCommentsButton)
                return true;

            // Some STRAND forms, including Buyer Walk serial-number rows, hide both
            // normal edit buttons while still storing a real photo for that row.
            // Surface those rows so the existing photo viewer and AI OCR remain usable.
            return item.Pictures?.Any(p => !string.IsNullOrWhiteSpace(p.Data)) == true;
        }
    }
}
