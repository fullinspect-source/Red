using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace InspectionEditor.Services
{
    public enum InspectFolderKind { MyList, Outbox, Archive }

    public sealed class ExistingInspectionCopy
    {
        public InspectFolderKind Folder { get; init; }
        public string Path { get; init; } = "";
    }

    // "Add to My List" for ORDERS, matching Inspect's ServiceRequestManager.AddFormToMyList:
    // the new form comes from the Strand website (cookie session), is filled from the service
    // request, and is saved as {InspectFolder}\MyList\{InspectionNumber}.ins (plain JSON).
    public static class StrandMyListService
    {
        private const string DefaultWebsiteUrl = "https://apps.strandsystems.com/";
        private static readonly CookieContainer _cookies = new();
        private static readonly HttpClient _web = new(new HttpClientHandler { CookieContainer = _cookies, UseCookies = true })
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        private static string _webSessionUser = "";
        private static Uri? _webSessionBase;

        public static string MyListFolder(string inspectorFolder) => InspectFolders.PathOf(inspectorFolder, InspectFolder.MyList);

        // Inspect's FormConfig.URL; only Strand-owned HTTPS hosts receive the login.
        private static Uri WebsiteBase()
        {
            string configured = StrandCredentialService.ReadInspectSetting("URL");
            if (Uri.TryCreate(configured, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            {
                string host = uri.Host.ToLowerInvariant();
                if (host == "strandsystems.com" || host.EndsWith(".strandsystems.com") ||
                    host == "strandae.com" || host.EndsWith(".strandae.com"))
                {
                    return new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
                }
            }
            return new Uri(DefaultWebsiteUrl);
        }

        // RestSharp joins the base and the resource; mirror that so a base with a path keeps it.
        private static Uri WebsiteUri(Uri baseUri, string resource) =>
            new(baseUri.AbsoluteUri.TrimEnd('/') + "/" + resource.TrimStart('/'));

        private static async Task EnsureWebsiteSessionAsync(Uri baseUri, bool force, CancellationToken cancellationToken)
        {
            var (username, password) = StrandScheduleService.SessionCredentials;
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                throw new StrandAuthRequiredException("Sign in to Strand again to add orders to My List.");

            bool haveSession = !force &&
                               string.Equals(_webSessionUser, username, StringComparison.OrdinalIgnoreCase) &&
                               _webSessionBase == baseUri &&
                               _cookies.GetCookies(baseUri).Count > 0;
            if (haveSession)
                return;

            // Same call Inspect makes (POST with the login in the query), but URL-encoded.
            string resource = $"Security/LogOn?UserName={Uri.EscapeDataString(username)}&Password={Uri.EscapeDataString(password)}";
            using var request = new HttpRequestMessage(HttpMethod.Post, WebsiteUri(baseUri, resource)) { Content = new StringContent("") };
            using var response = await _web.SendAsync(request, cancellationToken);
            if (_cookies.GetCookies(baseUri).Count == 0)
                throw new InvalidOperationException("Could not sign in to the Strand website.");

            _webSessionUser = username;
            _webSessionBase = baseUri;
        }

        // GET /Inspect/(Re)inspection/Create?jobNumber=&formId= on the website.
        public static async Task<JObject> CreateFormAsync(bool isReinspection, string jobNumber, int formId, CancellationToken cancellationToken = default)
        {
            Uri baseUri = WebsiteBase();
            string resource = $"{(isReinspection ? "/Inspect/Reinspection/Create" : "/Inspect/Inspection/Create")}" +
                              $"?jobNumber={Uri.EscapeDataString(jobNumber ?? "")}&formId={formId}";

            for (int attempt = 0; attempt < 2; attempt++)
            {
                await EnsureWebsiteSessionAsync(baseUri, force: attempt > 0, cancellationToken);
                using var response = await _web.GetAsync(WebsiteUri(baseUri, resource), cancellationToken);
                string content = await response.Content.ReadAsStringAsync(cancellationToken);

                // An expired website session answers with the login page instead of JSON; log in again once.
                if (response.IsSuccessStatusCode && content.TrimStart().StartsWith("{"))
                {
                    using var reader = new JsonTextReader(new StringReader(content)) { DateParseHandling = DateParseHandling.None };
                    return JObject.Load(reader);
                }
            }

            throw new InvalidOperationException("Could not communicate with inspect server.");
        }

        // PUT /Inspect/Jobs/RequestFiles?jobNumber={first 7}: Inspect asks for the job's files for kind Inspector only.
        // Any other kind (Preinspector, NonDropBox, Other, Missing) sends nothing.
        public static async Task RequestJobFilesAsync(InspectUserKind userKind, string jobNumber, CancellationToken cancellationToken = default)
        {
            if (!InspectUserType.RequestsJobFiles(userKind) || string.IsNullOrWhiteSpace(jobNumber) || jobNumber.Length < 7)
                return;

            Uri baseUri = WebsiteBase();
            await EnsureWebsiteSessionAsync(baseUri, force: false, cancellationToken);
            string resource = "/Inspect/Jobs/RequestFiles?jobNumber=" + Uri.EscapeDataString(jobNumber.Substring(0, 7));
            using var request = new HttpRequestMessage(HttpMethod.Put, WebsiteUri(baseUri, resource));
            using var response = await _web.SendAsync(request, cancellationToken);
        }

        // Applies what Inspect's GetInspection and Inspection.Save set on a new server form.
        public static string PrepareForm(JObject form, StrandRequestDetail request, string inspectorInitialsName, InspectUserKind userKind)
        {
            string requests = request.SpecialRequest ?? "";
            if (request.RequestTimeRequired && request.Date is DateTimeOffset date)
                requests = $"(Scheduled for {date.ToLocalTime().DateTime.ToShortTimeString()})  {requests}";

            SetHeader(form, "Contact", request.RequestorName);
            SetHeader(form, "ContactNumber", request.RequestorPhone);
            SetHeader(form, "Requests", requests);
            SetHeader(form, "IsReinspection", request.IsReinspection);
            SetHeader(form, "Attempt", request.Attempt);
            SetHeader(form, "PurchaseOrderRequired", request.PurchaseOrderRequired);
            SetHeader(form, "PurchaseOrderNumber", request.PurchaseOrderNumber);
            SetHeader(form, "RequisitionNumber", request.RequisitionNumber);
            if (request.WorkOrderId is Guid workOrderId)
                SetHeader(form, "WorkOrderId", workOrderId.ToString());
            SetHeader(form, "InspectorName", StrandScheduleService.DisplayName);
            SetHeader(form, "InspectorPhone", StrandScheduleService.Phone);
            SetHeader(form, "InspectorUsername", StrandScheduleService.Username);

            // The first item named "Superintendent" gets the request's superintendent.
            var superintendent = form["Sections"]?.Children()
                .SelectMany(section => section["Items"]?.Children() ?? Enumerable.Empty<JToken>())
                .OfType<JObject>()
                .FirstOrDefault(item => string.Equals(item.Value<string>("Name"), "Superintendent", StringComparison.OrdinalIgnoreCase));
            if (superintendent != null)
                superintendent["Value"] = request.SuperintendentName;
            string superintendentValue = superintendent?.Value<string>("Value") ?? "";
            SetHeader(form, "SuperintendentName", string.IsNullOrWhiteSpace(superintendentValue) ? null : superintendentValue);

            string inspectionNumber = form.Value<string>("InspectionNumber") ?? "";
            if (string.IsNullOrWhiteSpace(inspectionNumber))
            {
                bool wasPreinspection = form.Value<bool?>("IsPreinspection") == true;
                string job = form.Value<string>("JobNumber") ?? request.JobNumber ?? "";
                if (job.Length > 7)
                    job = job.Substring(0, 7);
                string code = form.Value<string>("InspectionCode") ?? "";
                int attempt = form.Value<int?>("Attempt") ?? request.Attempt;
                string initials = InspectorProfileService.Initials(inspectorInitialsName);
                inspectionNumber = wasPreinspection
                    ? $"{job}-{code}-P-{attempt}-{initials}"
                    : $"{job}-{code}-{attempt}-{initials}";
                SetHeader(form, "InspectionNumber", inspectionNumber);
            }

            SetHeader(form, "IsPreinspection", InspectUserType.IsPreinspection(userKind));

            // Inspect writes new forms with zero fees, encrypted with its own scheme.
            string zeroFee = (0m).ToString("F2", System.Globalization.CultureInfo.CurrentCulture);
            SetHeader(form, "InspectionFee_Secure", InspectCipher.Encrypt(zeroFee));
            SetHeader(form, "InspectorOtherFee1_Secure", InspectCipher.Encrypt(zeroFee));
            SetHeader(form, "InspectorOtherFee2_Secure", InspectCipher.Encrypt(zeroFee));

            return inspectionNumber;
        }

        // F-025 A6 (§10.3): only these header fields, in this order: InspectionNumber (the server's; built only when
        // blank), IsPreinspection, SuperintendentName (the first "Superintendent" item's Value, the item unchanged), the
        // three zero-fee _Secure fields. Contact, Requests, IsReinspection, Attempt, WorkOrderId and the inspector fields
        // keep the server's values; no event is added. zeroFee: tests pass a fake encoder.
        public static string PrepareBuyerWalkForm(JObject form, string jobNumber, string inspectorInitialsName, InspectUserKind userKind, Func<string>? zeroFee = null)
        {
            string inspectionNumber = form.Value<string>("InspectionNumber") ?? "";
            if (string.IsNullOrWhiteSpace(inspectionNumber))
            {
                bool wasPreinspection = form.Value<bool?>("IsPreinspection") == true;
                string job = form.Value<string>("JobNumber") ?? jobNumber ?? ""; // OC §10.8: an empty string counts as not null
                if (job.Length > 7)
                    job = job.Substring(0, 7);
                string code = form.Value<string>("InspectionCode") ?? "";
                int attempt = form.Value<int?>("Attempt") ?? 0;
                string initials = InspectorProfileService.Initials(inspectorInitialsName);
                inspectionNumber = wasPreinspection ? $"{job}-{code}-P-{attempt}-{initials}" : $"{job}-{code}-{attempt}-{initials}";
            }
            SetHeader(form, "InspectionNumber", inspectionNumber);
            SetHeader(form, "IsPreinspection", InspectUserType.IsPreinspection(userKind));
            var superintendent = form["Sections"]?.Children()
                .SelectMany(section => section["Items"]?.Children() ?? Enumerable.Empty<JToken>())
                .OfType<JObject>()
                .FirstOrDefault(item => string.Equals(item.Value<string>("Name"), "Superintendent", StringComparison.OrdinalIgnoreCase));
            string superintendentValue = superintendent?["Value"]?.Type == JTokenType.String ? (string?)superintendent["Value"] ?? "" : "";
            SetHeader(form, "SuperintendentName", string.IsNullOrWhiteSpace(superintendentValue) ? null : superintendentValue);
            // One encoder call per field, as ORDERS (OC §10.8 rows 16-18).
            string Fee() => zeroFee != null ? zeroFee() : InspectCipher.Encrypt((0m).ToString("F2", System.Globalization.CultureInfo.CurrentCulture));
            SetHeader(form, "InspectionFee_Secure", Fee());
            SetHeader(form, "InspectorOtherFee1_Secure", Fee());
            SetHeader(form, "InspectorOtherFee2_Secure", Fee());
            return inspectionNumber;
        }

        // iPad fee token (ORDERS decision D-03 = B): one encrypted "0.00", made with the same cipher Add to My List uses
        // for zero fees. The owner pastes it into the iPad once, so Inspect's secret never ships there. Callers must
        // never save or log it.
        public static string CreateFeeToken() => InspectCipher.Encrypt("0.00");

        // Inspect's lists read only the JSON before "Sections", so new header fields go there.
        private static void SetHeader(JObject form, string name, object? value)
        {
            JToken token = value == null ? JValue.CreateNull() : JToken.FromObject(value);
            if (form.Property(name, StringComparison.Ordinal) is JProperty existing)
            {
                existing.Value = token;
                return;
            }

            var property = new JProperty(name, token);
            if (form.Property("Sections", StringComparison.Ordinal) is JProperty sections)
                sections.AddBeforeSelf(property);
            else
                form.Add(property);
        }

        // Inspect's duplicate check: My List, Outbox and Archive, matched by InspectionNumber.
        public static List<ExistingInspectionCopy> FindExistingCopies(string inspectorFolder, string inspectionNumber)
        {
            var copies = new List<ExistingInspectionCopy>();
            foreach (var (kind, name) in new[] { (InspectFolderKind.MyList, "MyList"), (InspectFolderKind.Outbox, "Outbox"), (InspectFolderKind.Archive, "Archive") })
            {
                string folder = Path.Combine(inspectorFolder, name);
                if (!Directory.Exists(folder))
                    continue;

                foreach (string path in Directory.EnumerateFiles(folder, "*.ins", SearchOption.TopDirectoryOnly))
                {
                    string? number = ReadInspectionNumber(path);
                    if (string.Equals(number, inspectionNumber, StringComparison.Ordinal))
                        copies.Add(new ExistingInspectionCopy { Folder = kind, Path = path });
                }
            }
            return copies;
        }

        private static readonly Regex InspectionNumberPattern = new("\"InspectionNumber\"\\s*:\\s*(\"(?:[^\"\\\\]|\\\\.)*\"|null)", RegexOptions.Compiled);

        // Reads only the file's header (the part before "Sections"), as Inspect's list loader does.
        private static string? ReadInspectionNumber(string path)
        {
            try
            {
                using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var header = new StringBuilder();
                char[] buffer = new char[8192];
                while (header.Length < 512 * 1024)
                {
                    int read = reader.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        break;
                    header.Append(buffer, 0, read);
                    if (header.ToString().Contains("\"Sections\"", StringComparison.Ordinal))
                        break;
                }

                var match = InspectionNumberPattern.Match(header.ToString());
                if (match.Success && match.Groups[1].Value != "null")
                    return JToken.Parse(match.Groups[1].Value).Value<string>();
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Log($"Could not read the inspection number from {path}", ex);
            }

            return Path.GetFileNameWithoutExtension(path);
        }

        public static string TargetPath(string inspectorFolder, string inspectionNumber)
        {
            if (string.IsNullOrWhiteSpace(inspectionNumber) || inspectionNumber.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException($"The inspection number '{inspectionNumber}' cannot be used as a file name.");
            return Path.Combine(MyListFolder(inspectorFolder), inspectionNumber + ".ins");
        }

        // Writes the new form (compact JSON, UTF-8 without BOM) and never overwrites an existing file.
        // It is created in place, like Inspect's FileWrapper.Create: Inspect's My List watcher only adds rows
        // on Created and has no Renamed handler, so a temp-file-and-rename save would stay invisible to it.
        public static string SaveNewForm(string inspectorFolder, string inspectionNumber, JObject form)
        {
            string target = TargetPath(inspectorFolder, inspectionNumber);
            InspectFolders.EnsureForWrite(inspectorFolder, InspectFolder.MyList); // F-003 O8
            byte[] bytes = new UTF8Encoding(false).GetBytes(form.ToString(Formatting.None));

            // CreateNew throws if the file exists. FileShare.Read (as Inspect's File.CreateText) lets Inspect's
            // watcher read the file as soon as it appears.
            var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            try
            {
                using (stream)
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }
                if (!File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
                    throw new IOException("The saved form could not be verified.");
            }
            catch
            {
                try { File.Delete(target); } catch { } // only reached after this call created the file
                throw;
            }
            return target;
        }

        // Inspect's MoveToMyList, made safe: a rename into MyList that refuses to replace an existing file.
        public static string MoveToMyList(string inspectorFolder, string inspectionNumber, string sourcePath)
        {
            string target = TargetPath(inspectorFolder, inspectionNumber);
            InspectFolders.EnsureForWrite(inspectorFolder, InspectFolder.MyList); // F-003 O8
            if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                File.Move(sourcePath, target); // throws instead of overwriting MyList\{N}.ins
            return target;
        }
    }
}
