using InspectionEditor.Models;

namespace InspectionEditor.Services
{
    internal static class ChecklistItemVisibility
    {
        // F-004 F1/F2: only Hide (never true in observed data) keeps a row out, as in INSPECT's section builder.
        // HidePicturesButton, HideCommentsButton and HideAddButton never hide a row.
        public static bool ShouldShow(Item item) => !ItemControls.IsHidden(item);
    }
}
