using System;
using System.Collections.Generic;
using System.Linq;

namespace InspectionEditor.Services
{
    /// <summary>
    /// Owner 2026-10-19 (fix list p2): Strand can offer two forms with the same name for one service, e.g. "HERS Energy
    /// Rough" as FormId 96 (items 2.2-2.5 are text-and-NI fields) and FormId 299 (2.3-2.5 are Yes/No). INSPECT2026 and
    /// Inspect2022 show both under the same name: their Choose Form window and Add wizard list every form the server
    /// (or OfflineForms.json) returns, and the form data has no inactive flag, version or date to tell them apart. RED
    /// does not guess which one is wrong. When names repeat, each choice shows its form number so the inspector can
    /// tell them apart; names that occur once are shown unchanged.
    /// </summary>
    public static class FormChoiceLabels
    {
        public static string Label(string name, int formId) => $"{name}  (form {formId})";

        /// <summary>The label for each form, in the given order.</summary>
        public static List<string> Labels(IReadOnlyList<(string Name, int FormId)> forms)
        {
            var repeated = new HashSet<string>(
                forms.GroupBy(f => Key(f.Name), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key),
                StringComparer.OrdinalIgnoreCase);
            return forms.Select(f => repeated.Contains(Key(f.Name)) ? Label(Key(f.Name), f.FormId) : f.Name).ToList();
        }

        private static string Key(string? name) => (name ?? "").Trim();
    }
}
