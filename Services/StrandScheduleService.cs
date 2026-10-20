using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace InspectionEditor.Services
{
    // Request status ids used by the Strand API (S2E RequestStatus).
    public static class StrandRequestStatus
    {
        public const int Deleted = 0;
        public const int Requested = 1;
        public const int Assigned = 2;
        public const int FieldComplete = 3;

        // S2E RequestStatus.GetCompletedStatusIds(): PASS 4, COMPLETE 5, CORRECT_AND_PROCEED 6, FAIL 7, CANCELLED 8, NOT_READY 9.
        public static bool IsCompleted(int statusId) => statusId is >= 4 and <= 9;
    }

    // One row of the ORDERS grid, shaped like Inspect's service request list item.
    public sealed class StrandScheduleRequest
    {
        public Guid Id { get; set; }
        public Guid? WorkOrderId { get; set; }
        public int ServiceId { get; set; }
        public string ServiceName { get; set; } = "";
        public bool RequestTimeRequired { get; set; }
        public int ServiceTypeId { get; set; }
        public DateTimeOffset? Date { get; set; }
        public int StatusId { get; set; }
        public string StatusName { get; set; } = "";
        public string AssignedUsername { get; set; } = "";
        public string AssignedName { get; set; } = "";
        public string SpecialRequest { get; set; } = "";
        public bool IsPendingJob { get; set; }
        public string JobNumber { get; set; } = "";
        public string Address { get; set; } = "";
        public string City { get; set; } = "";
        public string Lot { get; set; } = "";
        public string Block { get; set; } = "";
        public string SubdivisionName { get; set; } = "";
        public string BuilderName { get; set; } = "";

        [JsonIgnore] public DateTime? LocalDate => Date?.ToLocalTime().DateTime;

        [JsonIgnore]
        public string ScheduleDateText => LocalDate is not DateTime local
            ? ""
            : local.ToString(RequestTimeRequired ? "M/d/yyyy hh:mm tt" : "M/d/yyyy", CultureInfo.CurrentCulture);

        [JsonIgnore]
        public string StatusGroup => StatusId switch
        {
            StrandRequestStatus.Requested => "Unassigned",
            StrandRequestStatus.Assigned => "Assigned",
            StrandRequestStatus.FieldComplete => "Field Complete",
            StrandRequestStatus.Deleted => "Deleted (last 7 days)",
            _ => "Completed (last 7 days)"
        };

        // Group order matches Inspect: Unassigned, Assigned, Field Complete, Completed, Deleted.
        [JsonIgnore]
        public int StatusGroupOrder => StatusId switch
        {
            StrandRequestStatus.Requested => 0,
            StrandRequestStatus.Assigned => 1,
            StrandRequestStatus.FieldComplete => 2,
            StrandRequestStatus.Deleted => 4,
            _ => 3
        };

        [JsonIgnore] public string PendingJobText => IsPendingJob ? "Yes" : "";
        [JsonIgnore] public string TimeRequiredText => RequestTimeRequired ? "Yes" : "";
        [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Address) ? JobNumber : Address;
    }

    // Full request as returned by GET Inspect/Request.
    public sealed class StrandRequestDetail
    {
        public Guid Id { get; set; }
        public int ServiceId { get; set; }
        public string ServiceName { get; set; } = "";
        public bool RequestTimeRequired { get; set; }
        public int ServiceTypeId { get; set; }
        public DateTimeOffset? Date { get; set; }
        public DateTimeOffset? RequestedForDate { get; set; }
        public DateTimeOffset? AssignedDate { get; set; }
        public DateTimeOffset? CompletedDate { get; set; }
        public int StatusId { get; set; }
        public string StatusName { get; set; } = "";
        public Guid? WorkOrderId { get; set; }
        public string RequestorName { get; set; } = "";
        public string RequestorPhone { get; set; } = "";
        public string AssignedUsername { get; set; } = "";
        public string AssignedName { get; set; } = "";
        public string SuperintendentName { get; set; } = "";
        public string SuperintendentPhone { get; set; } = "";
        public string SpecialRequest { get; set; } = "";
        public bool IsPendingJob { get; set; }
        public string JobNumber { get; set; } = "";
        public string Address { get; set; } = "";
        public string City { get; set; } = "";
        public string Lot { get; set; } = "";
        public string Block { get; set; } = "";
        public string SubdivisionName { get; set; } = "";
        public Guid BuilderId { get; set; }
        public string BuilderName { get; set; } = "";
        public bool IsReinspection { get; set; }
        public int Attempt { get; set; }
        public bool PurchaseOrderRequired { get; set; }
        public string PurchaseOrderNumber { get; set; } = "";
        public string RequisitionNumber { get; set; } = "";
        public List<string> History { get; set; } = new();
    }

    public sealed class StrandUserOption
    {
        // INSPECT's S2E.Constants.Placeholder.UNASSIGNED; INSPECT recognises the unassign row by this text.
        public const string UnassignedName = "[Unassigned]";

        public string Username { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public bool IsUnassigned => string.Equals(DisplayName.Trim(), UnassignedName, StringComparison.OrdinalIgnoreCase);
        public override string ToString() => DisplayName;
    }

    public sealed class StrandFormOption
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public override string ToString() => Name;
    }

    public sealed class StrandAuthRequiredException : Exception
    {
        public StrandAuthRequiredException(string message) : base(message) { }
    }

    // Live Strand API client for the ORDERS tab. Endpoints mirror Inspect2026's Schedule tab.
    // Holds only the session token; the saved login lives in StrandCredentialService.
    public static class StrandScheduleService
    {
        private const string DefaultApiBaseUrl = "https://api.strandae.com/";
        private const string ClientId = "InspectClient";
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
        private static Uri _apiBase = new(DefaultApiBaseUrl);
        private static readonly CultureInfo ApiCulture = CultureInfo.GetCultureInfo("en-US");

        private static string? _accessToken;
        private static string _accountId = "";

        public static bool IsSignedIn => !string.IsNullOrEmpty(_accessToken);
        // Changes on every sign-in and sign-out, so each picker window can tell the shared session moved.
        public static int SessionGeneration { get; private set; }
        // Raised on every sign-in and sign-out, so every open window can drop session-bound state (e.g. the fee token).
        public static event Action? SessionChanged;
        public static string Username { get; private set; } = "";
        public static string DisplayName { get; private set; } = "";
        public static string Phone { get; private set; } = "";

        // Assign is reserved for the owner's account (Inspect gates it by Strand permission instead).
        private const string SuperUserUsername = "fullinspect@gmail.com";
        public static bool IsSuperUser => IsSignedIn && string.Equals(Username, SuperUserUsername, StringComparison.OrdinalIgnoreCase);

        // Strand permissions (GET Inspect/User/permissions), as Inspect's Schedule ribbon uses them.
        private static HashSet<string> _permissions = new(StringComparer.Ordinal);
        public static bool CanDeleteRequests =>
            IsSignedIn && (_permissions.Contains("Service Request Edit - All") || _permissions.Contains("Service Request Delete"));

        // Kept in memory for the Strand website session (Add to My List), like Inspect does.
        private static string _sessionPassword = "";
        internal static (string Username, string Password) SessionCredentials => (Username, _sessionPassword);

        // Inspect can point at a different Strand API host; only Strand-owned HTTPS hosts are accepted.
        public static void UseApiUrl(string? apiUrl)
        {
            _apiBase = new Uri(DefaultApiBaseUrl);
            if (!Uri.TryCreate(apiUrl?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return;
            string host = uri.Host.ToLowerInvariant();
            if (host == "strandae.com" || host.EndsWith(".strandae.com") || host == "strandsystems.com" || host.EndsWith(".strandsystems.com"))
                _apiBase = new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
        }

        public static async Task SignInAsync(string username, string password, CancellationToken cancellationToken = default)
        {
            var body = new { Username = username, Password = password, ClientId };
            using var request = CreateJsonRequest(HttpMethod.Post, "api/Auth", body, bearerToken: null);
            using var response = await _http.SendAsync(request, cancellationToken);
            string content = await response.Content.ReadAsStringAsync(cancellationToken);

            JObject? result = null;
            try { result = JObject.Parse(content); } catch (JsonException) { }

            string token = result?.SelectToken("AccessToken")?.Value<string>()
                           ?? result?.SelectToken("accessToken")?.Value<string>() ?? "";
            if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(token))
            {
                string error = result?.SelectToken("ErrorMessage")?.Value<string>()
                               ?? result?.SelectToken("errorMessage")?.Value<string>() ?? "";
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? $"Strand sign-in failed ({(int)response.StatusCode}). Check your username and password."
                    : error);
            }

            var user = result?.SelectToken("User") ?? result?.SelectToken("user");
            _accessToken = token;
            _accountId = FirstString(user, "Account.Id", "account.id", "AccountId");
            Username = FirstString(user, "UserName", "userName", "Username") is { Length: > 0 } name ? name : username;
            DisplayName = FirstString(user, "DisplayName", "displayName") is { Length: > 0 } display ? display : Username;
            Phone = FirstString(user, "Phone", "phone");
            _sessionPassword = password;
            // Raised only once the login is complete: F-020's refresh at the first sign-in reads SessionCredentials here
            // (raised before them, it found no password and never ran; owner tablet test 2026-10-20).
            SessionGeneration++;
            SessionChanged?.Invoke();
            _permissions = await LoadPermissionsAsync(cancellationToken);
        }

        public static void SignOut()
        {
            bool hadSession = _accessToken != null;
            if (hadSession)
                SessionGeneration++;
            _accessToken = null;
            _accountId = "";
            Username = "";
            DisplayName = "";
            Phone = "";
            _sessionPassword = "";
            _permissions = new HashSet<string>(StringComparer.Ordinal);
            if (hadSession)
                SessionChanged?.Invoke();
        }

        // Inspect: GET Inspect/User/permissions?Username= returns [{Id, Name}]. A failure means no extra
        // permissions (Delete stays hidden) rather than a failed sign-in.
        private static async Task<HashSet<string>> LoadPermissionsAsync(CancellationToken cancellationToken)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                string content = await SendAsync(HttpMethod.Get, $"Inspect/User/permissions?Username={Uri.EscapeDataString(Username)}", null, cancellationToken);
                if (JToken.Parse(content) is JArray items)
                {
                    foreach (var item in items)
                    {
                        string name = FirstString(item, "Name", "name");
                        if (name.Length > 0)
                            names.Add(name);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DiagnosticLogService.Log("Could not load Strand permissions", ex);
            }
            return names;
        }

        // Same window as Inspect: one week back through one month ahead.
        public static async Task<List<StrandScheduleRequest>> GetRequestListAsync(CancellationToken cancellationToken = default)
        {
            string start = DateTime.Now.AddDays(-7).Date.ToString(ApiCulture);
            string end = DateTime.Now.AddMonths(1).Date.ToString(ApiCulture);
            string url = $"Inspect/Request/List?StartDate={Uri.EscapeDataString(start)}&EndDate={Uri.EscapeDataString(end)}";
            string content = await SendAsync(HttpMethod.Get, url, null, cancellationToken);
            var rows = JsonConvert.DeserializeObject<List<StrandScheduleRequest>>(content) ?? new();

            foreach (var row in rows.Where(r => r.RequestTimeRequired && r.LocalDate.HasValue))
                row.SpecialRequest = $"(Scheduled for {row.LocalDate!.Value.ToShortTimeString()})  {row.SpecialRequest}";

            return rows;
        }

        public static async Task<StrandRequestDetail> GetRequestAsync(Guid id, CancellationToken cancellationToken = default)
        {
            string content = await SendAsync(HttpMethod.Get, $"Inspect/Request?Id={id}", null, cancellationToken);
            return JsonConvert.DeserializeObject<StrandRequestDetail>(content)
                   ?? throw new InvalidOperationException("Service request not found.");
        }

        // The dialog closes and the grid reloads after a save, so no re-fetch is needed here.
        public static async Task UpdateRequestAsync(StrandRequestDetail request, CancellationToken cancellationToken = default)
        {
            var body = new
            {
                request.Id,
                request.AssignedUsername,
                request.Date,
                request.IsReinspection,
                request.Attempt,
                request.PurchaseOrderRequired,
                request.PurchaseOrderNumber,
                request.RequisitionNumber
            };
            await SendAsync(HttpMethod.Patch, "Inspect/Request", body, cancellationToken);
        }

        public static Task AssignAsync(IEnumerable<Guid> requestIds, string assignedUsername, CancellationToken cancellationToken = default) =>
            SendAsync(HttpMethod.Patch, "Inspect/Request/assign",
                new { RequestIds = requestIds.ToList(), AssignedUsername = assignedUsername }, cancellationToken);

        // Inspect sends work-order status 7 for a completed survey and 3 for a failed one.
        public static Task CompleteSurveyAsync(Guid requestId, bool passed, CancellationToken cancellationToken = default) =>
            SendAsync(HttpMethod.Patch, "Inspect/Request/Complete",
                new { RequestId = requestId, AttachedFileName = (string?)null, AttachedFileData = (byte[]?)null, WorkOrderStatusId = passed ? 7 : 3 },
                cancellationToken);

        public static Task DeleteAsync(Guid requestId, string reason, CancellationToken cancellationToken = default) =>
            SendAsync(HttpMethod.Delete, "Inspect/Request", new { Id = requestId, Reason = reason }, cancellationToken);

        public static Task UpdateJobNumberAsync(Guid requestId, string jobNumber, CancellationToken cancellationToken = default) =>
            SendAsync(HttpMethod.Patch, "Inspect/Request/Job", new { Id = requestId, JobNumber = jobNumber }, cancellationToken);

        // Inspect sends the request's BuilderId under the name AccountId.
        public static async Task<List<StrandFormOption>> GetFormOptionsAsync(int serviceId, Guid builderId, CancellationToken cancellationToken = default)
        {
            string url = $"Inspect/Request/forms?ServiceId={serviceId}&AccountId={builderId}";
            string content = await SendAsync(HttpMethod.Get, url, null, cancellationToken);
            return JsonConvert.DeserializeObject<List<StrandFormOption>>(content) ?? new();
        }

        public static async Task<List<StrandUserOption>> GetInspectorsAsync(CancellationToken cancellationToken = default)
        {
            string url = string.IsNullOrWhiteSpace(_accountId)
                ? "Inspect/User/Inspectors"
                : $"Inspect/User/Inspectors?AccountId={Uri.EscapeDataString(_accountId)}";
            string content = await SendAsync(HttpMethod.Get, url, null, cancellationToken);

            var rows = new List<StrandUserOption>();
            if (JToken.Parse(content) is JArray users)
            {
                rows.AddRange(users.Select(u => new StrandUserOption
                {
                    Username = FirstString(u, "UserName", "Username", "userName", "username"),
                    DisplayName = FirstString(u, "DisplayName", "displayName", "Name", "name")
                }));
            }
            return ArrangeInspectorOptions(rows);
        }

        // Exactly one "[Unassigned]" row, always first, then the inspectors by name. As in INSPECT
        // (ServiceRequestManager.GetInspectorList), the server's own "[Unassigned]" row is used as it comes, since its
        // username is what unassigns a request; RED adds a row with a blank username only when the server sends none.
        internal static List<StrandUserOption> ArrangeInspectorOptions(IEnumerable<StrandUserOption> serverRows)
        {
            var rows = serverRows.ToList();
            var unassigned = rows.FirstOrDefault(r => r.IsUnassigned)
                             ?? new StrandUserOption { DisplayName = StrandUserOption.UnassignedName, Username = "" };
            var inspectors = rows
                .Where(r => !r.IsUnassigned && !string.IsNullOrWhiteSpace(r.Username))
                .Select(r => string.IsNullOrWhiteSpace(r.DisplayName)
                    ? new StrandUserOption { Username = r.Username, DisplayName = r.Username }
                    : r)
                .OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase);
            return new List<StrandUserOption> { unassigned }.Concat(inspectors).ToList();
        }

        private static async Task<string> SendAsync(HttpMethod method, string url, object? body, CancellationToken cancellationToken)
        {
            string? token = _accessToken;
            if (string.IsNullOrEmpty(token))
                throw new StrandAuthRequiredException("Sign in to Strand to load orders.");

            using var request = CreateJsonRequest(method, url, body, token);
            using var response = await _http.SendAsync(request, cancellationToken);
            string content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Only end the session this request was sent with; a newer sign-in stays valid.
                // A null token means a parallel request on the same expired token already signed out.
                string? current = _accessToken;
                if (current != null && !ReferenceEquals(current, token))
                    throw new InvalidOperationException("The Strand account changed while this request was running. Refresh to try again.");
                SignOut();
                throw new StrandAuthRequiredException("Your Strand session expired. Please sign in again.");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new InvalidOperationException("Your Strand account does not have permission to do that.");

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Strand returned {(int)response.StatusCode} {response.ReasonPhrase} for {method} {url.Split('?')[0]}.");

            return content;
        }

        private static HttpRequestMessage CreateJsonRequest(HttpMethod method, string url, object? body, string? bearerToken)
        {
            var request = new HttpRequestMessage(method, new Uri(_apiBase, url));
            request.Headers.Accept.ParseAdd("application/json");
            if (bearerToken != null)
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
            if (body != null)
                request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
            return request;
        }

        private static string FirstString(JToken? root, params string[] paths)
        {
            if (root == null || root.Type != JTokenType.Object)
                return "";

            foreach (string path in paths)
            {
                string value = root.SelectToken(path)?.ToString()?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return "";
        }
    }
}
