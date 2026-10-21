using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace InspectionEditor.Services
{
    // Where an order's date falls, for the Add to My List date check (owner 2026-10-20).
    public enum OrderDateWindow { Soon, Later, Earlier, NoDate }

    // One order as the date question and the same-address check see it. The sheet passes its edited date.
    public sealed record OrderAddItem(Guid Id, string ServiceName, string Address, string City, DateTimeOffset? Date, bool TimeRequired, int ServiceTypeId);

    // Which selected orders Unassign sends, and a line for each one it skips.
    public sealed record UnassignPlan(List<StrandScheduleRequest> Rows, List<string> Skipped);

    // ORDERS rules from the owner's request of 2026-10-20 (F-001): the 30-hour date check and the same-address
    // checklist for Add to My List, and who may Unassign. Pure functions; OrdersRulesHarness tests them.
    public static class OrdersAddRules
    {
        public const double WindowHours = 30;

        public static OrderAddItem ItemFor(StrandScheduleRequest row) =>
            new(row.Id, row.ServiceName, row.Address, row.City, row.Date, row.RequestTimeRequired, row.ServiceTypeId);

        // The orders being added; the one the order dialog saved replaces its grid row (the dialog's date is newer).
        public static List<OrderAddItem> ItemsFor(IEnumerable<StrandScheduleRequest> rows, OrderAddItem? saved) =>
            rows.Select(r => saved != null && saved.Id == r.Id ? saved : ItemFor(r)).ToList();

        // An order is added without a question when it is for today or later and starts at most 30 hours from now.
        // A date-only order (no required time) starts at local midnight of its day, so today's and tomorrow's orders
        // never ask; a timed order starts at its time. An order for an earlier day, more than 30 hours ahead, or
        // with no date asks first.
        public static OrderDateWindow DateWindow(DateTimeOffset? date, bool timeRequired, DateTimeOffset now)
        {
            if (date is not DateTimeOffset value)
                return OrderDateWindow.NoDate;
            DateTime localDay = value.ToLocalTime().Date;
            if (localDay < now.ToLocalTime().Date)
                return OrderDateWindow.Earlier;
            DateTimeOffset start = timeRequired ? value : new DateTimeOffset(DateTime.SpecifyKind(localDay, DateTimeKind.Local));
            return start - now > TimeSpan.FromHours(WindowHours) ? OrderDateWindow.Later : OrderDateWindow.Soon;
        }

        public static bool NeedsDateQuestion(OrderAddItem item, DateTimeOffset now) =>
            DateWindow(item.Date, item.TimeRequired, now) != OrderDateWindow.Soon;

        // The grid's Schedule Date text (StrandScheduleRequest.ScheduleDateText).
        public static string DateText(DateTimeOffset? date, bool timeRequired) =>
            date is not DateTimeOffset value
                ? ""
                : value.ToLocalTime().DateTime.ToString(timeRequired ? "M/d/yyyy hh:mm tt" : "M/d/yyyy", CultureInfo.CurrentCulture);

        // The owner's wording for one order; several orders are listed with their dates.
        public static string DateQuestion(IReadOnlyList<OrderAddItem> asked, int batchCount)
        {
            if (asked.Count == 1 && batchCount == 1)
            {
                var only = asked[0];
                return only.Date == null
                    ? "That service has no scheduled date. Are you sure you want to add it to My List?"
                    : $"That service is for {DateText(only.Date, only.TimeRequired)}. Are you sure you want to add it to My List?";
            }

            string lines = string.Join(Environment.NewLine, asked.Select(i =>
                $"{i.ServiceName} for {i.Address}: {(i.Date == null ? "no scheduled date" : DateText(i.Date, i.TimeRequired))}"));
            return asked.Count == 1
                ? $"This service is not within the next {WindowHours:0} hours:{Environment.NewLine}{Environment.NewLine}{lines}{Environment.NewLine}{Environment.NewLine}Are you sure you want to add it to My List?"
                : $"These services are not within the next {WindowHours:0} hours:{Environment.NewLine}{Environment.NewLine}{lines}{Environment.NewLine}{Environment.NewLine}Are you sure you want to add them to My List?";
        }

        // Same address: trimmed, inner spaces collapsed, case-insensitive, and the same city. A blank address
        // never matches.
        public static string AddressKey(string? address, string? city)
        {
            string street = Normalize(address);
            return street.Length == 0 ? "" : street + "|" + Normalize(city);

            static string Normalize(string? text) =>
                string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        }

        // Other orders in the loaded list for the same address on the same local calendar day as an order being added,
        // and of its service type (an inspection never offers a survey): open (Requested or Assigned), no pending job,
        // not already in the batch or declined, and not already in My List. Ordered by time, then service.
        public static List<StrandScheduleRequest> SameAddressCandidates(IEnumerable<OrderAddItem> adding,
                                                                         IEnumerable<StrandScheduleRequest> loaded,
                                                                         ISet<Guid> exclude,
                                                                         IReadOnlyCollection<string> myListFileNames)
        {
            var keys = new HashSet<(string Key, DateTime Day, int Type)>();
            foreach (var item in adding)
            {
                string key = AddressKey(item.Address, item.City);
                if (key.Length > 0 && item.Date is DateTimeOffset date)
                    keys.Add((key, date.ToLocalTime().Date, item.ServiceTypeId));
            }
            if (keys.Count == 0)
                return new List<StrandScheduleRequest>();

            return loaded
                .Where(r => !exclude.Contains(r.Id)
                            && r.StatusId is StrandRequestStatus.Requested or StrandRequestStatus.Assigned
                            && !r.IsPendingJob
                            && r.LocalDate is DateTime day
                            && keys.Contains((AddressKey(r.Address, r.City), day.Date, r.ServiceTypeId))
                            && !IsInMyList(r, myListFileNames))
                .GroupBy(r => r.Id)
                .Select(g => g.First())
                .OrderBy(r => r.LocalDate)
                .ThenBy(r => r.ServiceName ?? "", StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        // The checklist row's second line: job, the grid's Schedule Date text and the address, plus who has it when it
        // is someone else's (adding it to My List assigns it to the signed-in user).
        public static string CandidateDetail(StrandScheduleRequest order, string username)
        {
            var parts = new List<string> { $"Job {order.JobNumber}", order.ScheduleDateText, order.Address };
            if (IsAssigned(order) && !string.Equals(order.AssignedUsername?.Trim(), username.Trim(), StringComparison.OrdinalIgnoreCase))
                parts.Add($"Assigned to {AssigneeName(order)}");
            return string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        // My List files are named by InspectionNumber, "{first 7 of the job}-{inspection code}-...". An order is taken
        // to be in My List when a file there has its job and the code its service name starts with ("HER - HERS Energy
        // Rough" is code HER). When either is unknown it is not.
        public static bool IsInMyList(StrandScheduleRequest order, IReadOnlyCollection<string> myListFileNames)
        {
            string job = (order.JobNumber ?? "").Trim();
            string name = (order.ServiceName ?? "").Trim();
            int dash = name.IndexOf(" - ", StringComparison.Ordinal);
            if (job.Length < 7 || dash <= 0)
                return false;
            string job7 = job[..7];
            string code = name[..dash].Trim();
            return myListFileNames.Any(file =>
            {
                string[] parts = file.Split('-');
                return parts.Length >= 2
                       && string.Equals(parts[0].Trim(), job7, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(parts[1].Trim(), code, StringComparison.OrdinalIgnoreCase);
            });
        }

        // Unassign (owner 2026-10-20): anyone may unassign orders assigned to themselves; only the super user may
        // unassign someone else's. Pending, deleted and completed orders are skipped with INSPECT's Assign messages.
        // current: the server's rows by Id, read just before (the grid can be minutes old); each selected order is
        // judged by its current row, and one the server no longer lists is skipped.
        public static UnassignPlan PlanUnassign(IEnumerable<StrandScheduleRequest> selected, IReadOnlyDictionary<Guid, StrandScheduleRequest> current,
                                                string username, bool isSuperUser)
        {
            var rows = new List<StrandScheduleRequest>();
            var skipped = new List<string>();
            foreach (var shown in selected)
            {
                if (!current.TryGetValue(shown.Id, out var row))
                {
                    skipped.Add($"A selected {shown.ServiceName} for {shown.Address} is no longer on the schedule.");
                    continue;
                }
                string what = $"A selected {row.ServiceName} for {row.Address}";
                if (row.IsPendingJob)
                    skipped.Add($"{what} can not be assigned due to a pending job.");
                else if (row.StatusId == StrandRequestStatus.Deleted)
                    skipped.Add($"{what} has a deleted status and can not be updated.");
                else if (StrandRequestStatus.IsCompleted(row.StatusId))
                    skipped.Add($"{what} has a completed status and can not be updated.");
                else if (!IsAssigned(row))
                    skipped.Add($"{what} is not assigned.");
                else if (!isSuperUser && !string.Equals(row.AssignedUsername?.Trim(), username.Trim(), StringComparison.OrdinalIgnoreCase))
                    skipped.Add($"{what} is assigned to {AssigneeName(row)}. Only the super user can unassign someone else's order.");
                else
                    rows.Add(row);
            }
            return new UnassignPlan(rows, skipped);
        }

        public static bool IsAssigned(StrandScheduleRequest row) =>
            row.StatusId != StrandRequestStatus.Requested
            && !string.IsNullOrWhiteSpace(row.AssignedUsername)
            && !string.Equals(row.AssignedName?.Trim(), StrandUserOption.UnassignedName, StringComparison.OrdinalIgnoreCase);

        public static string AssigneeName(StrandScheduleRequest row) =>
            string.IsNullOrWhiteSpace(row.AssignedName) ? row.AssignedUsername?.Trim() ?? "" : row.AssignedName.Trim();

        public static string UnassignResult(int count) =>
            $"{count} {(count == 1 ? "request was" : "requests were")} unassigned.";
    }
}
