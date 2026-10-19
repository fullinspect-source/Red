using InspectionEditor.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace InspectionEditor.Services
{
    // F-004 SPEC §3: how INSPECT edits each item ControlName and which values it stores.
    public enum ItemControlKind
    {
        Checkboxes, Text, Memo, Lookup, HomeReadiness, SearchLookup, NumberPad, Time, Date,
        PictureUpload, DocumentUpload, DocumentButton, Button, Ink, Unsupported
    }

    public enum NumberPadOutcome { Changed, Unchanged, TooLarge }

    public sealed class ItemControl
    {
        internal ItemControl(string name, ItemControlKind kind, string[]? choices = null, bool niToggle = false,
            int maxLength = 0, bool rewritesNa = true)
        {
            Name = name;
            Kind = kind;
            Choices = choices;
            HasNiToggle = niToggle;
            MaxLength = maxLength;
            RewritesNaToNi = rewritesNa;
        }

        public string Name { get; }
        public ItemControlKind Kind { get; }
        // Checkbox choices in INSPECT's order; null for every other kind.
        public IReadOnlyList<string>? Choices { get; }
        // INSPECT's NI checkbox beside a text, lookup or number pad editor (the *Ni and *NaNi controls, RTC B32/B34/B37).
        public bool HasNiToggle { get; }
        // 0 means no limit.
        public int MaxLength { get; }
        // SPEC V1: INSPECT's StandardItemControl rewrites a stored "NA" to "NI" on load. Not BWCheck, ink, plain PassFail
        // or names INSPECT cannot instantiate.
        public bool RewritesNaToNi { get; }
        public bool IsCheckboxes => Kind == ItemControlKind.Checkboxes;
    }

    public static class ItemControls
    {
        public static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

        public const double NumberPadMaximum = 99999;
        public const string NumberTooLargeMessage = "Value cannot be greater than 99999.00";
        public const string NumberTooLargeCaption = "Value Too Large";
        public const string ItemFixedMessage = "If this item was fixed, would you like to remove the Comments and Pictures?";
        public const string ItemFixedCaption = "Item Fixed Confirmation";
        public const string UploadPictureCaption = "Upload Picture";
        public const string ChangePictureCaption = "Change Picture";
        public const string UploadedPictureValue = "[Uploaded]";
        public const string UploadFileCaption = "Upload File";
        public const string ChangeFileCaption = "Change File";
        public const int TextMaxLength = 100;
        public const int MemoMaxLength = 1000;

        private static readonly string[] PassFailNi = { "Pass", "Fail", "NI" };
        private static readonly string[] YesNo = { "Yes", "No" };
        public static readonly IReadOnlyList<string> HomeReadinessChoices =
            Enumerable.Range(1, 10).Select(n => n.ToString(CultureInfo.InvariantCulture)).ToArray();

        // TextControl::UsePhoneMask (RTC B32): exact item names (string op_Equality) get the RegEx mask
        // "\(\d{3}\) [0-9A-Z]{3}-[0-9A-Z]{4}" (Inspect2026.dll #US literal); the stored value keeps the literals.
        public static readonly IReadOnlyList<string> PhoneMaskItemNames = new[] { "Customer Phone", "Customer Phone 2", "Sales Agent Phone" };

        public static bool UsesPhoneMask(Item item) =>
            For(item).Kind == ItemControlKind.Text && item.Name != null && PhoneMaskItemNames.Contains(item.Name, StringComparer.Ordinal);

        /// <summary>
        /// INSPECT's phone mask applied to typed text: three digits, then seven digits or letters (upper-cased), shown as
        /// "(555) 010-0100". Literals appear as the next character is typed, so a partial entry stays editable.
        /// </summary>
        public static string ApplyPhoneMask(string? text)
        {
            var chars = new StringBuilder();
            foreach (char raw in text ?? "")
            {
                if (chars.Length == 10) break;
                char ch = char.ToUpperInvariant(raw);
                bool digit = ch >= '0' && ch <= '9';
                bool letter = ch >= 'A' && ch <= 'Z';
                if (digit || letter && chars.Length >= 3) chars.Append(ch);
            }
            string c = chars.ToString();
            if (c.Length == 0) return "";
            if (c.Length <= 3) return "(" + c;
            if (c.Length <= 6) return "(" + c[..3] + ") " + c[3..];
            return "(" + c[..3] + ") " + c[3..6] + "-" + c[6..];
        }

        // INSPECT resolves "SAE.Inspect.Controls.Item.{ControlName}Control" by reflection (RTC B18), so names match exactly.
        private static readonly Dictionary<string, ItemControl> Known = new[]
        {
            new ItemControl("PassFailNi", ItemControlKind.Checkboxes, PassFailNi),
            new ItemControl("PassFailNaNi", ItemControlKind.Checkboxes, PassFailNi),
            // No INSPECT control (RTC B25); RED renders Pass/Fail so required items can be completed (owner Q-11).
            new ItemControl("PassFail", ItemControlKind.Checkboxes, new[] { "Pass", "Fail" }, rewritesNa: false),
            new ItemControl("YesNo", ItemControlKind.Checkboxes, YesNo),
            // YesNoNiControl loads the PassFailNi checkbox set (RTC B23).
            new ItemControl("YesNoNi", ItemControlKind.Checkboxes, PassFailNi),
            new ItemControl("YesNoNaNi", ItemControlKind.Checkboxes, PassFailNi),
            new ItemControl("CPRYesNo", ItemControlKind.Checkboxes, YesNo),
            new ItemControl("BWCheck", ItemControlKind.Checkboxes, new[] { "Discussed", "NA" }, rewritesNa: false),
            new ItemControl("Text", ItemControlKind.Text, maxLength: TextMaxLength),
            new ItemControl("TextNi", ItemControlKind.Text, niToggle: true, maxLength: TextMaxLength),
            new ItemControl("TextNaNi", ItemControlKind.Text, niToggle: true, maxLength: TextMaxLength),
            new ItemControl("Memo", ItemControlKind.Memo, maxLength: MemoMaxLength),
            new ItemControl("Lookup", ItemControlKind.Lookup),
            new ItemControl("LookupNi", ItemControlKind.Lookup, niToggle: true),
            new ItemControl("LookupNaNi", ItemControlKind.Lookup, niToggle: true),
            new ItemControl("HomeReadiness", ItemControlKind.HomeReadiness),
            new ItemControl("SearchLookup", ItemControlKind.SearchLookup),
            new ItemControl("NumberPad", ItemControlKind.NumberPad),
            new ItemControl("NumberPadNi", ItemControlKind.NumberPad, niToggle: true),
            new ItemControl("NumberPadNaNi", ItemControlKind.NumberPad, niToggle: true),
            new ItemControl("Time", ItemControlKind.Time),
            new ItemControl("Date", ItemControlKind.Date),
            new ItemControl("PictureUpload", ItemControlKind.PictureUpload),
            new ItemControl("DocumentUpload", ItemControlKind.DocumentUpload),
            new ItemControl("DocumentButton", ItemControlKind.DocumentButton),
            new ItemControl("Button", ItemControlKind.Button),
            // Ink items use INSPECT's older BaseControl (RTC B41); F-006.
            new ItemControl("Signature", ItemControlKind.Ink, rewritesNa: false),
            new ItemControl("Sketch", ItemControlKind.Ink, rewritesNa: false),
            new ItemControl("FoundationSketch", ItemControlKind.Ink, rewritesNa: false),
        }.ToDictionary(c => c.Name, StringComparer.Ordinal);

        // The checkbox controls INSPECT's section "All" labels act on (RTC B40), and the NI-toggle controls NI also flips.
        private static readonly HashSet<string> AllLabelCheckboxNames =
            new(StringComparer.Ordinal) { "BWCheck", "CPRYesNo", "PassFailNaNi", "YesNo", "YesNoNaNi" };
        private static readonly HashSet<string> AllLabelNiToggleNames =
            new(StringComparer.Ordinal) { "LookupNaNi", "NumberPadNaNi", "TextNaNi" };
        // INSPECT's CheckboxesControl children, used for SPEC SC3 (PassFail is not rendered by INSPECT).
        private static readonly HashSet<string> SectionCheckboxNames =
            new(StringComparer.Ordinal) { "PassFailNi", "PassFailNaNi", "YesNo", "YesNoNi", "YesNoNaNi", "CPRYesNo", "BWCheck" };

        public static ItemControl Resolve(string? controlName) =>
            controlName != null && Known.TryGetValue(controlName, out var control)
                ? control
                : new ItemControl(controlName ?? "", ItemControlKind.Unsupported, rewritesNa: false);

        public static ItemControl For(Item item)
        {
            var control = Resolve(item.ControlName);
            // Grid cells are drawn by their group control, which ignores the item's ControlName (RTC B51: TruckPlacement
            // cells say "PassFail" but hold times and numbers). Until F-005 draws the grids they are plain text rows.
            if (control.Kind == ItemControlKind.Checkboxes && IsGroupMember(item))
                return new ItemControl(control.Name, ItemControlKind.Text, rewritesNa: false);
            return control;
        }

        public static string UnsupportedBadge(Item item) => $"Unsupported control: {item.ControlName}";

        public static string? ValueText(Item item) => item.Value switch
        {
            null => null,
            string text => text,
            JValue { Value: string text } => text,
            JValue { Type: JTokenType.Null } => null,
            _ => item.Value.ToString()
        };

        // ── Item flags kept in ExtensionData ────────────────────────────────────────────────────────────────

        public static bool IsHidden(Item item) => GetBool(item, "Hide");

        // F-005 grid member: consecutive items with the same non-empty ItemGroupId (RTC B20).
        public static bool IsGroupMember(Item item) => !string.IsNullOrEmpty(GetString(item, "ItemGroupId"));

        // Newtonsoft keeps unknown primitives in ExtensionData as CLR values (bool, string, long), objects as JTokens.
        public static bool IsReinspection(InspectionFile? inspection) =>
            inspection?.ExtensionData != null && inspection.ExtensionData.TryGetValue("IsReinspection", out var value) &&
            (value is true || value is JValue { Type: JTokenType.Boolean, Value: true });

        public static string? GetString(Item item, string key) =>
            item.ExtensionData != null && item.ExtensionData.TryGetValue(key, out var value)
                ? value switch { null => null, JValue { Type: JTokenType.Null } => null, JValue v => v.Value?.ToString(), _ => value.ToString() }
                : null;

        private static bool GetBool(Item item, string key) =>
            item.ExtensionData != null && item.ExtensionData.TryGetValue(key, out var value) &&
            (value is true || value is JValue { Type: JTokenType.Boolean, Value: true });

        // ── SPEC V1: "NA" becomes "NI" when the inspection opens ───────────────────────────────────────────

        /// <summary>
        /// Rewrites every exact "NA" to "NI" in memory on the items INSPECT renders through StandardItemControl (RTC B24):
        /// not BWCheck, ink, plain PassFail or unknown controls, not hidden items and not grid members (F-005 groups are
        /// drawn by group controls). "N/A" and other spellings stay. Returns the rewritten items.
        /// </summary>
        public static List<Item> RewriteNaOnOpen(InspectionFile inspection)
        {
            var rewritten = new List<Item>();
            foreach (var section in inspection.Sections ?? new List<Section>())
            {
                if (section?.Items == null) continue;
                foreach (var item in section.Items)
                {
                    if (item == null || !string.Equals(ValueText(item), "NA", StringComparison.Ordinal)) continue;
                    if (!For(item).RewritesNaToNi || IsHidden(item) || IsGroupMember(item)) continue;
                    item.Value = "NI";
                    rewritten.Add(item);
                }
            }
            return rewritten;
        }

        // ── Checkboxes ─────────────────────────────────────────────────────────────────────────────────────

        public static bool IsSelected(Item item, string choice) =>
            string.Equals(ValueText(item), choice, StringComparison.OrdinalIgnoreCase);

        // Clicking the selected choice clears the value to null (RTC B26); any other choice is stored as shown.
        public static string? NextChoiceValue(Item item, string choice) => IsSelected(item, choice) ? null : choice;

        // NI toggle of the *Ni controls: NI ↔ null (RTC B32).
        public static string? NextNiToggleValue(Item item) =>
            string.Equals(ValueText(item), "NI", StringComparison.OrdinalIgnoreCase) ? null : "NI";

        // SPEC V3: choosing Pass or Discussed on a reinspection when the item has comments or pictures.
        public static bool AsksItemFixed(InspectionFile? inspection, Item item, string newValue) =>
            IsReinspection(inspection) &&
            (string.Equals(newValue, "Pass", StringComparison.Ordinal) || string.Equals(newValue, "Discussed", StringComparison.Ordinal)) &&
            (!string.IsNullOrEmpty(item.Comments) || item.Pictures.Any(p => p != null && !string.IsNullOrEmpty(p.Data)));

        // Yes on the Item Fixed Confirmation: Comments = String.Empty, Pictures = [] (CheckboxesControl::Item_ValueChanged).
        public static void ClearFixedItem(Item item)
        {
            item.Comments = "";
            item.Pictures.Clear();
        }

        /// <summary>
        /// SPEC C1/C2 for RED's bulk writers (templates and the like): the form of <paramref name="value"/> this control
        /// may store, or false when it may not store it. Checkbox values take the control's own spelling.
        /// </summary>
        public static bool TryValueForControl(Item item, string value, out string stored)
        {
            stored = value;
            var control = For(item);
            // C2: "NA" only on BWCheck; "NI" only where the control's set or NI toggle has it (or plain text).
            if (control.Kind != ItemControlKind.Checkboxes && string.Equals(value.Trim(), "NA", StringComparison.OrdinalIgnoreCase))
                return false;
            switch (control.Kind)
            {
                case ItemControlKind.HomeReadiness:
                    string? level = HomeReadinessChoices.FirstOrDefault(c => c == value.Trim());
                    if (level == null) return false;
                    stored = level;
                    return true;
                case ItemControlKind.SearchLookup when value.Equals("NI", StringComparison.OrdinalIgnoreCase):
                    return false;
                case ItemControlKind.Checkboxes:
                    string? choice = control.Choices!.FirstOrDefault(c => c.Equals(value, StringComparison.OrdinalIgnoreCase));
                    if (choice == null) return false;
                    stored = choice;
                    return true;
                case ItemControlKind.NumberPad:
                    if (control.HasNiToggle && value.Equals("NI", StringComparison.OrdinalIgnoreCase)) { stored = "NI"; return true; }
                    if (!TryParseNumber(value, out double number) || number > NumberPadMaximum) return false;
                    stored = FormatNumber(number);
                    return true;
                case ItemControlKind.Time:
                    if (!TryParseTime(value, out DateTime time)) return false;
                    stored = FormatTime(time);
                    return true;
                case ItemControlKind.Date:
                    if (!TryParseDate(value, out DateTime date)) return false;
                    stored = FormatDate(date);
                    return true;
                case ItemControlKind.Text when control.MaxLength > 0 && value.Length > control.MaxLength:
                    return false;
                case ItemControlKind.Memo:
                case ItemControlKind.PictureUpload:
                case ItemControlKind.DocumentUpload:
                case ItemControlKind.DocumentButton:
                case ItemControlKind.Button:
                case ItemControlKind.Ink:
                    return false; // these derive Value from other storage
                default:
                    return true;
            }
        }

        // ── Section "All" labels (SPEC SC3, V4) ───────────────────────────────────────────────────────────

        /// <summary>
        /// The upper-cased "All" labels for a section, or null when INSPECT shows none: the section needs at least one
        /// checkbox item and more than one row. The texts are the choices of the checkbox item with the most choices.
        /// </summary>
        public static IReadOnlyList<string>? SectionAllLabels(Section section, int rowCount)
        {
            if (rowCount <= 1 || section?.Items == null) return null;
            IReadOnlyList<string>? best = null;
            foreach (var item in section.Items)
            {
                if (item == null || item.ControlName == null || !SectionCheckboxNames.Contains(item.ControlName)) continue;
                var choices = Resolve(item.ControlName).Choices!;
                if (best == null || choices.Count > best.Count) best = choices;
            }
            return best?.Select(c => c.ToUpperInvariant()).ToArray();
        }

        /// <summary>
        /// INSPECT's ClickLabel_SelectAllCheckboxes: YES, PASS or DISCUSSED pick the first column, NO, FAIL or NA the
        /// second, NI the NI column; an item that already holds the value is cleared. Sets Value directly, with no side
        /// effects. Items in <paramref name="skip"/> are left alone. Returns the items changed.
        /// </summary>
        public static List<Item> ApplySectionAllLabel(Section section, string label, Func<Item, bool>? skip = null)
        {
            var changed = new List<Item>();
            string upper = (label ?? "").Trim().ToUpperInvariant();
            int column = upper switch
            {
                "YES" or "PASS" or "DISCUSSED" => 0,
                "NO" or "FAIL" or "NA" => 1,
                "NI" => 2,
                _ => -1
            };
            if (column < 0 || section?.Items == null) return changed;
            foreach (var item in section.Items)
            {
                if (item == null || item.ControlName == null || skip?.Invoke(item) == true) continue;
                string? target = null;
                if (AllLabelCheckboxNames.Contains(item.ControlName))
                {
                    var choices = Resolve(item.ControlName).Choices!;
                    if (column < 2 && column < choices.Count) target = choices[column];
                    else if (column == 2 && choices.Contains("NI")) target = "NI";
                }
                else if (column == 2 && AllLabelNiToggleNames.Contains(item.ControlName))
                {
                    target = "NI";
                }
                if (target == null) continue;
                item.Value = string.Equals(ValueText(item), target, StringComparison.Ordinal) ? null : target;
                changed.Add(item);
            }
            return changed;
        }

        // ── Number pad (SPEC §4) ──────────────────────────────────────────────────────────────────────────

        public static bool TryParseNumber(string? text, out double number) =>
            double.TryParse((text ?? "").Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite |
                NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out number) && !double.IsNaN(number) && !double.IsInfinity(number);

        // N7: the plain decimal string, en-US digits, "." separator, no thousands separator, no added zeros.
        public static string FormatNumber(double number) => number.ToString(EnUs);

        // N6: display mask N2 (ControlFormatString is null in every observed file).
        public static string FormatNumberDisplay(string? value) =>
            TryParseNumber(value, out double number) ? number.ToString("N2", EnUs) : value ?? "";

        public static bool TryNumberPadFraction(string key, out double fraction)
        {
            fraction = key switch { "¼" or "1/4" => 0.25, "½" or "1/2" => 0.5, "¾" or "3/4" => 0.75, _ => -1 };
            return fraction > 0;
        }

        /// <summary>
        /// One key on INSPECT's keypad (0-9, ¼, ½, ¾, CLEAR): N1 an empty value takes the key's value; N2 a fraction on a
        /// value gives its integer part plus the fraction; N3 a digit is appended only when there is no decimal point;
        /// N4 CLEAR empties the value; N5 a result above 99999 is refused. A value that is not a number (NI) counts as
        /// empty, as INSPECT's keypad shows it blank.
        /// </summary>
        public static NumberPadOutcome PressNumberPadKey(string? current, string key, out string? value)
        {
            value = current;
            if (key == "CLEAR" || key == "C")
            {
                if (string.IsNullOrEmpty(current)) return NumberPadOutcome.Unchanged;
                value = "";
                return NumberPadOutcome.Changed;
            }
            bool isDigit = key.Length == 1 && key[0] >= '0' && key[0] <= '9';
            bool isFraction = TryNumberPadFraction(key, out double fraction);
            if (!isDigit && !isFraction) return NumberPadOutcome.Unchanged;

            string text = (current ?? "").Trim();
            double result;
            if (!TryParseNumber(text, out double number))
                result = isFraction ? fraction : key[0] - '0';
            else if (isFraction)
                result = Math.Truncate(number) + fraction;
            else if (text.Contains('.') || !TryParseNumber(text + key, out result))
                return NumberPadOutcome.Unchanged;

            if (result > NumberPadMaximum) return NumberPadOutcome.TooLarge;
            string formatted = FormatNumber(result);
            if (string.Equals(formatted, current, StringComparison.Ordinal)) return NumberPadOutcome.Unchanged;
            value = formatted;
            return NumberPadOutcome.Changed;
        }

        // Keyboard editor (UseNumberPad false, RTC B38): digits and one decimal point, never above 99999.
        public static bool IsAcceptableNumberText(string text, out bool tooLarge)
        {
            tooLarge = false;
            if (text.Length == 0) return true;
            if (text.Count(c => c == '.') > 1 || text.Any(c => c != '.' && (c < '0' || c > '9'))) return false;
            if (text == ".") return true;
            if (!TryParseNumber(text, out double number)) return false;
            tooLarge = number > NumberPadMaximum;
            return !tooLarge;
        }

        // What the keyboard editor stores for its text: "" stays "", a number its N7 string, anything else unchanged.
        public static string NormalizeTypedNumber(string text) =>
            text.Trim() is "" or "." ? "" : TryParseNumber(text, out double number) ? FormatNumber(number) : text;

        // ── Time and Date (RTC B39, SPEC C3: always en-US) ───────────────────────────────────────────────

        public static string FormatTime(DateTime time) => time.ToString("h:mm tt", EnUs);

        public static string FormatDate(DateTime date) => date.ToString("M/d/yyyy", EnUs);

        public static bool TryParseTime(string? text, out DateTime time) =>
            DateTime.TryParse(text, EnUs, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault, out time);

        public static bool TryParseDate(string? text, out DateTime date) =>
            DateTime.TryParse(text, EnUs, DateTimeStyles.AllowWhiteSpaces, out date);

        // ── PictureUpload (RTC B46) ───────────────────────────────────────────────────────────────────────

        public static bool HasPictures(Item item) => item.Pictures.Any(p => p != null && !string.IsNullOrEmpty(p.Data));

        public static string PictureUploadValue(Item item) => HasPictures(item) ? UploadedPictureValue : "";

        public static string PictureUploadCaption(Item item) => HasPictures(item) ? ChangePictureCaption : UploadPictureCaption;

        // CheckPictureValue after the pictures change: true when Value changed.
        public static bool SyncPictureUploadValue(Item item)
        {
            if (For(item).Kind != ItemControlKind.PictureUpload) return false;
            string value = PictureUploadValue(item);
            if (string.Equals(ValueText(item), value, StringComparison.Ordinal)) return false;
            item.Value = value;
            return true;
        }

        // ── DocumentUpload (RTC B47, owner Q-13 default (a)) ──────────────────────────────────────────────

        public static string DocumentUploadCaption(Item item)
        {
            if (!string.IsNullOrWhiteSpace(ValueText(item))) return ChangeFileCaption;
            string? buttonText = GetString(item, "ButtonText");
            return string.IsNullOrEmpty(buttonText) ? UploadFileCaption : buttonText;
        }

        // INSPECT stores the file bytes decoded as UTF-8 in Value (lossy for binary files) and the file name in Template.
        public static void StoreDocumentUpload(Item item, string fileName, byte[] bytes)
        {
            item.Value = Encoding.UTF8.GetString(bytes);
            item.ExtensionData ??= new Dictionary<string, object>();
            item.ExtensionData["Template"] = new JValue(fileName);
        }

        // ── SearchLookup (RTC B36, owner Q-16 default) ────────────────────────────────────────────────────

        // The list shown: ValueList sorted, plus the current value when it is missing. Display only; the model is not
        // re-sorted (whether INSPECT persists its in-place sort is SPEC §11 research).
        public static List<string> SearchLookupChoices(Item item)
        {
            var choices = (item.ValueList ?? new List<string>()).Where(v => !string.IsNullOrEmpty(v))
                .Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase).ToList();
            string? current = ValueText(item);
            if (!string.IsNullOrEmpty(current) && !choices.Contains(current, StringComparer.Ordinal)) choices.Add(current);
            return choices;
        }

        // "Add New": appends the typed text to the item's ValueList and selects it. False when the text is blank.
        public static bool AddSearchLookupValue(Item item, string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            item.ValueList ??= new List<string>();
            if (!item.ValueList.Contains(text, StringComparer.Ordinal)) item.ValueList.Add(text);
            item.Value = text;
            return true;
        }

        // INSPECT's Inspection.GetSuperintendentItem match (F-013 copies it to SuperintendentName).
        public static bool IsSuperintendentItem(Item item) =>
            string.Equals(item.Name, "SUPERINTENDENT", StringComparison.OrdinalIgnoreCase);

        // ── Memo (RTC B33) ────────────────────────────────────────────────────────────────────────────────

        // The text lives in Comments; Value is "ok" when the text is not blank, else null.
        public static string? MemoValueFor(string? comments) => string.IsNullOrWhiteSpace(comments) ? null : "ok";

        // ── Copies (SPEC K3, K4) ──────────────────────────────────────────────────────────────────────────

        public static bool CanCopy(Item item) => !IsGroupMember(item);

        public static bool CanRemove(Section? section, Item item) =>
            section?.Items != null && !IsGroupMember(item) &&
            section.Items.Count(other => other != null && other.ItemId == item.ItemId) > 1;
    }
}
