using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InspectionEditor.Services
{
    // One row of page 2: a form with its history (local files of the same job and ServiceId, else the server's values).
    public sealed class AddFormRow
    {
        public int ServiceId { get; init; }
        public int FormId { get; init; }
        public string Name { get; init; } = "";
        public int Attempt { get; set; } = 1;
        public string? LastWorkOrderId { get; set; }
        public string LastReport { get; set; } = "";
        public string LastDate { get; set; } = "";
        public string LastStatus { get; set; } = "";
        public string NextAction { get; set; } = "";
        public string? LastLocalPath { get; set; }
    }

    // What pages 1-3 collected (MADD §2.3-2.5).
    public sealed class AddRequest
    {
        public JObject? Job { get; init; }                 // null after Skip
        public bool Skip => Job == null;
        public required AddFormRow Form { get; init; }
        public string Contact { get; init; } = "";
        public string ContactPhone { get; init; } = "";
        public DateTime? Date { get; init; }               // D6
        public bool Reinspection { get; init; }
        public string AttemptText { get; init; } = "";
        public bool PurchaseOrderRequired { get; init; }
        public string PurchaseOrderNumber { get; init; } = "";
        public string Requests { get; init; } = "";        // D5 text
    }

    public sealed record AddDuplicate(string Path, string Number, string FolderCaption);

    /// <summary>
    /// F-021 Add Service (behavior authority Inspect2022 2022.7.27.1, with the SPEC's RED deviations): job search, forms
    /// with on-disk history, page 3 rules, the template and reinspection builds, the duplicate check and the save as
    /// INSPECT-local bytes. Job, buyer, agent and superintendent data are never logged (S5).
    /// </summary>
    public static class AddServiceService
    {
        public const string ReceivingText = "Receiving offline work requirements from Strand, please wait...";
        public const string ReceivingCaption = "Stay Connected To WiFi";
        public const string MissingText = "Please connect to WiFi and open your Settings Window, then click the \"Download Offline Data\" button and try again.";
        public const string MissingCaption = "Missing Requirements For Offline Work";
        public const string OfflineSearchText = "Device is not online and cannot contact Strand for most recent jobs.";
        public const string OfflineSearchText2 = "Searching offline jobs now...";
        public const string OfflineSearchCaption = "No Internet Connection";
        public const string NotFoundCaption = "Job Not Found";
        public const string SkipWarning = "Job files and prior inspections will NOT be downloaded.";
        public const string SelectAddress = "Please select an address.";
        public const string SelectInspection = "Please select an inspection.";
        public const string DuplicateRefused = "Cannot create inspection as it already exists on device. Remove the duplicate inspections or change the Attempt number.";
        public const int ManualJobNumberLength = 7;
        public const string ManualJobNumber = "0000000";
        private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");
        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(100);

        public static string NotFoundText(string query) =>
            $"No jobs for \"{query}\" were found, we are sorry for the inconvenience." + Environment.NewLine + Environment.NewLine +
            "Please connect to the internet, close Inspect, and then try again. Contact IT Support if issue persists.";

        public static string DuplicateQuestion(string number, string folderCaption) =>
            $"{number} inspection already exists in your {folderCaption}. Would you like to remove it?";

        public static string AddFailed(string formName, string? jobNumber) => $"Could not add {formName} inspection for {jobNumber}.";

        public static string FutureWarning(int days) => $"You have scheduled this inspection {days} day(s) in the future.";
        public static string PastWarning(int days) => $"You have scheduled this inspection {days} day(s) in the past.";

        // E2: all three caches present and written today.
        public static bool CachesReady(DateTime now, string? root = null) =>
            Enum.GetValues<OfflineCache>().All(cache => !OfflineCacheService.IsStale(cache, now, root));

        // ── Page 1 ──────────────────────────────────────────────────────────────────────────────────────────────────

        public static string TitleCase(string? text) =>
            string.IsNullOrWhiteSpace(text) ? "" : EnUs.TextInfo.ToTitleCase(text.Trim().ToLower(EnUs));

        public static string BlockLot(JObject job)
        {
            string block = S(job["Block"]), lot = S(job["Lot"]);
            if (block.Length > 0 && lot.Length > 0) return $"{block}/{lot}";
            if (lot.Length > 0) return $"Lot {lot}";
            if (block.Length > 0) return $"Blk {block}";
            return " ";
        }

        public static string Job7(JObject? job) => job == null ? ManualJobNumber : JobFilesService.Job7(S(job["JobNumber"]));

        // Online search (MADD 2.3.16-17): null means "not online" (any status but 200, no network, not JSON twice).
        public static async Task<List<JObject>?> SearchOnlineAsync(StrandWebsiteSession session, string query, CancellationToken token = default)
        {
            var body = await GetJsonAsync(session, "Inspect/Jobs/Read?searchTerms=" + Uri.EscapeDataString(query), token);
            if (body is not JArray array) return null;
            var jobs = array.OfType<JObject>().ToList();
            foreach (var job in jobs)
                foreach (string key in new[] { "Address", "City", "Subdivision" })
                    job[key] = TitleCase(S(job[key]));
            return jobs;
        }

        // Offline search (MADD 2.3.18): Address or JobNumber non-empty AND (lower-cased Address contains the query as
        // typed, OR JobNumber contains it), ordinal. A null Address counts as "".
        public static List<JObject> SearchOffline(JArray? cachedJobs, string query) =>
            (cachedJobs ?? new JArray()).OfType<JObject>().Where(job =>
            {
                string address = S(job["Address"]), number = S(job["JobNumber"]);
                return (address.Length > 0 || number.Length > 0) &&
                       (address.ToLower(EnUs).Contains(query, StringComparison.Ordinal) || number.Contains(query, StringComparison.Ordinal));
            }).ToList();

        // ── Page 2 ──────────────────────────────────────────────────────────────────────────────────────────────────

        public static List<AddFormRow> FormsFromJson(JArray? forms) =>
            (forms ?? new JArray()).OfType<JObject>().Select(f => new AddFormRow
            {
                ServiceId = I(f["ServiceId"]),
                FormId = I(f["FormId"]),
                Name = S(f["Name"]),
                Attempt = Math.Max(1, I(f["Attempt"])),
                LastWorkOrderId = NullIfBlank(S(f["LastWorkOrderId"])),
                LastReport = S(f["LastReportNumber"]),
                LastDate = S(f["LastDate"]),
                LastStatus = S(f["LastStatus"]),
                NextAction = S(f["NextAction"])
            }).ToList();

        // MADD 2.4.24-25: Skip → every cached form; else Forms/Read for job7, falling back to the cache when it yields no data.
        public static async Task<List<AddFormRow>> FormsAsync(StrandWebsiteSession? session, string? job7, JArray? cachedForms, CancellationToken token = default)
        {
            if (session != null && !string.IsNullOrEmpty(job7))
            {
                var body = await GetJsonAsync(session, "Inspect/Forms/Read?jobNumber=" + Uri.EscapeDataString(job7), token);
                if (body is JArray { Count: > 0 } online)
                    return FormsFromJson(online);
            }
            return FormsFromJson(cachedForms);
        }

        // MADD 2.4.26-28 with the RED deviation: files whose JobNumber's first 7 characters equal job7, from the four
        // folders on disk (header-only reads; an unreadable folder or file is skipped and logged).
        public static void ApplyHistory(List<AddFormRow> forms, string inspectionsFolder, string? job7, bool includeReview, Action<string>? redLog = null)
        {
            if (string.IsNullOrEmpty(job7)) return;
            redLog ??= DiagnosticLogService.Info;
            var folders = new List<InspectFolder> { InspectFolder.MyList, InspectFolder.Archive, InspectFolder.Outbox };
            if (includeReview) folders.Add(InspectFolder.Review);
            var files = new List<(string Path, InspectionHeader Header)>();
            foreach (var folder in folders)
            {
                string path = InspectFolders.PathOf(inspectionsFolder, folder);
                string[] names;
                try { names = Directory.Exists(path) ? Directory.GetFiles(path, "*.ins", SearchOption.TopDirectoryOnly) : Array.Empty<string>(); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { redLog($"Add: the {folder} folder could not be read ({ex.GetType().Name})"); continue; }
                foreach (string file in names)
                {
                    try
                    {
                        var header = InspectionHeaderReader.Read(file);
                        if (header != null && JobFilesService.Job7(header["JobNumber"]) == job7) files.Add((file, header));
                    }
                    catch (Exception ex) { redLog($"Add: a listed inspection could not be read ({ex.GetType().Name})"); }
                }
            }
            foreach (var form in forms)
            {
                var latest = files.Where(f => int.TryParse(f.Header["ServiceId"], out int id) && id == form.ServiceId)
                    .OrderByDescending(f => int.TryParse(f.Header["Attempt"], out int a) ? a : 0).FirstOrDefault();
                if (latest.Header == null) continue;
                int attempt = int.TryParse(latest.Header["Attempt"], out int at) ? at : 0;
                form.LastReport = latest.Header["InspectionNumber"] ?? "";
                form.LastDate = latest.Header["DateInspected"] ?? "";
                form.LastStatus = latest.Header.StatusName;
                form.LastWorkOrderId = NullIfBlank(latest.Header["WorkOrderId"]);
                form.LastLocalPath = latest.Path;
                form.Attempt = attempt + 1;
            }
        }

        // ── Page 3 ──────────────────────────────────────────────────────────────────────────────────────────────────

        // Any run of 10 digits becomes (ddd) ddd-dddd as typed.
        public static string FormatPhone(string text) =>
            Regex.Replace(text ?? "", @"(?<!\d)(\d{3})(\d{3})(\d{4})(?!\d)", "($1) $2-$3");

        // D5.
        public static string RequestsText(bool call, bool callWithResults, bool takePhotos, string? other)
        {
            var parts = new List<string>();
            if (call) parts.Add("call in advance");
            if (callWithResults) parts.Add("call with results");
            if (takePhotos) parts.Add("take photos");
            string text = "";
            if (parts.Count > 0)
            {
                text = string.Join(", ", parts);
                text = char.ToUpperInvariant(text[0]) + text.Substring(1) + ".";
            }
            string extra = (other ?? "").Trim();
            if (extra.Length > 0) text = text.Length > 0 ? text + " " + extra : extra;
            return text;
        }

        // D3: null when no warning applies.
        public static string? DateWarning(DateTime picked, DateTime now)
        {
            int days = (picked.Date - now.Date).Days;
            if (days >= 7) return FutureWarning(days);
            if (days <= -2) return PastWarning(Math.Abs(days));
            return null;
        }

        // D1 for ServiceId 121 with a job: (Contact, Phone, Other).
        public static (string Contact, string Phone, string Other) NhoDefaults(JObject? job, int serviceId)
        {
            if (serviceId != BuyerWalkRules.NewHomeOrientationServiceId || job == null) return ("", "", "");
            string phone = S(job["BuyerPhone1"]);
            if (phone.Length == 0) phone = S(job["BuyerPhone2"]);
            return (S(job["BuyerName"]), FormatPhone(phone), S(job["Requests"]));
        }

        // D1 PO default: Attempt > 1, except ServiceIds 33 and 121.
        public static bool DefaultPoRequired(AddFormRow form) =>
            form.Attempt > 1 && form.ServiceId != 33 && form.ServiceId != BuyerWalkRules.NewHomeOrientationServiceId;

        // ── Build (§4, §5) ──────────────────────────────────────────────────────────────────────────────────────────

        public static int AttemptOf(AddRequest request) =>
            int.TryParse(request.AttemptText?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int attempt) ? attempt : request.Reinspection ? 2 : 1;

        public static string InspectionNumber(string job7, string code, int attempt, string inspectorName, bool preinspector) =>
            preinspector
                ? $"{job7}-{code}-P-{attempt}-{InspectorProfileService.Initials(inspectorName)}"
                : $"{job7}-{code}-{attempt}-{InspectorProfileService.Initials(inspectorName)}";

        /// <summary>
        /// Builds the new inspection (§4) or reinspection (§5) as a document to project. Throws InvalidOperationException
        /// with the user's message ("Cannot find template for form id: {id}.").
        /// </summary>
        public static async Task<JObject> BuildAsync(AddRequest request, CompleteProfile profile, string? rawUserType, JArray? templates,
            StrandWebsiteSession? session, DateTime now, Func<string, string>? encryptFee = null, Action<string>? redLog = null, CancellationToken token = default)
        {
            redLog ??= DiagnosticLogService.Info;
            encryptFee ??= InspectCipher.Encrypt;
            bool preinspector = string.Equals(rawUserType, "Preinspector", StringComparison.Ordinal);
            int attempt = AttemptOf(request);
            string fullJob = request.Skip ? ManualJobNumber : S(request.Job!["JobNumber"]);
            string job7 = Job7(request.Job);

            JObject? doc = null;
            bool reinspection = request.Reinspection && request.Form.ServiceId != 33 && request.Form.LastWorkOrderId != null; // R1
            if (reinspection && request.Form.LastLocalPath != null && File.Exists(request.Form.LastLocalPath))
            {
                redLog("Creating reinspection from local file.");
                doc = InspectionFileReader.ParseDocument(File.ReadAllBytes(request.Form.LastLocalPath), out _);
            }
            else if (reinspection && session != null)
            {
                var fetched = await GetJsonAsync(session, $"Inspect/Reinspection/Fetch2/?jobNumber={Uri.EscapeDataString(fullJob)}&workOrderId={Uri.EscapeDataString(request.Form.LastWorkOrderId!)}", token);
                if (fetched is JObject fromServer) doc = fromServer;
            }

            string zeroFee = encryptFee(0m.ToString("F2", CultureInfo.CurrentCulture));
            var eventJson = new JObject
            {
                ["Date"] = HeaderRules.FormatDate(now), ["Username"] = profile.Username, ["UserDisplayName"] = profile.InspectorName,
                ["EventTypeId"] = 30, ["AdditionalInfo"] = null // owner Q-32: no "Inspect Version" text
            };

            if (doc != null)
            {
                // R2 resets on the copied inspection; R3 keeps everything else.
                doc["JobNumber"] = fullJob;
                doc["WorkOrderId"] = Guid.NewGuid().ToString("D");
                doc["IsPreinspection"] = preinspector;
                doc["InspectorUsername"] = profile.Username;
                doc["InspectorName"] = profile.InspectorName;
                doc["InspectorPhone"] = profile.Phone;
                doc["CreatedDate"] = doc["LastModifiedDate"] = HeaderRules.FormatDate(now);
                doc["LastModifiedUsername"] = profile.Username;
                doc["LastDBDate"] = null;
                doc["IsTransfer"] = false;
                doc["IsApproval"] = false;
                doc["IsReinspection"] = true;
                doc["Attempt"] = attempt;
                doc["NextActionText"] = "";
                doc["NextActionId"] = 0;
                doc["StatusId"] = 0;
                doc["Signature"] = null;
                doc["Events"] = new JArray(eventJson);
                foreach (var attachment in (doc["Attachments"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                    attachment["Id"] = Guid.NewGuid().ToString("D");
            }
            else
            {
                // N2: the first Templates-cache entry with this FormId, deep-copied.
                var template = (templates ?? new JArray()).OfType<JObject>().FirstOrDefault(t => I(t["FormId"]) == request.Form.FormId)
                    ?? throw new InvalidOperationException($"Cannot find template for form id: {request.Form.FormId}.");
                template = (JObject)template.DeepClone();
                doc = new JObject
                {
                    ["InspectVersion"] = null,
                    ["WorkOrderId"] = Guid.NewGuid().ToString("D"),
                    ["ServiceId"] = request.Form.ServiceId,
                    ["FormId"] = template["FormId"]?.DeepClone(),
                    ["IsTransfer"] = false,
                    ["IsPreinspection"] = preinspector,
                    ["IsApproval"] = false,
                    ["NoCharge"] = false,
                    ["NextActionId"] = 0,
                    ["InspectionName"] = request.Form.Name,
                    ["InspectionCode"] = template["InspectionCode"]?.DeepClone(),
                    ["Attempt"] = attempt,
                    ["StatusId"] = 0,
                    ["StatusList"] = template["StatusList"]?.DeepClone(),
                    ["JobNumber"] = fullJob,
                    ["InspectorUsername"] = profile.Username,
                    ["InspectorName"] = profile.InspectorName,
                    ["InspectorPhone"] = profile.Phone,
                    ["CreatedDate"] = HeaderRules.FormatDate(now),
                    ["LastModifiedDate"] = HeaderRules.FormatDate(now),
                    ["LastModifiedUsername"] = profile.Username,
                    ["LastDBDate"] = null,
                    ["Version"] = 0,
                    ["IsReinspection"] = request.Reinspection,
                    ["ManuallyEnteredAddress"] = request.Skip,
                    ["Signature"] = null,
                    ["DocumentTypeId"] = template["DocumentTypeId"]?.DeepClone(),
                    ["Sections"] = template["Sections"]?.DeepClone() ?? new JArray(),
                    ["Events"] = new JArray(eventJson),
                    ["Attachments"] = new JArray()
                };
                if (request.Skip)
                {
                    doc["State"] = profile.DefaultState; // N3 after Skip
                }
                else
                {
                    var job = request.Job!;
                    doc["BuilderName"] = job["BuilderName"]?.DeepClone();
                    doc["BuilderAccountId"] = job["BuilderAccountId"]?.DeepClone();
                    doc["PlanNumber"] = job["Plan"]?.DeepClone();
                    doc["Project"] = job["Subdivision"]?.DeepClone();
                    doc["Lot"] = job["Lot"]?.DeepClone();
                    doc["Block"] = job["Block"]?.DeepClone();
                    doc["Address"] = job["Address"]?.DeepClone();
                    doc["City"] = job["City"]?.DeepClone();
                    doc["State"] = job["State"]?.DeepClone();
                }
                // N4: superintendent choices for a template with a SUPERINTENDENT item.
                var superItem = SuperintendentItem(doc);
                if (superItem != null && session != null)
                {
                    try
                    {
                        if (await GetJsonAsync(session, "Inspect/Jobs/GetSupers?jobNumber=" + Uri.EscapeDataString(fullJob), token) is JArray supers)
                        {
                            var list = superItem["ValueList"] as JArray ?? new JArray();
                            foreach (string name in supers.Select(s => s.Type == JTokenType.String ? (string)s! : null).Where(s => !string.IsNullOrEmpty(s))
                                         .Select(s => s!).OrderBy(s => s, StringComparer.CurrentCulture))
                                list.Add(name);
                            superItem["ValueList"] = list;
                        }
                    }
                    catch (Exception ex) { redLog($"Add: superintendent list not received ({ex.GetType().Name})"); }
                }
            }

            // Both paths: the inspection number, zero fees, page 3.
            string code = S(doc["InspectionCode"]);
            doc["InspectionNumber"] = InspectionNumber(job7, code, attempt, profile.InspectorName, preinspector);
            doc["InspectionFee_Secure"] = zeroFee;
            doc["InspectorOtherFee1_Secure"] = zeroFee;
            doc["InspectorOtherFee2_Secure"] = zeroFee;
            doc["Contact"] = request.Contact.Trim();
            doc["ContactNumber"] = request.ContactPhone.Trim();
            doc["DateInspected"] = request.Date.HasValue ? HeaderRules.FormatDate(request.Date.Value) : null;
            doc["PurchaseOrderRequired"] = request.PurchaseOrderRequired;
            doc["PurchaseOrderNumber"] = request.PurchaseOrderNumber.Trim();
            doc["Requests"] = request.Requests;
            if (doc is { } && reinspection) doc["IsReinspection"] = true;

            // N5: New Home Orientation extras (ServiceId 121 with a job selected in this window).
            if (!request.Skip && I(doc["ServiceId"]) == BuyerWalkRules.NewHomeOrientationServiceId)
                BuyerWalkRules.Prefill(doc, BuyerWalkJobOf(request.Job!));

            // SuperintendentName = the SUPERINTENDENT item's value at save, else null (MADD §4).
            var superValue = SuperintendentItem(doc)?["Value"];
            doc["SuperintendentName"] = superValue == null || superValue.Type == JTokenType.Null || S(superValue).Length == 0 ? null : superValue.DeepClone();
            return doc;
        }

        public static BuyerWalkJob BuyerWalkJobOf(JObject job) => new(
            NullIfBlank(S(job["BuyerName"])), NullIfBlank(S(job["BuyerPhone1"])), NullIfBlank(S(job["BuyerPhone2"])), NullIfBlank(S(job["BuyerEmail"])),
            NullIfBlank(S(job["AgentName"])), NullIfBlank(S(job["AgentPhone"])), NullIfBlank(S(job["AgentEmail"])), Date(job["ClosingDate"]),
            NullIfBlank(S(job["Superintendent"])), NullIfBlank(S(job["SuperintendentPhone"])),
            Date(job["Schedule1Date"])); // INSPECT reads Schedule1Date only (N5; the cache has ScheduleDate)

        private static JObject? SuperintendentItem(JObject doc) =>
            (doc["Sections"] as JArray)?.OfType<JObject>().SelectMany(s => (s["Items"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                .FirstOrDefault(i => string.Equals(S(i["Name"]).ToUpperInvariant(), "SUPERINTENDENT", StringComparison.Ordinal));

        // ── Duplicates and save (§6) ────────────────────────────────────────────────────────────────────────────────

        // S1: {num}.ins by file name in MyList, Archive, Outbox and Review on disk, for every path including Skip.
        public static List<AddDuplicate> FindDuplicates(string inspectionsFolder, string number)
        {
            var found = new List<AddDuplicate>();
            foreach (var (folder, caption) in new[] { (InspectFolder.MyList, "My List"), (InspectFolder.Archive, "Archive"), (InspectFolder.Review, "Review list"), (InspectFolder.Outbox, "Outbox") })
            {
                string path = Path.Combine(InspectFolders.PathOf(inspectionsFolder, folder), number + ".ins");
                if (File.Exists(path)) found.Add(new AddDuplicate(path, number, caption));
            }
            return found;
        }

        // S2: compact INSPECT-local bytes (F-002 Mode B), CreateNew in My List, verified.
        public static string Save(string inspectionsFolder, JObject doc)
        {
            string number = S(doc["InspectionNumber"]);
            if (!SubmitService.IsSafeReportName(number))
                throw new IOException("The inspection number cannot be used as a file name.");
            byte[] bytes = InspectProjectionWriter.Project(doc).ToUtf8Bytes();
            string folder = InspectFolders.EnsureForWrite(inspectionsFolder, InspectFolder.MyList);
            string target = Path.Combine(folder, number + ".ins");
            using (var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            if (!File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
                throw new IOException("The new inspection could not be verified.");
            return target;
        }

        // S3: once per created inspection, only for a selected job and UserType exactly "Inspector".
        public static bool ShouldRequestFiles(AddRequest request, string? rawUserType) =>
            !request.Skip && string.Equals(rawUserType, "Inspector", StringComparison.Ordinal);

        // ── Website ─────────────────────────────────────────────────────────────────────────────────────────────────

        // A GET on the website session; a 200 that is a login page logs in again once (RED deviation). Null for any
        // failure, any other status or a body that is not JSON.
        public static async Task<JToken?> GetJsonAsync(StrandWebsiteSession session, string resource, CancellationToken token)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await session.EnsureAsync(force: attempt > 0, token);
                    using var request = new HttpRequestMessage(HttpMethod.Get, StrandWebsiteSession.Resource(session.Base, resource));
                    using var response = await session.SendAsync(request, RequestTimeout, token);
                    if ((int)response.StatusCode != 200) return null;
                    string text = (await response.Content.ReadAsStringAsync(token)).TrimStart('﻿', ' ', '\r', '\n', '\t');
                    if (text.StartsWith("<", StringComparison.Ordinal)) { session.Discard(); continue; }
                    if (text.Length == 0) return null;
                    using var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None };
                    return JToken.Load(reader);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    return null;
                }
            }
            return null;
        }

        private static string S(JToken? token) => token == null || token.Type == JTokenType.Null ? "" : token.ToString();
        private static int I(JToken? token) => token != null && int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
        private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
        private static DateTime? Date(JToken? token) =>
            DateTimeOffset.TryParse(S(token), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.LocalDateTime : null;
    }
}
