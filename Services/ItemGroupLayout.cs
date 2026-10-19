using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using InspectionEditor.Models;
using Newtonsoft.Json.Linq;

namespace InspectionEditor.Services
{
    // F-005: INSPECT's four item-group grids (TruckPlacement, SlabExtMeasures, SlabIntMeasures,
    // DrainageVerification). UI-independent so the rules can be tested without WPF.
    public enum ItemGroupKind { Unknown, TruckPlacement, SlabExtMeasures, SlabIntMeasures, DrainageVerification }

    public enum ItemGroupCellKind { Number, Time, Result }

    public sealed class ItemGroupCell
    {
        public ItemGroupCell(Item item, int row, int column, ItemGroupCellKind kind)
        {
            Item = item;
            Row = row;
            Column = column;
            Kind = kind;
        }

        public Item Item { get; }
        public int Row { get; }
        public int Column { get; }
        public ItemGroupCellKind Kind { get; }
    }

    public sealed class ItemGroupRun
    {
        private readonly Dictionary<Item, ItemGroupCell> _cellByItem = new(ReferenceEqualityComparer.Instance);

        internal ItemGroupRun(Section section, int startIndex, List<Item> items, string groupId, string? groupControlName)
        {
            Section = section;
            StartIndex = startIndex;
            Items = items;
            GroupId = groupId;
            GroupControlName = groupControlName;
            Kind = ItemGroupLayout.ParseKind(groupControlName);
        }

        public Section Section { get; }
        public int StartIndex { get; }
        public List<Item> Items { get; }
        public string GroupId { get; }
        public string? GroupControlName { get; }
        public ItemGroupKind Kind { get; }

        // True when RED draws the grid. False: the items render as normal rows (unknown group name, or a known
        // group whose layout is not INSPECT's; then Problem holds the reason and RED shows NotRecognisedText).
        public bool Recognized { get; internal set; }
        public string? Problem { get; internal set; }

        public string Title { get; internal set; } = "";
        public string[] ColumnHeaders { get; internal set; } = Array.Empty<string>();
        public string[] RowLabels { get; internal set; } = Array.Empty<string>();
        public ItemGroupCell?[,] Cells { get; internal set; } = new ItemGroupCell?[0, 0];

        public int RowCount => Cells.GetLength(0);
        public int ColumnCount => Cells.GetLength(1);

        public ItemGroupCell? CellFor(Item item) => _cellByItem.TryGetValue(item, out var cell) ? cell : null;

        public ItemGroupCell? CellAt(int row, int column) =>
            row >= 0 && row < RowCount && column >= 0 && column < ColumnCount ? Cells[row, column] : null;

        internal void SetCell(ItemGroupCell cell)
        {
            Cells[cell.Row, cell.Column] = cell;
            _cellByItem[cell.Item] = cell;
        }

        internal void ClearCells()
        {
            Cells = new ItemGroupCell?[Cells.GetLength(0), Cells.GetLength(1)];
            _cellByItem.Clear();
        }
    }

    public static class ItemGroupLayout
    {
        public const string NotRecognisedText = "Grid layout not recognised";
        public const string RequiredMissingText = "Required value is missing.";

        // TruckPlacement: 15 trucks x Arrival, Gallons, Slump, Temp (RTC B51; FormId 27 section 4).
        public const int TruckCount = 15;
        public static readonly string[] TruckTypes = { "Arrival", "Gallons", "Slump", "Temp" };
        public static readonly string[] TruckHeaders = { "Arrival time", "Gallons", "Slump", "Temp (F)" };

        // DrainageVerification: 14 rows x C, E, F, S, R bound by "{type} {n}" (RTC B54; FormId 133).
        public const int DrainageRowCount = 14;
        public static readonly string[] DrainageTypes =
            { "Calc Point Id", "Expected Elevation", "Field Point Id", "Shot Elevation", "Result" };
        public const int DrainageExpectedColumn = 1;
        public const int DrainageShotColumn = 3;
        public const int DrainageResultColumn = 4;

        // Slab grids: 12 items bound by position, 4 rows of 3 (RTC B52-B53). Row-major order (three values per
        // corner/location) is INFERRED from RED's CPP mapping (8.1, 8.4, 8.7 ... are TOF to TOG) and its
        // per-three progress colours; INSPECT indexes the run with List.get_Item(Int32).
        public const int SlabCellCount = 12;
        public static readonly string[] SlabExtHeaders = { "TOF to TOG", "TOF to BOB", "BW" };
        public static readonly string[] SlabIntHeaders = { "SD", "BW", "BD" };

        public const decimal DrainageTolerance = 0.05m;
        public const string ValueTooLargeText = ItemControls.NumberTooLargeMessage;
        public const string ValueTooLargeCaption = ItemControls.NumberTooLargeCaption;

        private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

        // Cache per section, invalidated when the item list changes (copies, removals, reloads).
        private sealed class CacheEntry
        {
            public Item[] Snapshot = Array.Empty<Item>();
            public List<ItemGroupRun> Runs = new();
        }

        private static readonly ConditionalWeakTable<Section, CacheEntry> Cache = new();

        public static string? GetGroupId(Item item) => ReadExtensionString(item, "ItemGroupId");

        public static string? GetGroupControlName(Item item) => ReadExtensionString(item, "ItemGroupControlName");

        public static ItemGroupKind ParseKind(string? groupControlName) => groupControlName switch
        {
            "TruckPlacement" => ItemGroupKind.TruckPlacement,
            "SlabExtMeasures" => ItemGroupKind.SlabExtMeasures,
            "SlabIntMeasures" => ItemGroupKind.SlabIntMeasures,
            "DrainageVerification" => ItemGroupKind.DrainageVerification,
            _ => ItemGroupKind.Unknown
        };

        // G1: runs of consecutive items in one section sharing the same non-empty ItemGroupId. The grid type is the
        // first item's ItemGroupControlName.
        public static List<ItemGroupRun> Detect(Section section)
        {
            var items = section.Items ?? new List<Item>();
            var entry = Cache.GetOrCreateValue(section);
            if (entry.Snapshot.Length == items.Count && entry.Snapshot.SequenceEqual(items, ReferenceEqualityComparer.Instance))
                return entry.Runs;

            var runs = new List<ItemGroupRun>();
            int i = 0;
            while (i < items.Count)
            {
                string? id = GetGroupId(items[i]);
                if (string.IsNullOrEmpty(id))
                {
                    i++;
                    continue;
                }

                int start = i;
                var runItems = new List<Item>();
                while (i < items.Count && string.Equals(GetGroupId(items[i]), id, StringComparison.Ordinal))
                {
                    runItems.Add(items[i]);
                    i++;
                }

                var run = new ItemGroupRun(section, start, runItems, id, GetGroupControlName(runItems[0]));
                Layout(run);
                runs.Add(run);
            }

            entry.Snapshot = items.ToArray();
            entry.Runs = runs;
            return runs;
        }

        public static ItemGroupRun? FindRun(Section? section, Item item)
        {
            if (section == null)
                return null;
            foreach (var run in Detect(section))
                if (run.CellFor(item) != null || run.Items.Contains(item, ReferenceEqualityComparer.Instance))
                    return run;
            return null;
        }

        public static ItemGroupRun? FindRun(InspectionFile? inspection, Item item)
        {
            if (inspection?.Sections == null)
                return null;
            foreach (var section in inspection.Sections)
                if (section.Items.Contains(item, ReferenceEqualityComparer.Instance))
                    return FindRun(section, item);
            return null;
        }

        // The cell RED draws for this item, or null when the item is not in a drawn grid.
        public static ItemGroupCell? FindCell(InspectionFile? inspection, Item item)
        {
            var run = FindRun(inspection, item);
            return run is { Recognized: true } ? run.CellFor(item) : null;
        }

        // G2: inside a known group INSPECT ignores the per-item ControlName (truck cells carry "PassFail"). RED's
        // ControlName-driven rules (section Pass/NI fills, automatic Fail) must therefore skip these items.
        public static bool IsGroupedItem(Item item) =>
            !string.IsNullOrEmpty(GetGroupId(item)) && ParseKind(GetGroupControlName(item)) != ItemGroupKind.Unknown;

        private static void Layout(ItemGroupRun run)
        {
            switch (run.Kind)
            {
                case ItemGroupKind.TruckPlacement:
                    LayoutByName(run, "Truck placement", TruckHeaders, TruckCount, "Truck",
                        (item) => ParseTruckName(item.Name),
                        column => column == 0 ? ItemGroupCellKind.Time : ItemGroupCellKind.Number);
                    break;
                case ItemGroupKind.DrainageVerification:
                    LayoutByName(run, "Drainage verification", DrainageTypes, DrainageRowCount, "Point",
                        (item) => ParseDrainageName(item.Name),
                        column => column == DrainageResultColumn ? ItemGroupCellKind.Result : ItemGroupCellKind.Number);
                    break;
                case ItemGroupKind.SlabExtMeasures:
                    LayoutSlab(run, "Exterior measurements (nearest 1/4 inch)", SlabExtHeaders, "Corner");
                    break;
                case ItemGroupKind.SlabIntMeasures:
                    LayoutSlab(run, "Interior measurements", SlabIntHeaders, "Location");
                    break;
                default:
                    // Unknown group name: INSPECT draws nothing; RED keeps the rows (F-004) without the grid notice.
                    run.Recognized = false;
                    run.Problem = null;
                    break;
            }
        }

        private static void LayoutByName(ItemGroupRun run, string title, string[] headers, int rowCount, string rowWord,
            Func<Item, (int Row, int Column)?> parse, Func<int, ItemGroupCellKind> kindOf)
        {
            run.Title = title;
            run.ColumnHeaders = headers;
            run.RowLabels = Enumerable.Range(1, rowCount).Select(n => $"{rowWord} {n}").ToArray();
            run.Cells = new ItemGroupCell?[rowCount, headers.Length];

            // G3: every expected name must occur exactly once, and nothing else may be in the run, otherwise the
            // grid would hide an item or bind a cell to the wrong one.
            var seen = new HashSet<(int, int)>();
            foreach (var item in run.Items)
            {
                var position = parse(item);
                if (position == null || position.Value.Row < 0 || position.Value.Row >= rowCount)
                {
                    Reject(run, $"item {item.Number} \"{item.Name}\" has a name the grid does not use");
                    return;
                }
                if (!seen.Add(position.Value))
                {
                    Reject(run, $"the name \"{item.Name}\" occurs more than once");
                    return;
                }
                run.SetCell(new ItemGroupCell(item, position.Value.Row, position.Value.Column, kindOf(position.Value.Column)));
            }

            if (seen.Count != rowCount * headers.Length)
            {
                Reject(run, $"{rowCount * headers.Length - seen.Count} of {rowCount * headers.Length} cells have no item");
                return;
            }

            run.Recognized = true;
        }

        private static void LayoutSlab(ItemGroupRun run, string title, string[] headers, string rowWord)
        {
            run.Title = title;
            run.ColumnHeaders = headers;
            run.RowLabels = Enumerable.Range(1, 4).Select(n => $"{rowWord} {n}").ToArray();
            run.Cells = new ItemGroupCell?[4, headers.Length];
            if (run.Items.Count != SlabCellCount)
            {
                Reject(run, $"the run has {run.Items.Count} items, not {SlabCellCount}");
                return;
            }
            for (int index = 0; index < run.Items.Count; index++)
                run.SetCell(new ItemGroupCell(run.Items[index], index / 3, index % 3, ItemGroupCellKind.Number));
            run.Recognized = true;
        }

        private static void Reject(ItemGroupRun run, string reason)
        {
            run.Recognized = false;
            run.Problem = reason;
            run.ClearCells(); // CellFor finds nothing in a rejected run
        }

        // "Truck {n} Arrival|Gallons|Slump|Temp" exactly (SetModelValue builds "Truck {0} {1}" and matches the Name).
        public static (int Row, int Column)? ParseTruckName(string? name)
        {
            if (name == null)
                return null;
            var parts = name.Split(' ');
            if (parts.Length != 3 || parts[0] != "Truck")
                return null;
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int truck) ||
                truck.ToString(CultureInfo.InvariantCulture) != parts[1])
                return null;
            int column = Array.IndexOf(TruckTypes, parts[2]);
            if (column < 0)
                return null;
            return (truck - 1, column);
        }

        // "{Calc Point Id|Expected Elevation|Field Point Id|Shot Elevation|Result} {n}" exactly.
        public static (int Row, int Column)? ParseDrainageName(string? name)
        {
            if (name == null)
                return null;
            for (int column = 0; column < DrainageTypes.Length; column++)
            {
                string prefix = DrainageTypes[column] + " ";
                if (!name.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                string number = name.Substring(prefix.Length);
                if (int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int row) &&
                    row.ToString(CultureInfo.InvariantCulture) == number)
                    return (row - 1, column);
                return null;
            }
            return null;
        }

        // Truck arrival time: stored "hh:mm tt" (en-US, leading zero), e.g. "08:25 AM" (RTC B51, B96).
        // Accepts "8:25", "08:25", "20:25", "8:25 pm", "825", "0825". Blank clears the cell to "".
        public static bool TryParseTruckTime(string? text, out string stored)
        {
            stored = "";
            string input = (text ?? "").Trim();
            if (input.Length == 0)
                return true;

            string upper = input.ToUpperInvariant().Replace(".", "");
            string? meridiem = null;
            if (upper.EndsWith("AM") || upper.EndsWith("PM"))
            {
                meridiem = upper.Substring(upper.Length - 2);
                upper = upper.Substring(0, upper.Length - 2).Trim();
            }
            else if (upper.EndsWith("A") || upper.EndsWith("P"))
            {
                meridiem = upper.EndsWith("A") ? "AM" : "PM";
                upper = upper.Substring(0, upper.Length - 1).Trim();
            }

            int hour, minute;
            if (upper.Contains(':'))
            {
                var parts = upper.Split(':');
                if (parts.Length != 2 || parts[0].Length is < 1 or > 2 || parts[1].Length != 2 ||
                    !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hour) ||
                    !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minute))
                    return false;
            }
            else if (upper.Length is 3 or 4 && upper.All(char.IsDigit))
            {
                hour = int.Parse(upper.Substring(0, upper.Length - 2), CultureInfo.InvariantCulture);
                minute = int.Parse(upper.Substring(upper.Length - 2), CultureInfo.InvariantCulture);
            }
            else if (upper.Length is 1 or 2 && upper.All(char.IsDigit))
            {
                hour = int.Parse(upper, CultureInfo.InvariantCulture);
                minute = 0;
            }
            else
            {
                return false;
            }

            if (minute > 59)
                return false;
            if (meridiem != null)
            {
                if (hour < 1 || hour > 12)
                    return false;
                if (meridiem == "AM" && hour == 12) hour = 0;
                else if (meridiem == "PM" && hour != 12) hour += 12;
            }
            else if (hour > 23)
            {
                return false;
            }

            stored = new DateTime(2000, 1, 1, hour, minute, 0).ToString("hh:mm tt", EnUs);
            return true;
        }

        // Cells display "HH:mm" (INSPECT 2026). A stored value RED cannot read is shown as stored.
        public static string FormatTruckTimeDisplay(string? stored)
        {
            string value = stored ?? "";
            if (value.Length == 0)
                return "";
            if (DateTime.TryParseExact(value, new[] { "hh:mm tt", "h:mm tt" }, EnUs, DateTimeStyles.None, out var time))
                return time.ToString("HH:mm", CultureInfo.InvariantCulture);
            return value;
        }

        // Number cells: F-004 N5 and N7 through ItemControls' number rules. Blank clears to "". Stored as the plain
        // decimal string (en-US digits, "." separator, no thousands separator).
        public static bool TryNormalizeNumber(string? text, out string stored, out string? error)
        {
            stored = "";
            error = null;
            string input = (text ?? "").Trim();
            if (input.Length == 0)
                return true;
            if (!ItemControls.TryParseNumber(input, out double value))
            {
                error = "Enter a number.";
                return false;
            }
            if (value > ItemControls.NumberPadMaximum)
            {
                error = ValueTooLargeText;
                return false;
            }
            stored = ItemControls.FormatNumber(value);
            return true;
        }

        // DrainageVerification Result (INSPECT's only calculation): Pass when |E - S| <= 0.05, else Fail;
        // "" when E or S is blank or not a decimal (GetRowResult).
        public static string DrainageResult(string? expected, string? shot)
        {
            if (!decimal.TryParse(expected, NumberStyles.Number, EnUs, out decimal e) ||
                !decimal.TryParse(shot, NumberStyles.Number, EnUs, out decimal s))
                return "";
            return Math.Abs(e - s) <= DrainageTolerance ? "Pass" : "Fail";
        }

        // Recomputes the Result cell of the row that holds `edited`. Returns the Result item when its Value changed.
        public static Item? UpdateDrainageResult(ItemGroupRun run, Item edited)
        {
            if (run.Kind != ItemGroupKind.DrainageVerification || !run.Recognized)
                return null;
            var cell = run.CellFor(edited);
            if (cell == null || (cell.Column != DrainageExpectedColumn && cell.Column != DrainageShotColumn))
                return null;
            var resultCell = run.CellAt(cell.Row, DrainageResultColumn);
            var expectedCell = run.CellAt(cell.Row, DrainageExpectedColumn);
            var shotCell = run.CellAt(cell.Row, DrainageShotColumn);
            if (resultCell == null || expectedCell == null || shotCell == null)
                return null;

            string result = DrainageResult(expectedCell.Item.Value?.ToString(), shotCell.Item.Value?.ToString());
            string current = resultCell.Item.Value?.ToString() ?? "";
            if (string.Equals(current, result, StringComparison.Ordinal))
                return null;
            resultCell.Item.Value = result;
            return resultCell.Item;
        }

        public static string DisplayValue(ItemGroupCell cell)
        {
            string stored = cell.Item.Value?.ToString() ?? "";
            return cell.Kind == ItemGroupCellKind.Time ? FormatTruckTimeDisplay(stored) : stored;
        }

        public static bool IsRequiredMissing(Item item) =>
            item.Required && string.IsNullOrWhiteSpace(item.Value?.ToString());

        private static string? ReadExtensionString(Item item, string key)
        {
            if (item.ExtensionData == null || !item.ExtensionData.TryGetValue(key, out var raw) || raw == null)
                return null;
            if (raw is JValue value)
                return value.Type == JTokenType.Null ? null : Convert.ToString(value.Value, CultureInfo.InvariantCulture);
            if (raw is JToken token)
                return token.Type == JTokenType.Null ? null : token.ToString();
            return Convert.ToString(raw, CultureInfo.InvariantCulture);
        }
    }
}
