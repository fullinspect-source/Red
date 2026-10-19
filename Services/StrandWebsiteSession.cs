using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InspectionEditor.Services
{
    // F-013 §3A/§3B (and later F-020, F-021, F-025): a Strand website cookie session. Production uses only HTTPS Strand
    // hosts (F-001's host rule); a loopback base exists for the V3a mock in tests and practice mode only. Never logs URLs,
    // cookies, credentials or bodies.
    public sealed class StrandWebsiteSession
    {
        private const string DefaultWebsiteUrl = "https://apps.strandsystems.com/";
        private readonly Func<Uri> _base;
        private readonly Func<(string? Username, string? Password)> _credentials;
        private string _sessionUser = "";
        private Uri? _sessionBase;

        public CookieContainer Cookies { get; } = new();
        public HttpClient Client { get; }

        public static StrandWebsiteSession Default { get; } = new(ConfiguredBase, () => StrandScheduleService.SessionCredentials);

        public StrandWebsiteSession(Func<Uri> baseUri, Func<(string? Username, string? Password)> credentials, HttpMessageHandler? handler = null)
        {
            _base = baseUri;
            _credentials = credentials;
            Client = new HttpClient(handler ?? new HttpClientHandler { CookieContainer = Cookies, UseCookies = true, AllowAutoRedirect = true })
            {
                Timeout = Timeout.InfiniteTimeSpan // per-call timeouts below
            };
        }

        // A session against a loopback mock (V3a). Refuses any other host.
        public static StrandWebsiteSession ForLoopbackMock(Uri baseUri, Func<(string? Username, string? Password)> credentials)
        {
            if (!baseUri.IsLoopback)
                throw new InvalidOperationException("Only a loopback address may stand in for the Strand website.");
            return new StrandWebsiteSession(() => baseUri, credentials);
        }

        public Uri Base => _base();

        public bool HasCredentials
        {
            get
            {
                var (user, password) = _credentials();
                return !string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(password);
            }
        }

        public string Username => _credentials().Username ?? "";

        // INSPECT's FormConfig.URL; only Strand-owned HTTPS hosts receive the login (F-001 rule).
        public static Uri ConfiguredBase()
        {
            string configured = StrandCredentialService.ReadInspectSetting("URL");
            if (Uri.TryCreate(configured, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            {
                string host = uri.Host.ToLowerInvariant();
                if (host == "strandsystems.com" || host.EndsWith(".strandsystems.com") ||
                    host == "strandae.com" || host.EndsWith(".strandae.com"))
                    return new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
            }
            return new Uri(DefaultWebsiteUrl);
        }

        // RestSharp joins the base and the resource.
        public static Uri Resource(Uri baseUri, string resource) => new(baseUri.AbsoluteUri.TrimEnd('/') + "/" + resource.TrimStart('/'));

        public void Discard()
        {
            foreach (Cookie cookie in Cookies.GetAllCookies())
                cookie.Expired = true;
            _sessionUser = "";
            _sessionBase = null;
        }

        // A2: log on when there is no session (POST Security/LogOn with URL-encoded UserName and Password).
        public async Task EnsureAsync(bool force, CancellationToken token)
        {
            var (username, password) = _credentials();
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                throw new StrandAuthRequiredException("Sign in to Strand first.");
            Uri baseUri = Base;
            if (!force && string.Equals(_sessionUser, username, StringComparison.OrdinalIgnoreCase) && _sessionBase == baseUri &&
                Cookies.GetCookies(baseUri).Count > 0)
                return;
            string resource = $"Security/LogOn?UserName={Uri.EscapeDataString(username)}&Password={Uri.EscapeDataString(password)}";
            using var request = new HttpRequestMessage(HttpMethod.Post, Resource(baseUri, resource)) { Content = new StringContent("") };
            using var response = await SendAsync(request, TimeSpan.FromSeconds(60), token);
            if (Cookies.GetCookies(baseUri).Count == 0)
                throw new InvalidOperationException("Could not sign in to the Strand website.");
            _sessionUser = username;
            _sessionBase = baseUri;
        }

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken token)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutSource.CancelAfter(timeout);
            return await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutSource.Token);
        }

        // The connection could not be made at all (no network, DNS, refused, TLS): nothing reached Strand. A reset or a
        // broken answer after the request went out is NOT "not sent" (A4, A7, C9): the POST may have arrived.
        public static bool IsNotSent(Exception ex) =>
            ex is HttpRequestException http &&
            (http.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError ||
             http.HttpRequestError == HttpRequestError.Unknown && http.InnerException is AuthenticationException ||
             http.HttpRequestError == HttpRequestError.Unknown && http.InnerException is SocketException
             {
                 SocketErrorCode: SocketError.ConnectionRefused or SocketError.HostNotFound or SocketError.HostUnreachable or
                     SocketError.NetworkUnreachable or SocketError.NetworkDown or SocketError.TryAgain or SocketError.NoData or SocketError.TimedOut
             });
    }

    // §3A A3-A4: POST {base}/Inspect/Inspection/Submit (or /NormalSubmit for §3B) with exactly one multipart part
    // Content-Disposition: form-data; name="inspectionFile"; filename="{name}" and Content-Type application/octet-stream.
    public sealed class StrandWebsiteUploader : IStrandWebsite
    {
        private readonly StrandWebsiteSession _session;
        public static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(100); // RestSharp default (INFERRED)

        public StrandWebsiteUploader(StrandWebsiteSession session) => _session = session;

        public bool HasCredentials => _session.HasCredentials;

        public async Task<WebsiteReply> UploadInspectionAsync(string resource, string fileName, byte[] bytes, CancellationToken token)
        {
            try
            {
                await _session.EnsureAsync(force: false, token);
            }
            catch (Exception ex) when (StrandWebsiteSession.IsNotSent(ex))
            {
                return new WebsiteReply(WebsiteOutcome.NotSent, null);
            }
            catch (StrandAuthRequiredException)
            {
                return new WebsiteReply(WebsiteOutcome.NotSent, null);
            }
            catch (Exception)
            {
                // The LogOn answer is ignored, as INSPECT; a failed logon means the upload was never sent.
                return new WebsiteReply(WebsiteOutcome.NotSent, null);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, StrandWebsiteSession.Resource(_session.Base, resource))
            {
                Content = BuildMultipart(new[] { ("inspectionFile", fileName, bytes) }, Array.Empty<(string, string)>())
            };
            HttpResponseMessage response;
            try
            {
                response = await _session.SendAsync(request, UploadTimeout, token);
            }
            catch (Exception ex) when (StrandWebsiteSession.IsNotSent(ex))
            {
                return new WebsiteReply(WebsiteOutcome.NotSent, null);
            }
            catch (Exception)
            {
                return new WebsiteReply(WebsiteOutcome.NoUsableAnswer, null); // timeout or a broken answer after sending
            }
            using (response)
            {
                string body;
                try { body = await response.Content.ReadAsStringAsync(token); }
                catch (Exception) { return new WebsiteReply(WebsiteOutcome.NoUsableAnswer, null); }
                return Classify(body, _session);
            }
        }

        // A4: a JSON object with Success true = accepted, false = rejected; anything else = no usable answer. After a
        // login page the session is discarded so the next attempt logs on again. Never resent automatically.
        public static WebsiteReply Classify(string body, StrandWebsiteSession? session)
        {
            string trimmed = body.TrimStart();
            if (trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                try
                {
                    using var reader = new JsonTextReader(new StringReader(trimmed)) { DateParseHandling = DateParseHandling.None };
                    var json = JObject.Load(reader);
                    var success = json["Success"];
                    if (success?.Type == JTokenType.Boolean)
                        return (bool)success ? new WebsiteReply(WebsiteOutcome.Accepted, (string?)json["Message"])
                                             : new WebsiteReply(WebsiteOutcome.Rejected, json["Message"]?.Type == JTokenType.Null ? null : (string?)json["Message"]);
                }
                catch (JsonException) { }
            }
            else if (trimmed.StartsWith("<", StringComparison.Ordinal))
            {
                session?.Discard();
            }
            return new WebsiteReply(WebsiteOutcome.NoUsableAnswer, null);
        }

        // Built by hand so the part headers are exactly INSPECT's (MultipartFormDataContent adds filename* and quotes
        // differently by default).
        public static ByteArrayContent BuildMultipart(IReadOnlyList<(string Name, string FileName, byte[] Bytes)> files,
            IReadOnlyList<(string Name, string Value)> fields)
        {
            string boundary = "-----------------------------" + Guid.NewGuid().ToString("N");
            using var body = new MemoryStream();
            void Write(string text)
            {
                byte[] b = Encoding.UTF8.GetBytes(text);
                body.Write(b, 0, b.Length);
            }
            foreach (var (name, value) in fields)
            {
                Write($"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n");
            }
            foreach (var (name, fileName, bytes) in files)
            {
                Write($"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"; filename=\"{fileName}\"\r\nContent-Type: application/octet-stream\r\n\r\n");
                body.Write(bytes, 0, bytes.Length);
                Write("\r\n");
            }
            Write($"--{boundary}--\r\n");
            var content = new ByteArrayContent(body.ToArray());
            content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");
            return content;
        }
    }
}
