using System;
using System.Linq;
using InspectionEditor.Models;
using Newtonsoft.Json.Linq;

namespace InspectionEditor.Services
{
    // F-008 CPR Concrete Pour (FormId 27). The "Test cylinders included" switch (RTC B27a, owner Q-14): item ItemId 1615
    // "Yes" means ServiceId 77, "No" means ServiceId 61; a cleared value leaves ServiceId as it is. INSPECT applies it in
    // the item's click handler only; RED applies it on every path that changed the item (tap, bulk, template, AI fill),
    // by comparing the item with its last saved value right before each save. ServiceId is never touched on open, so an
    // existing Yes/61 file stays as it is until item 1615 is changed in RED (SPEC §4).
    public static class CprRules
    {
        public const int ConcretePourFormId = 27;
        public const int CylindersItemId = 1615;
        public const int ServiceWithCylinders = 77;
        public const int ServiceWithoutCylinders = 61;

        // Hooks the switch into a save service: before every save, ServiceId follows a changed item 1615 (C1: the only
        // top-level key this feature writes, and only 61 or 77).
        public static void Install(SurgicalSaveService saveService)
        {
            saveService.BeforeSave = inspection =>
            {
                int? serviceId = CylinderServiceId(inspection, saveService.SavedValue);
                if (serviceId.HasValue)
                {
                    saveService.SetHeaderValue("ServiceId", new JValue(serviceId.Value));
                    inspection.ServiceId = serviceId.Value;
                }
            };
        }

        // The ServiceId to write, or null when nothing should change.
        public static int? CylinderServiceId(InspectionFile inspection, Func<Item, JToken?> savedValue)
        {
            if (inspection.FormId != ConcretePourFormId || inspection.Sections == null)
                return null;
            var item = inspection.Sections.Where(s => s?.Items != null).SelectMany(s => s.Items).FirstOrDefault(i => i != null && i.ItemId == CylindersItemId && !i.IsCopied);
            if (item == null)
                return null;
            string current = item.Value?.ToString() ?? "";
            JToken? saved = savedValue(item);
            string before = saved == null || saved.Type == JTokenType.Null ? "" : saved.ToString();
            if (string.Equals(current, before, StringComparison.Ordinal))
                return null; // not changed in RED since the last load or save
            return current switch
            {
                "Yes" => ServiceWithCylinders,
                "No" => ServiceWithoutCylinders,
                _ => null
            };
        }
    }
}
