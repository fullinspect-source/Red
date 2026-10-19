using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InspectionEditor.Services
{
    // F-025 §2.2: one row of the Buyer Walk list (B1). Names match case-insensitively; required Int/Bool/UUID values
    // that are null or unconvertible fail the whole decode (Newtonsoft defaults, §2.4).
    public sealed class StrandBuyerWalk
    {
        public Guid JobId { get; set; }
        public string? JobNumber { get; set; }
        public string? BuilderName { get; set; }
        public string? Subdivision { get; set; }
        public string? Lot { get; set; }
        public string? Block { get; set; }
        public string? Address { get; set; }
        public string? City { get; set; }
        public string? PlanNumberAndElevation { get; set; }
        public string? BuyerName { get; set; }
        public DateTime? OrderDate { get; set; }
        public DateTime? ReadyDate { get; set; }
        public DateTime? ClosingDate { get; set; }
        public DateTime? ScheduleDate { get; set; }
        public DateTime? Schedule2Date { get; set; }
        public byte ScheduleStatusId { get; set; }
        public string? ScheduleStatusName { get; set; }
        public string? History { get; set; }
        public string? Note { get; set; }
        public bool Flagged { get; set; }
        public string? GroupingField { get; set; }
        public string? InspectorName { get; set; }
        public string? Report1Url { get; set; }
        public string? Report2Url { get; set; }
        public bool IsReinspection { get; set; }

        [JsonIgnore] public int ServerIndex { get; set; }
        [JsonIgnore] public string Group => GroupingField ?? "";
        [JsonIgnore] public string ReadyText => StrandBuyerWalkService.DayText(ReadyDate);
        [JsonIgnore] public string ClosingText => StrandBuyerWalkService.DayText(ClosingDate);
        [JsonIgnore] public string FirstText => StrandBuyerWalkService.DayTimeText(ScheduleDate);
        [JsonIgnore] public string SecondText => StrandBuyerWalkService.DayTimeText(Schedule2Date);
        // §10.1: the date Add to My List checks.
        [JsonIgnore] public DateTime? AddDate => IsReinspection ? Schedule2Date : ScheduleDate;
    }

    public sealed class StrandBuyerWalkInspector
    {
        public string? Username { get; set; }
        public string? DisplayName { get; set; }
    }

    public sealed class StrandScheduleStatus
    {
        public byte Id { get; set; }
        public string? Name { get; set; }
    }

    // §2.3: the Edit record (B2), every field kept because Update re-sends them.
    public sealed class StrandBuyerWalkEdit
    {
        public string? JobNumber { get; set; }
        public DateTime? ReadyDate { get; set; }
        public DateTime? ClosingDate { get; set; }
        public string? BuyerName { get; set; }
        public string? BuyerPhone1 { get; set; }
        public string? BuyerPhone2 { get; set; }
        public string? BuyerEmail { get; set; }
        public string? Address { get; set; }
        public string? City { get; set; }
        public string? InspectorUsername { get; set; }
        public DateTime? ScheduleDate { get; set; }
        public string? Requests { get; set; }
        public string? OrderText { get; set; }
        public byte ScheduleStatusId { get; set; }
        public string? BuilderName { get; set; }
        public string? SubdivisionName { get; set; }
        public string? History { get; set; }
        public bool Flagged { get; set; }
        public string? Note { get; set; }
        public string? Superintendent { get; set; }
        public string? SuperintendentEmail { get; set; }
        public string? SuperintendentAsstEmail { get; set; }
        public string? SuperintendentPhone { get; set; }
        public string? SalesEmail { get; set; }
        public List<StrandBuyerWalkInspector> InspectorList { get; set; } = new();
        public List<StrandScheduleStatus>? ScheduleStatusList { get; set; }
        public string? BuyerAgentName { get; set; }
        public string? BuyerAgentPhone { get; set; }
        public string? BuyerAgentEmail { get; set; }
        public string? ElectricProvider { get; set; }
        public string? GasProvider { get; set; }
        public string? WaterProvider { get; set; }
        public string? ElectricProviderPhone { get; set; }
        public string? GasProviderPhone { get; set; }
        public string? WaterProviderPhone { get; set; }
    }

    public sealed class BuyerWalkReadException : Exception
    {
        public BuyerWalkReadException(string message) : base(message) { }
    }

    /// <summary>
    /// F-025 backend (website only, §3): B1 list, B2 read, B3 update and W2 create on the Strand website session, with
    /// the response acceptance of §3.3. Never logs bodies, URLs with queries, cookies or credentials (§3.6).
    /// </summary>
    public sealed class StrandBuyerWalkService
    {
        public const string CreateFailedMessage = "Could not communicate with server.";
        public const string SaveFailedMessage = "Could not save schedule. Please try again.";
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
        private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

        private readonly StrandWebsiteSession _session;

        public StrandBuyerWalkService(StrandWebsiteSession? session = null) => _session = session ?? StrandWebsiteSession.Default;

        public static StrandBuyerWalkService Default { get; } = new();

        private static readonly JsonSerializerSettings Decode = new()
        {
            DateParseHandling = DateParseHandling.DateTime,
            DateTimeZoneHandling = DateTimeZoneHandling.RoundtripKind,
            MissingMemberHandling = MissingMemberHandling.Ignore
        };

        // ── Formats (§2.5) ──────────────────────────────────────────────────────────────────────────────────────────

        public static string DayText(DateTime? value) => value?.ToString("MMM d", EnUs) ?? "";
        public static string DayTimeText(DateTime? value) => value?.ToString("MMM d h:mm tt", EnUs) ?? "";
        public static string SheetDayText(DateTime? value) => value?.ToString("M/d/yyyy", EnUs) ?? "";
        public static string SheetScheduledText(DateTime? value) => value?.ToString("M/d/yyyy hh:mm tt", EnUs) ?? "";

        // AsOrdinal (BW §9.5).
        public static string Ordinal(int day)
        {
            int hundred = day % 100;
            if (hundred is >= 11 and <= 13) return day.ToString(CultureInfo.InvariantCulture) + "th";
            return day.ToString(CultureInfo.InvariantCulture) + (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        }

        public static string ScheduleNote(DateTime value) =>
            $"Scheduled for {value.ToString("MMM", EnUs)} {Ordinal(value.Day)} at {value.ToString("h:mm tt", EnUs)}.";

        // Update ReadyDate/ClosingDate: UTC ISO with a trimmed fraction and Z. A UTC value is sent as it is; any other
        // is device-local wall time (RestSharp SimpleJson ToUniversalTime). Tests pass their own zone.
        public static string? UtcIso(DateTime? value, TimeZoneInfo? zone = null)
        {
            if (value is not DateTime date) return null;
            DateTime utc = date.Kind == DateTimeKind.Utc ? date
                : TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), zone ?? TimeZoneInfo.Local);
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture) + "Z";
        }

        public static string? ScheduleDateWire(DateTime? value) => value?.ToString("M/d/yyyy h:mm tt", EnUs);

        // ── Calls ─────────────────────────────────────────────────────────────────────────────────────────────────

        private async Task<(int Status, string Body)> GetAsync(string resource, bool force, CancellationToken token)
        {
            await _session.EnsureAsync(force, token);
            using var request = new HttpRequestMessage(HttpMethod.Get, StrandWebsiteSession.Resource(_session.Base, resource));
            using var response = await _session.SendAsync(request, Timeout, token);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(token));
        }

        private static bool IsHtml(string body) => body.TrimStart().StartsWith("<", StringComparison.Ordinal);
        private static bool Ok(int status) => status is >= 200 and < 300;

        // B1 (§3.3): 2xx with a JSON array (empty or null → no rows); an HTML page → one forced re-login and retry.
        public async Task<List<StrandBuyerWalk>> GetBuyerWalksAsync(CancellationToken token = default)
        {
            for (int attempt = 0; ; attempt++)
            {
                var (status, body) = await GetAsync("Inspect/BuyerWalk/GetBuyerWalks", force: attempt > 0, token);
                string trimmed = body.TrimStart();
                if (Ok(status) && (trimmed.Length == 0 || trimmed == "null"))
                    return new List<StrandBuyerWalk>();
                if (Ok(status) && trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    var rows = JsonConvert.DeserializeObject<List<StrandBuyerWalk>>(trimmed, Decode) ?? new List<StrandBuyerWalk>();
                    rows.RemoveAll(r => r == null);
                    for (int i = 0; i < rows.Count; i++) rows[i].ServerIndex = i;
                    return rows;
                }
                if (attempt == 0 && Ok(status) && IsHtml(body))
                    continue;
                throw new InvalidOperationException(Ok(status) ? "The server's answer could not be read." : $"The server answered {status}.");
            }
        }

        // B2 (§3.3, E1): 2xx, a JSON object with Success true and a decodable Data (object or a JSON string holding one).
        public async Task<StrandBuyerWalkEdit> ReadAsync(string jobNumber, CancellationToken token = default)
        {
            string resource = "Inspect/BuyerWalk/Read?jobNumber=" + Uri.EscapeDataString(jobNumber ?? "");
            for (int attempt = 0; ; attempt++)
            {
                var (status, body) = await GetAsync(resource, force: attempt > 0, token);
                if (attempt == 0 && Ok(status) && IsHtml(body))
                    continue;
                if (!Ok(status))
                    throw new BuyerWalkReadException($"The server answered {status}.");
                string trimmed = body.TrimStart();
                if (!trimmed.StartsWith("{", StringComparison.Ordinal))
                    throw new BuyerWalkReadException("");
                JObject reply;
                try
                {
                    using var reader = new JsonTextReader(new StringReader(trimmed)) { DateParseHandling = DateParseHandling.None };
                    reply = JObject.Load(reader);
                }
                catch (JsonException ex) { throw new BuyerWalkReadException(ex.Message); }
                var success = reply.GetValue("Success", StringComparison.OrdinalIgnoreCase);
                if (success?.Type != JTokenType.Boolean || !(bool)success)
                {
                    var message = reply.GetValue("Message", StringComparison.OrdinalIgnoreCase);
                    throw new BuyerWalkReadException(message?.Type == JTokenType.String ? ((string?)message ?? "").Trim() : "");
                }
                var data = reply.GetValue("Data", StringComparison.OrdinalIgnoreCase);
                string? json = data?.Type switch
                {
                    JTokenType.Object => data.ToString(Formatting.None),
                    JTokenType.String => (string?)data,
                    _ => null
                };
                if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
                    throw new BuyerWalkReadException("");
                try
                {
                    var edit = JsonConvert.DeserializeObject<StrandBuyerWalkEdit>(json, Decode) ?? throw new BuyerWalkReadException("");
                    edit.InspectorList ??= new List<StrandBuyerWalkInspector>();
                    return edit;
                }
                catch (JsonException ex) { throw new BuyerWalkReadException(ex.Message); }
            }
        }

        // B3 (§3.3, E4): accepted when 2xx and not an HTML page; an HTML page → one forced re-login and retry.
        // Returns null when saved, else the alert text (auth errors propagate).
        public async Task<string?> UpdateAsync(string body, CancellationToken token = default)
        {
            for (int attempt = 0; ; attempt++)
            {
                int status;
                string reply;
                try
                {
                    await _session.EnsureAsync(force: attempt > 0, token);
                    using var request = new HttpRequestMessage(HttpMethod.Post, StrandWebsiteSession.Resource(_session.Base, "Inspect/BuyerWalk/Update"))
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json")
                    };
                    using var response = await _session.SendAsync(request, Timeout, token);
                    status = (int)response.StatusCode;
                    reply = await response.Content.ReadAsStringAsync(token);
                }
                catch (StrandAuthRequiredException) { throw; }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is IOException || ex is InvalidOperationException)
                {
                    return SaveFailedMessage;
                }
                if (Ok(status) && !IsHtml(reply))
                    return null;
                if (attempt == 0 && Ok(status) && IsHtml(reply))
                    continue;
                return SaveFailedMessage;
            }
        }

        // W2 (§3.2, OC §10.7): GET Inspect/(Re)inspection/Create?jobNumber=&formId=67; 2xx and a JSON object, the second
        // attempt forcing the website login; after two failures INSPECT's text.
        public async Task<JObject> CreateFormAsync(string jobNumber, bool isReinspection, CancellationToken token = default)
        {
            string resource = (isReinspection ? "Inspect/Reinspection/Create" : "Inspect/Inspection/Create") +
                              "?jobNumber=" + Uri.EscapeDataString(jobNumber ?? "") + "&formId=67";
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var (status, body) = await GetAsync(resource, force: attempt > 0, token);
                if (Ok(status) && body.TrimStart().StartsWith("{", StringComparison.Ordinal))
                {
                    using var reader = new JsonTextReader(new StringReader(body)) { DateParseHandling = DateParseHandling.None };
                    return JObject.Load(reader);
                }
            }
            throw new InvalidOperationException(CreateFailedMessage);
        }

        // ── §12 texts ──────────────────────────────────────────────────────────────────────────────────────────────

        public static string LoadedText(int count, DateTime when) => $"{count.ToString(CultureInfo.InvariantCulture)} buyer walks. Updated {when.ToString("h:mm tt", EnUs)}.";
        public static string LoadFailedText(string message) => $"Could not load buyer walks from Strand: {message}";

        // E1 failure (BW-D03): the server's Message, or the transport / decode message; nothing when there is none.
        public static string ReadFailureText(string jobNumber, Exception error) =>
            error is BuyerWalkReadException && string.IsNullOrWhiteSpace(error.Message)
                ? $"Could not load the buyer walk for {jobNumber}."
                : $"Could not load the buyer walk for {jobNumber}: {error.Message}";

        // ── Add to My List rules (§10.2) ───────────────────────────────────────────────────────────────────────────

        // A1 then A1b; null when the row may be added.
        public static string? Refusal(string jobNumber, int statusId, DateTime? date) => statusId switch
        {
            0 => $"Cannot add {jobNumber} to My List. The buyer walk for has not been scheduled.",
            4 or 5 => $"Cannot add {jobNumber} to My List. The final buyer walk for already occured.",
            6 => $"Cannot add {jobNumber} to My List. The buyer walk request for has been cancelled.",
            _ => date == null ? $"Cannot add {jobNumber} to My List. The schedule date is missing for the buyer walk." : null
        };
    }

    /// <summary>
    /// F-025 §9 Edit Schedule sheet without UI: the fields, E5-E8 behaviours (none while loading), E3 validation and the
    /// §3.4 Update body. The window binds to it.
    /// </summary>
    public sealed class BuyerWalkSheet
    {
        public StrandBuyerWalkEdit Loaded { get; }
        public bool IsReinspection { get; }
        public bool Loading { get; private set; } = true;

        public string BuyerName = "", BuyerPhone1 = "", BuyerPhone2 = "", BuyerEmail = "", Superintendent = "", SuperintendentPhone = "",
            SuperintendentEmail = "", SalesEmail = "", Requests = "", Note = "";
        public DateTime? Ready, Closing, Scheduled;
        public int? StatusId;
        public string? InspectorUsername;
        public bool Flagged, NoAnswerChecked, LeftMessageChecked;
        public int NoAnswerCount = 1, LeftMessageCount = 1;
        private readonly DateTime? _loadedReady, _loadedClosing;

        public IReadOnlyList<StrandBuyerWalkInspector> Inspectors { get; }
        public IReadOnlyList<StrandScheduleStatus> Statuses { get; }

        public BuyerWalkSheet(StrandBuyerWalkEdit edit, bool isReinspection)
        {
            Loaded = edit;
            IsReinspection = isReinspection;
            var compare = StringComparer.Create(CultureInfo.GetCultureInfo("en-US"), CompareOptions.None);
            Inspectors = edit.InspectorList.Where(i => i != null)
                .OrderBy(i => i.DisplayName ?? "", compare).ThenBy(i => i.Username ?? "", compare).ToList();
            Statuses = edit.ScheduleStatusList?.Where(s => s != null).ToList() ?? new List<StrandScheduleStatus>(); // [RED-SAFETY]
            BuyerName = edit.BuyerName ?? "";
            BuyerPhone1 = edit.BuyerPhone1 ?? "";
            BuyerPhone2 = edit.BuyerPhone2 ?? "";
            BuyerEmail = edit.BuyerEmail ?? "";
            Superintendent = edit.Superintendent ?? "";
            SuperintendentPhone = edit.SuperintendentPhone ?? "";
            SuperintendentEmail = edit.SuperintendentEmail ?? "";
            SalesEmail = edit.SalesEmail ?? "";
            Requests = edit.Requests ?? "";
            Note = edit.Note ?? "";
            Ready = _loadedReady = edit.ReadyDate;
            Closing = _loadedClosing = edit.ClosingDate;
            Scheduled = edit.ScheduleDate;
            StatusId = edit.ScheduleStatusId;
            InspectorUsername = edit.InspectorUsername;
            Flagged = edit.Flagged; // BW-D15
            (NoAnswerCount, LeftMessageCount) = CountersFromHistory(edit.History);
            Loading = false; // E1 step 5: no behaviour ran while filling
        }

        public string StatusName(int? id) => Statuses.FirstOrDefault(s => s.Id == id)?.Name ?? "";

        // E7 at load: the last occurrence of each text (case-insensitive search), a capital first letter, one digit.
        public static (int NoAnswer, int LeftMessage) CountersFromHistory(string? history)
        {
            int Counter(string prefix)
            {
                if (string.IsNullOrEmpty(history)) return 1;
                int at = history.LastIndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (at < 0 || history[at] != prefix[0]) return 1;
                int digit = at + prefix.Length;
                if (digit >= history.Length || !char.IsAsciiDigit(history[digit])) return 1;
                return Math.Clamp(history[digit] - '0' + 1, 1, 4);
            }
            int left = Counter("Left message (");
            int noAnswer = Counter("No answer (");
            return (noAnswer, left);
        }

        // E5: a Scheduled value set by the user. Returns the warnings to show (no title, warning style).
        public List<string> SetScheduled(DateTime? value)
        {
            var warnings = new List<string>();
            Scheduled = value;
            if (Loading || value is not DateTime when) return warnings;
            StatusId = 2; // without running E6
            if (Ready is DateTime ready && when.Ticks < ready.Ticks) warnings.Add("Your schedule date is before the Ready date.");
            if (Closing is DateTime closing && when.Ticks > closing.Ticks) warnings.Add("Your schedule date is after the Closing date.");
            Note = StrandBuyerWalkService.ScheduleNote(when);
            Flagged = false;
            return warnings;
        }

        // E6: a status picked by the user.
        public void SetStatus(int id)
        {
            StatusId = id;
            if (Loading) return;
            if (id is 0 or 6)
            {
                Scheduled = null;
                Note = $"Changing status to {StatusName(id)}.";
            }
            else if (id == 2)
            {
                Note = $"Changing status to {StatusName(id)}.";
            }
        }

        private void UpdateMessage()
        {
            if (NoAnswerChecked) Note = $"No answer ({NoAnswerCount.ToString(CultureInfo.InvariantCulture)})";
            else if (LeftMessageChecked) Note = $"Left message ({LeftMessageCount.ToString(CultureInfo.InvariantCulture)})";
        }

        // E7 on use.
        public void SetNoAnswerChecked(bool value)
        {
            NoAnswerChecked = value;
            if (Loading) return;
            UpdateMessage();
            Flagged = NoAnswerCount > 2 && NoAnswerChecked;
        }

        public void SetLeftMessageChecked(bool value)
        {
            LeftMessageChecked = value;
            if (Loading) return;
            UpdateMessage();
            Flagged = LeftMessageCount > 2 && LeftMessageChecked;
        }

        public void SetNoAnswerCount(int value)
        {
            NoAnswerCount = Math.Clamp(value, 1, 4);
            if (Loading) return;
            NoAnswerChecked = true;
            UpdateMessage();
            Flagged = NoAnswerCount > 2;
        }

        public void SetLeftMessageCount(int value)
        {
            LeftMessageCount = Math.Clamp(value, 1, 4);
            if (Loading) return;
            LeftMessageChecked = true;
            UpdateMessage();
            Flagged = LeftMessageCount > 2;
        }

        public void ToggleFlag() => Flagged = !Flagged; // E8

        // E3, in INSPECT's order.
        public List<string> Validate()
        {
            var errors = new List<string>();
            void Max(string? text, int limit, string message) { if ((text ?? "").Length > limit) errors.Add(message); }
            Max(Requests, 1000, "Request field only allows a max of 1000 characters.  Please correct");
            Max(BuyerName, 100, "Buyer name field only allows a max of 100 characters.  Please correct");
            Max(Loaded.BuyerAgentName, 100, "Agent name field only allows a max of 100 characters.  Please correct");
            Max(BuyerPhone1, 15, "Buyer Phone field only allows a max of 15 characters.  Please correct");
            Max(BuyerPhone2, 15, "Alt Phone field only allows a max of 15 characters.  Please correct");
            Max(Loaded.BuyerAgentPhone, 15, "Agent Phone field only allows a max of 15 characters.  Please correct");
            Max(BuyerEmail, 250, "Buyer email field only allows a max of 250 characters.  Please correct");
            Max(Loaded.BuyerAgentEmail, 250, "Agent email field only allows a max of 250 characters.  Please correct");
            Max(SuperintendentPhone, 15, "Superintendent Phone field only allows a max of 15 characters.  Please correct");
            Max(Superintendent, 100, "Superintendent field only allows a max of 100 characters.  Please correct");
            Max(SuperintendentEmail, 250, "Superintendent email field only allows a max of 250 characters.  Please correct");
            Max(SalesEmail, 250, "Sales email field only allows a max of 250 characters.  Please correct");
            if (string.IsNullOrWhiteSpace(BuyerName)) errors.Add("Buyer Name is required");
            if (string.IsNullOrWhiteSpace(BuyerPhone1)) errors.Add("Buyer Phone is required");
            if (string.IsNullOrWhiteSpace(Superintendent)) errors.Add("Superintendent name is required");
            if (string.IsNullOrWhiteSpace(SuperintendentEmail)) errors.Add("Superintendent email is required");
            if (StatusId == 2 && Scheduled == null) errors.Add("Please set the scheduled date for the buyer walk.");
            if (string.IsNullOrEmpty(InspectorUsername)) errors.Add("Inspector is required"); // [RED-NEW] BW-D05
            return errors;
        }

        // The Error(s) alert text: each message preceded by a line break, as INSPECT.
        public static string ValidationText(IEnumerable<string> errors) => string.Concat(errors.Select(e => "\n" + e));

        // §3.4: exactly 27 properties in this order, nulls emitted, no "\/" escaping.
        public string UpdateBody(TimeZoneInfo? zone = null)
        {
            var text = new StringWriter(CultureInfo.InvariantCulture);
            using (var json = new JsonTextWriter(text) { Formatting = Formatting.None, StringEscapeHandling = StringEscapeHandling.Default })
            {
                void S(string name, string? value) { json.WritePropertyName(name); if (value == null) json.WriteNull(); else json.WriteValue(value); }
                json.WriteStartObject();
                S("JobNumber", Loaded.JobNumber);
                S("ReadyDate", Ready == _loadedReady && _loadedReady?.Kind == DateTimeKind.Utc ? StrandBuyerWalkService.UtcIso(_loadedReady) : StrandBuyerWalkService.UtcIso(Ready, zone));
                S("ClosingDate", Closing == _loadedClosing && _loadedClosing?.Kind == DateTimeKind.Utc ? StrandBuyerWalkService.UtcIso(_loadedClosing) : StrandBuyerWalkService.UtcIso(Closing, zone));
                S("BuyerName", BuyerName);
                S("BuyerEmail", BuyerEmail);
                S("BuyerPhone1", BuyerPhone1);
                S("BuyerPhone2", BuyerPhone2);
                S("InspectorUsername", InspectorUsername);
                S("ScheduleDate", StrandBuyerWalkService.ScheduleDateWire(Scheduled));
                S("Requests", Requests);
                json.WritePropertyName("ScheduleStatusId"); json.WriteValue(StatusId ?? Loaded.ScheduleStatusId);
                json.WritePropertyName("Flagged"); json.WriteValue(Flagged);
                S("Note", Note);
                S("Superintendent", Superintendent);
                S("SuperintendentPhone", SuperintendentPhone);
                S("SuperintendentEmail", SuperintendentEmail);
                S("SuperintendentAsstEmail", Loaded.SuperintendentAsstEmail);
                S("SalesEmail", SalesEmail);
                S("BuyerAgentName", Loaded.BuyerAgentName);
                S("BuyerAgentEmail", Loaded.BuyerAgentEmail);
                S("BuyerAgentPhone", Loaded.BuyerAgentPhone);
                S("ElectricProvider", Loaded.ElectricProvider);
                S("GasProvider", Loaded.GasProvider);
                S("WaterProvider", Loaded.WaterProvider);
                S("ElectricProviderPhone", Loaded.ElectricProviderPhone);
                S("GasProviderPhone", Loaded.GasProviderPhone);
                S("WaterProviderPhone", Loaded.WaterProviderPhone);
                json.WriteEndObject();
            }
            return text.ToString();
        }
    }

    public sealed class BuyerWalkAddRow
    {
        public required string JobNumber { get; init; }
        public bool IsReinspection { get; init; }
        public int StatusId { get; init; }
        public DateTime? Date { get; init; }
    }

    /// <summary>
    /// F-025 §10 Add to My List per row: A1 → A1b → W2 → A6 → A3 duplicates → act. Alerts are shown at once through the
    /// callbacks; an auth error stops the batch (it propagates).
    /// </summary>
    public sealed class BuyerWalkAdder
    {
        public const string Caption = "Cannot Add Buyer Walk";

        public required StrandBuyerWalkService Service { get; init; }
        public required string InspectorFolder { get; init; }
        public required InspectUserKind UserKind { get; init; }
        public required string InitialsName { get; init; }
        public required Func<string, bool> AskYesNo { get; init; }     // a duplicate question, title Caption; true = Yes
        public required Action<string> Error { get; init; }            // no title, error style
        public required Func<string, bool> IsOpenInRed { get; init; }
        public Func<string>? ZeroFee { get; init; }                    // tests: a fake fee encoder

        public static string Summary(int copied) => copied switch
        {
            0 => "",
            1 => "Copied buyer walk to My List.",
            _ => $"Copied {copied.ToString(CultureInfo.InvariantCulture)} buyer walks to My List."
        };

        public async Task<int> AddAsync(IEnumerable<BuyerWalkAddRow> rows, CancellationToken token = default)
        {
            int copied = 0;
            foreach (var row in rows)
            {
                try
                {
                    if (await AddOneAsync(row, token)) copied++;
                }
                catch (StrandAuthRequiredException) { throw; }
                catch (Exception ex) { Error(ex.Message); }
            }
            return copied;
        }

        public async Task<bool> AddOneAsync(BuyerWalkAddRow row, CancellationToken token = default)
        {
            if (StrandBuyerWalkService.Refusal(row.JobNumber, row.StatusId, row.Date) is string refusal)
            {
                Error(refusal);
                return false;
            }
            var form = await Service.CreateFormAsync(row.JobNumber, row.IsReinspection, token);
            string inspectionNumber = StrandMyListService.PrepareBuyerWalkForm(form, row.JobNumber, InitialsName, UserKind, ZeroFee);

            var existing = StrandMyListService.FindExistingCopies(InspectorFolder, inspectionNumber);
            if (existing.Any(c => IsOpenInRed(c.Path)))
            {
                Error($"Cannot add to My List. '{inspectionNumber}' is open in RED. Close it first, then try again.");
                return false;
            }
            var myListCopies = existing.Where(c => c.Folder == InspectFolderKind.MyList).ToList();
            InspectFolderKind? otherKind = existing.Any(c => c.Folder == InspectFolderKind.Outbox) ? InspectFolderKind.Outbox
                : existing.Any(c => c.Folder == InspectFolderKind.Archive) ? InspectFolderKind.Archive : null;
            var otherCopies = otherKind is InspectFolderKind kind ? existing.Where(c => c.Folder == kind).ToList() : new List<ExistingInspectionCopy>();
            string otherName = otherKind == InspectFolderKind.Outbox ? "Outbox" : "Archive";
            if (otherCopies.Count > 1)
            {
                var names = otherCopies.Select(c => Path.GetFileName(c.Path)).OrderBy(n => n.ToUpperInvariant(), StringComparer.Ordinal);
                Error($"Cannot add to My List. {otherName} has {otherCopies.Count} files for {inspectionNumber} ({string.Join(", ", names)}). Move the one you want to My List by hand.");
                return false;
            }
            // Every question before any file change; No skips the row silently.
            if (myListCopies.Count > 0 && !AskYesNo($"My List already contains '{inspectionNumber}'. Do you want to replace it?"))
                return false;
            bool move = false;
            if (myListCopies.Count == 0 && otherCopies.Count == 1)
            {
                if (!AskYesNo($"{otherName} already contains '{inspectionNumber}'. Move this copy to My List instead?"))
                    return false;
                move = true;
            }
            foreach (var copy in myListCopies)
                File.Delete(copy.Path);
            if (move)
            {
                StrandMyListService.MoveToMyList(InspectorFolder, inspectionNumber, otherCopies[0].Path);
                return true;
            }
            StrandMyListService.SaveNewForm(InspectorFolder, inspectionNumber, form);
            return true;
        }
    }
}
