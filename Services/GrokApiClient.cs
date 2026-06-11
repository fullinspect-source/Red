using Newtonsoft.Json;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace InspectionEditor.Services
{
    public class GrokApiClient
    {
        private readonly string _apiKey;
        private readonly HttpClient _httpClient;
        private const string API_URL = "https://generativelanguage.googleapis.com/v1beta/models/{0}:generateContent";
        
        // Gemini model choices. Fast favors touch-speed field use; careful favors richer reasoning.
        private const string FAST_MODEL = "gemini-3.1-flash-lite";
        private const string FAST_LEGACY_MODEL = "gemini-2.5-flash-lite";
        private const string CAREFUL_MODEL = "gemini-3.5-flash";
        private const string CAREFUL_LEGACY_MODEL = "gemini-2.5-flash";
        private const string TRANSCRIBE_MODEL = "gemini-2.5-flash";
        private const int PRIMARY_TIMEOUT_SECONDS = 20;
        
        // Default collection (legacy "Red" collection with all docs)
        private const string DEFAULT_COLLECTION_ID = "collection_b7d1f6be-ed1e-4d3a-99ec-cc9d31e364ad";
        
        /// <summary>
        /// Maps inspection type codes to their stage-specific collection IDs.
        /// When a stage-specific collection exists, it will be used instead of the default.
        /// Add new collection IDs here as they are created in the Grok console.
        /// </summary>
        private static readonly Dictionary<string, string> CollectionMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // === GROUP 1: Prepour / Concrete / Slab ===
            { "CPP", "collection_ae09506b-63ec-4024-b07d-0db516a09c3a" },  // Concrete Pre-Pour
            { "CPR", "collection_ae09506b-63ec-4024-b07d-0db516a09c3a" },  // Concrete Pre-Pour Residential
            { "SRP", "collection_ae09506b-63ec-4024-b07d-0db516a09c3a" },  // Slab/Rebar/Post-Tension
            { "STR", "collection_ae09506b-63ec-4024-b07d-0db516a09c3a" },  // Structural
            
            // === GROUP 2: Framing / Structural / Rough MEP ===
            { "FS",  "collection_f6fa7100-2e20-4ebf-b917-7f64272e04f9" },  // Framing Standard
            { "FSF", "collection_f6fa7100-2e20-4ebf-b917-7f64272e04f9" },  // Framing Standard Final
            { "FWI", "collection_f6fa7100-2e20-4ebf-b917-7f64272e04f9" },  // Framing Wall Inspection
            { "ME",  "collection_f6fa7100-2e20-4ebf-b917-7f64272e04f9" },  // Mechanical
            { "MP",  "collection_f6fa7100-2e20-4ebf-b917-7f64272e04f9" },  // Mechanical/Plumbing
            { "TPC", "collection_f6fa7100-2e20-4ebf-b917-7f64272e04f9" },  // Tile/Post-Construction
            { "TFF", "collection_f6fa7100-2e20-4ebf-b917-7f64272e04f9" },  // Tile/Flat/Final
            { "SWI", "collection_f6fa7100-2e20-4ebf-b917-7f64272e04f9" },  // Stucco/Weather Inspection
            
            // === GROUP 3: Energy / Insulation / ACCA Testing ===
            { "HER", "collection_a09a38df-f64b-4941-bf46-280d3a81d3b7" },  // Home Energy Rating
            { "IER", "collection_a09a38df-f64b-4941-bf46-280d3a81d3b7" },  // Interim Energy Rating
            { "IAP", "collection_a09a38df-f64b-4941-bf46-280d3a81d3b7" },  // Indoor Air Plus
            { "PLY", "collection_a09a38df-f64b-4941-bf46-280d3a81d3b7" },  // Plumbing
            { "HEF", "collection_a09a38df-f64b-4941-bf46-280d3a81d3b7" },  // Home Electrical Final
            { "IEF", "collection_a09a38df-f64b-4941-bf46-280d3a81d3b7" },  // Interim Electrical Final
            { "HET", "collection_a09a38df-f64b-4941-bf46-280d3a81d3b7" },  // Home Electrical Test
            { "QIER","collection_a09a38df-f64b-4941-bf46-280d3a81d3b7" },  // Legacy (deprecated)
            { "AFI", "collection_a09a38df-f64b-4941-bf46-280d3a81d3b7" },  // ACCA Test
            
            // === GROUP 4: Roofing / Shingles ===
            { "TRDI","collection_77e5f3a6-434c-4864-be13-1fe7afc550f5" },  // Trade Inspection
            { "TRSI","collection_77e5f3a6-434c-4864-be13-1fe7afc550f5" },  // Trade Structural Inspection
            
            // === GROUP 5: Building / Final ===
            { "BC",  "collection_bde5db0c-4ee9-4bb5-9ca3-da96eaed1cfd" },  // Building Code
            { "BF",  "collection_bde5db0c-4ee9-4bb5-9ca3-da96eaed1cfd" },  // Building Final
            { "COH", "collection_bde5db0c-4ee9-4bb5-9ca3-da96eaed1cfd" },  // Certificate of Habitability
            
            // === Intentionally unused (fall back to default collection) ===
            // BWT, SCI, PPE — no stage-specific collection
        };
        
        /// <summary>
        /// Event fired when the AI model falls back to the faster model due to timeout.
        /// </summary>
        public event Action? OnModelFallback;

        public GrokApiClient(string apiKey)
        {
            _apiKey = apiKey;
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(20); // 20-second timeout — fail fast if no connection
            _httpClient.DefaultRequestHeaders.Add("x-goog-api-key", _apiKey);
        }
        
        /// <summary>
        /// Returns which model is currently being used (for status display)
        /// </summary>
        public string CurrentModel => FAST_MODEL;

        /// <summary>
        /// Quick internet connectivity check. Returns true if reachable, false if offline.
        /// Uses a lightweight HEAD request with a short timeout.
        /// </summary>
        private async Task<bool> CheckConnectivityAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var checkClient = new HttpClient();
                var request = new HttpRequestMessage(HttpMethod.Head, "https://generativelanguage.googleapis.com");
                var response = await checkClient.SendAsync(request, cts.Token);
                return true; // Any response (even 4xx) means we have internet
            }
            catch
            {
                return false;
            }
        }

        public async Task<List<string>> GetInspectionSuggestions(
            byte[] imageData,
            string itemNumber,
            string question,
            string inspectionType = "",
            string sectionName = "",
            List<string>? quickComments = null,
            string previousSuggestions = "",
            string currentComment = "",
            string tone = "Nice")
        {
            try
            {
                // Quick connectivity check before sending large image payload
                if (!await CheckConnectivityAsync())
                {
                    return new List<string>
                    {
                        "⚠️ No internet connection",
                        "AI features need Wi-Fi or cell data to work.",
                        "Check your connection and try again."
                    };
                }
                string base64Image = Convert.ToBase64String(imageData);
                string imageDataUrl = $"data:image/jpeg;base64,{base64Image}";
                var aiRequest = ParseAiRequestStyle(tone);

                // Build rich context
                string inspectionNameForAi = GetInspectionNameForAi(inspectionType);
                string inspectionContext = string.IsNullOrWhiteSpace(inspectionNameForAi)
                    ? ""
                    : $"\nYou are performing a **{inspectionNameForAi}** inspection.";
                
                string sectionContext = string.IsNullOrEmpty(sectionName) 
                    ? "" 
                    : $"\nYou are in the **{sectionName}** section of the checklist.";
                
                string quickCommentContext = "";
                if (quickComments != null && quickComments.Count > 0)
                {
                    var cleanedComments = quickComments.Where(c => !string.IsNullOrWhiteSpace(c)).Take(10).ToList();
                    if (cleanedComments.Count > 0)
                    {
                        quickCommentContext = $@"

Top 10 existing quick comments for this checklist item:
{string.Join("\n", cleanedComments.Select(c => $"- {c}"))}

Use these as context for common wording and common failure patterns. Do not copy any quick comment word-for-word. It is okay to cover the same thought if the photo and checklist context support it, but rewrite it in fresh wording.";
                    }
                }

                string trimmedCurrentComment = (currentComment ?? "").Trim();
                string commentMode = string.IsNullOrWhiteSpace(trimmedCurrentComment)
                    ? "EMPTY COMMENT MODE"
                    : trimmedCurrentComment.Contains("?")
                        ? "QUESTION MARK MODE"
                        : "POLISH MODE";

                string commentModeInstructions = commentMode switch
                {
                    "QUESTION MARK MODE" => @"QUESTION MARK MODE:
This is a fill-in-the-blank request.
- Treat every question mark in the current comment as a blank that needs a short technical fill-in.
- Preserve the inspector's typed comment as the source of truth.
- Preserve all existing wording, trade prefix, suffix text, punctuation, and sentence structure except the question-mark blank itself.
- Replace only the missing question-mark portion with the likely code citation, plan reference, material value, trade phrase, or correction.
- Use the photo only as supporting context for the blank. Do not invent a new unrelated issue from the photo.
- Return 3 complete finished versions of the same comment.
- Do not leave any question marks in the finished suggestions.
- If the blank cannot be known from the context, use conservative wording such as ""per approved plans"" or ""per manufacturer's instructions"" instead of guessing.",
                    "POLISH MODE" => @"POLISH MODE:
If the current comment is not empty and has no question mark, polish it up.
- Keep the same basic meaning and correction.
- Improve grammar, clarity, trade prefix, action wording, and citation if a citation is appropriate.
- Do not invent a different issue unless the photo clearly proves the typed comment is wrong.
- Return 3 polished versions with slightly different wording.",
                    _ => @"EMPTY COMMENT MODE:
If the current comment box is empty, analyze the photo and context deeply.
- Use the inspection type, section, item number, item name, top quick comments, and visible photo details.
- Identify the most likely useful inspection comment for this item.
- Return 3 specific comments ranked by likelihood."
                };
                
                // Search the collection for relevant context about this checklist item
                string collectionContext = "";
                try
                {
                    string searchQuery = $"{itemNumber} {question} {inspectionNameForAi} {sectionName}".Trim();
                    collectionContext = await SearchCollectionAsync(searchQuery, inspectionType);
                }
                catch
                {
                    // Collection search is optional — continue without it
                }
                
                string focusInstruction = commentMode == "QUESTION MARK MODE"
                    ? @"FILL-IN-THE-BLANK FOCUS: The current comment is the source of truth. Find the question-mark blank, fill that missing piece, and preserve the rest of the comment."
                    : @"FOCUS ON THE PHOTO: Analyze what's actually visible in the image. The checklist item name gives context, but your response should describe what you SEE, not just restate the checklist item.";

                string prompt = $@"You are a building inspector at a construction site communicating to a builder about something you noticed during inspection.
{inspectionContext}{sectionContext}

You are looking at checklist item **{itemNumber}: ""{question}""**

{focusInstruction}
{quickCommentContext}
{collectionContext}

Use the knowledge base context above (if present) for proven corrections, engineering details, and code references for item {itemNumber}.

TONE CONTROL: Use the ""{aiRequest.Tone}"" tone.
- Nice: Polite builder-friendly wording, still clear and actionable.
- Strict: Direct failure wording with less softening.
- Technical: More code/spec language and precise construction terminology.

CURRENT COMMENT FIELD:
{(string.IsNullOrWhiteSpace(trimmedCurrentComment) ? "(empty)" : trimmedCurrentComment)}

ACTIVE COMMENT MODE: {commentMode}
{commentModeInstructions}

CRITICAL RULES:
1. TRADE ASSIGNMENT - Start each response with the responsible trade in SQUARE BRACKETS. BE SPECIFIC:
   - [builder] - only when truly no specific trade applies (less than 10% of cases)
   - [cabinet] - cabinets, countertops, trim work
   - [concrete] - foundation, flatwork, formwork, rebar, mud
   - [drywall] - hanging, taping, finishing, texture
   - [electrician] - wiring, panels, outlets, switches, lights
   - [flooring] - tile, carpet, hardwood, vinyl
   - [framer] - wood framing, studs, joists, trusses, sheathing
   - [hvac] - heating, cooling, ductwork, vents, thermostats
   - [insulation] - batts, blown-in, foam, vapor barriers
   - [landscaping] - grading, sod, plants, irrigation
   - [mason] - brick, stone, block work
   - [painter] - interior/exterior paint, trim, caulk
   - [plumber] - pipes, drains, water lines, gas lines, fixtures
   - [roofer] - shingles, flashing, underlayment, valleys
   - [siding] - exterior siding installation
   - [tile] - ceramic, stone, grout work
   - [window] - window and door installation

2. DO NOT CITE INTERNAL DOCUMENTS: Never reference ""Global failure library"", ""knowledge base"", or internal file names. Use the information but cite only:
   - Building codes: ""per 2021 IRC R404.1.6""
   - Manufacturer specs: ""per Simpson StrongTie""
   - Engineering: ""per engineer's details""
   - Or no citation if none applies

3. KNOWLEDGE BASE PRIORITY: If the knowledge base has corrections for this item number, USE THAT EXACT LANGUAGE (but fix trade assignment if needed).

4. ACTION-ORIENTED: Start with action verbs (Install, Repair, Adjust, Secure, Relocate, Replace, Clean, Remove, etc.)

5. CITATIONS: Add verifiable citations when applicable:
   - 'per 2021 IRC [code]' for building codes
   - 'per Simpson StrongTie' for hardware
   - 'per engineer's details' for plan-designed aspects
   - Skip citation if no clear authority

6. LANGUAGE REQUIREMENTS:
   - ONE sentence preferred
   - TWO sentences maximum (first = action, second = commentary if needed)
   - THREE sentences FORBIDDEN
   - NO run-on sentences
   - Each suggestion MUST end with a single period (.) - no quotes, commas, or other punctuation after the period
   - Capitalize first letter of each sentence
   - Be specific, formal, brief
   - Avoid vague language, fragments, hallucinations

7. RANKING: Sort by likelihood using this priority:
   a) Most common failure for this item in knowledge base (check Global Usage count)
   b) What's visible in the photo
   c) General knowledge

GOOD EXAMPLES:
- ""[concrete] Secure loose poly.""
- ""[concrete] Tape holes in vapor barrier.""
- ""[plumber] Install a compliant disconnect at the Water Heater per NEC 422.31.""
- ""[framer] Relocate the corner anchors to maintain at least 6 inches from the outside edge of concrete to the cable center per engineer's details.""
- ""[hvac] Install the fasteners to complete the connector per manufacturer's instructions.""
- ""[electrician] Repair the AC disconnect. Inoperable.""
- ""[landscaping] Grade soil to slope away from foundation per IRC R401.3.""

BAD EXAMPLES (DO NOT USE):
- ""(builder) Clean the site."" (Wrong brackets - use [builder] not (builder))
- ""the AC doesn't work"" (no trade, no action verb, vague)
- ""per Global failure library #1234"" (don't cite internal docs)

{(string.IsNullOrEmpty(previousSuggestions) ? "" : $"\nDO NOT REUSE these previous suggestions:\n{previousSuggestions}\n")}

Format your response as a JSON array of exactly 3 strings ranked by likelihood.
Example: [""[concrete] Action here."", ""[framer] Alternative action."", ""[plumber] Third option.""]

Only return the JSON array, nothing else.";

                string responseText = await MakeAiRequestWithFallback(prompt, imageDataUrl, aiRequest);

                var result = JsonConvert.DeserializeObject<GeminiResponse>(responseText);
                
                string aiResponse = ExtractGeminiText(result);
                if (!string.IsNullOrWhiteSpace(aiResponse))
                {
                    var suggestions = ParseSuggestionResponse(aiResponse);
                    if (suggestions.Count > 0)
                    {
                        return suggestions;
                    }
                }

                return new List<string>
                {
                    "⚠️ AI returned no usable suggestion",
                    $"Raw response: {TruncateForDisplay(aiResponse)}",
                    "Try again or enter your comment manually."
                };
            }
            catch (OperationCanceledException)
            {
                return new List<string>
                {
                    "⚠️ Request timed out",
                    "The AI server took too long to respond. This usually means a weak connection.",
                    "Try again when you have a stronger signal."
                };
            }
            catch (HttpRequestException)
            {
                return new List<string>
                {
                    "⚠️ No internet connection",
                    "AI features need Wi-Fi or cell data to work.",
                    "Check your connection and try again."
                };
            }
            catch (GeminiApiException ex) when (IsTransientApiStatus(ex.StatusCode))
            {
                return new List<string>
                {
                    "⚠️ Gemini is busy",
                    "The AI service is temporarily overloaded.",
                    "Try again in a moment."
                };
            }
            catch (Exception ex)
            {
                return new List<string>
                {
                    $"⚠️ Error: {ex.Message}",
                    "Something went wrong with the AI request.",
                    "Try again or enter your comment manually."
                };
            }
        }

        public async Task<string> FactCheckInspectionComment(
            string comment,
            string itemNumber,
            string question,
            string inspectionType = "",
            string sectionName = "",
            byte[]? imageData = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(comment))
                    return "No comment to fact-check.";

                if (!await CheckConnectivityAsync())
                    return "No internet connection. Fact-check needs Wi-Fi or cell data.";

                string inspectionNameForAi = GetInspectionNameForAi(inspectionType);
                string context = string.Join("\n", new[]
                {
                    string.IsNullOrWhiteSpace(inspectionNameForAi) ? "" : $"Inspection type: {inspectionNameForAi}",
                    string.IsNullOrWhiteSpace(sectionName) ? "" : $"Section: {sectionName}",
                    $"Checklist item: {itemNumber} {question}".Trim()
                }.Where(line => !string.IsNullOrWhiteSpace(line)));

                string? imageDataUrl = null;
                if (imageData != null && imageData.Length is > 0 and <= 700_000)
                    imageDataUrl = $"data:image/jpeg;base64,{Convert.ToBase64String(imageData)}";

                string prompt = $@"You are a strict technical building inspection reviewer.
Fact-check the inspector's proposed comment for technical accuracy, clarity, and whether it fits the checklist item.
If a photo is provided, use it as direct visual context. If the comment makes an absence claim such as ""not present"" or ""no insulation required"", check whether the photo and checklist context are enough to support that claim.

{context}

COMMENT:
{comment.Trim()}

Research the issue using authoritative sources when available, such as model code, IRC/IECC language, manufacturer instructions, or recognized construction guidance.

Reply in one complete field-useful paragraph. No markdown. No email wording.
If the comment is technically sound, start with ""Looks correct:"" and briefly say why.
If it may be wrong or incomplete, start with ""Check this:"" and give the smallest useful correction.
If more context is required, start with ""Need context:"" and give a conditional answer the inspector can use immediately.
For conditional answers, state both sides briefly, for example: ""Need context: If this is exterior brick veneer, the joint is required; if it is interior decorative brick, it may not be required.""
Use strict technical wording. Include a brief authority reference in the sentence when possible, such as ""per IRC R703"" or ""per manufacturer installation instructions."" Do not rewrite the whole comment unless a correction is needed.
Do not abbreviate checklist names, code sections, or the final word. Always finish the sentence.";

                string aiResponse;
                try
                {
                    aiResponse = await GetFactCheckResponseText(prompt, imageDataUrl);
                }
                catch (OperationCanceledException) when (!string.IsNullOrWhiteSpace(imageDataUrl))
                {
                    string textOnlyPrompt = prompt + @"

The photo payload timed out. Answer from the checklist and comment context only.";
                    aiResponse = await GetFactCheckResponseText(textOnlyPrompt, imageDataUrl: null);
                }

                aiResponse = NormalizeFactCheckResponse(aiResponse);

                if (string.IsNullOrWhiteSpace(aiResponse))
                {
                    string retryPrompt = prompt + @"

Your previous answer was empty. Return exactly one complete sentence starting with Looks correct:, Check this:, or Need context:.
If using Need context:, include the likely conditional rule instead of only asking for more information.";
                    string retryText = await MakeTextOnlyApiRequestWithTimeout(
                        retryPrompt,
                        CAREFUL_LEGACY_MODEL,
                        45,
                        mediumReasoning: false,
                        maxOutputTokens: 1600,
                        imageDataUrl,
                        enableGoogleSearch: true);
                    aiResponse = NormalizeFactCheckResponse(ExtractFactCheckText(retryText, includeSources: true));
                }

                return !string.IsNullOrWhiteSpace(aiResponse)
                    ? aiResponse
                    : "AI fact-check did not return a usable answer. Try again or use Get 3 for rewrite suggestions.";
            }
            catch (OperationCanceledException)
            {
                return "The careful fact-check took too long. Try again with a stronger connection.";
            }
            catch (HttpRequestException)
            {
                return "No internet connection. Fact-check needs Wi-Fi or cell data.";
            }
            catch (GeminiApiException ex) when (IsTransientApiStatus(ex.StatusCode))
            {
                return "Gemini is busy. Try the fact-check again in a moment.";
            }
            catch (Exception ex)
            {
                return $"Fact-check error: {ex.Message}";
            }
        }

        /// <summary>
        /// Transcribe a product label, window sticker, or meter reading from an image.
        /// Returns 3 options with increasing verbosity (for Value field, not Comments).
        /// </summary>
        public async Task<List<string>> TranscribeLabelMultiple(byte[] imageData)
        {
            try
            {
                // Quick connectivity check before sending large image payload
                if (!await CheckConnectivityAsync())
                {
                    return new List<string> { "⚠️ No internet connection — Transcribe needs Wi-Fi or cell data to work. Check your connection and try again." };
                }
                
                string base64Image = Convert.ToBase64String(imageData);
                string imageDataUrl = $"data:image/jpeg;base64,{base64Image}";

                string prompt = @"You are transcribing a label, sticker, or meter reading from a construction inspection photo.

TASK: Extract information visible in this image and provide 3 transcription options with INCREASING DETAIL.

CRITICAL: Transcribe ALL characters in model numbers and serial numbers EXACTLY as printed. 
Do NOT truncate, abbreviate, or round any alphanumeric codes. Every character matters.
If a character is hard to read, provide your best guess wrapped in square brackets, for example Serial: 12[8]4A or Model: C[A]16NA036.
Use brackets only around uncertain characters, not around the whole value.

RULES:
1. NO trade prefixes like [hvac] or [insulation] - just the data
2. ALWAYS return exactly 3 separate strings. A single string is invalid, even if only one value is visible.
3. Three levels of detail:
   - OPTION 1 (SIMPLE): Just the essential info (Model/Serial OR U-Value/SHGC OR main reading)
   - OPTION 2 (MEDIUM): Add secondary details visible on the label
   - OPTION 3 (VERBOSE): Everything you can read from the label
4. If only one value is readable, repeat that value with clear increasing context:
   - OPTION 1: The direct value only
   - OPTION 2: The value with its likely field label expanded
   - OPTION 3: The value plus 'Additional label details unreadable'

EXAMPLES:

Equipment label:
1. ""Model: CA16NA036 / Serial: 1832E48823""
2. ""Model: CA16NA036 / Serial: 1832E48823 / BTU: 36,000""
3. ""Model: CA16NA036 / Serial: 1832E48823 / BTU: 36,000 / Voltage: 208-230V / Phase: 1 / Hz: 60""

Window NFRC sticker:
1. ""U-Value = 0.30 / SHGC = 0.25""
2. ""U-Value = 0.30 / SHGC = 0.25 / VT = 0.42""
3. ""U-Value = 0.30 / SHGC = 0.25 / VT = 0.42 / Air Leakage = 0.3 / CR = 55""

Water heater:
1. ""Model: XG50T06EC38U1 / Serial: 2145789321""
2. ""Model: XG50T06EC38U1 / Serial: 2145789321 / 50 Gal / 40,000 BTU""
3. ""Model: XG50T06EC38U1 / Serial: 2145789321 / 50 Gal / 40,000 BTU / EF: 0.62 / First Hour: 67 gal""

If you cannot read the label clearly, use: ""Unable to read label - try a clearer photo""

Format your response as a JSON array of exactly 3 strings from simplest to most verbose.
Example: [""Simple version"", ""Medium version"", ""Verbose version""]

Only return the JSON array, nothing else.";

                string responseText;
                try
                {
                    responseText = await MakeApiRequestWithTimeout(prompt, imageDataUrl, TRANSCRIBE_MODEL, PRIMARY_TIMEOUT_SECONDS);
                }
                catch (GeminiApiException ex) when (IsTransientApiStatus(ex.StatusCode))
                {
                    responseText = await MakeApiRequestWithTimeout(prompt, imageDataUrl, FAST_LEGACY_MODEL, 45);
                }
                
                // Parse API response to extract AI content (same as GetInspectionSuggestions)
                var result = JsonConvert.DeserializeObject<GeminiResponse>(responseText);
                
                string aiResponse = ExtractGeminiText(result);
                if (!string.IsNullOrWhiteSpace(aiResponse))
                {
                    var options = ParseTranscriptionOptions(aiResponse);
                    if (options.Count > 0)
                    {
                        return options;
                    }
                }
                
                // Fallback if no choices
                return new List<string> { "Unable to read label — try a clearer photo" };
            }
            catch (OperationCanceledException)
            {
                return new List<string> { "⚠️ Request timed out — the AI server took too long. Try again when you have a stronger signal." };
            }
            catch (HttpRequestException)
            {
                return new List<string> { "⚠️ No internet connection — Transcribe needs Wi-Fi or cell data to work." };
            }
            catch (GeminiApiException ex) when (IsTransientApiStatus(ex.StatusCode))
            {
                return new List<string> { "⚠️ Gemini is busy — Transcribe retried with the backup model, but the service is still overloaded. Try again in a moment." };
            }
            catch (Exception ex)
            {
                return new List<string> { $"⚠️ Error: {ex.Message}" };
            }
        }

        /// <summary>
        /// Get the collection ID for a given inspection type.
        /// Falls back to DEFAULT_COLLECTION_ID if no stage-specific collection exists.
        /// </summary>
        private string GetCollectionId(string inspectionType)
        {
            if (!string.IsNullOrEmpty(inspectionType) && CollectionMap.TryGetValue(inspectionType, out var collectionId))
            {
                return collectionId;
            }
            return DEFAULT_COLLECTION_ID;
        }

        /// <summary>
        /// Legacy Grok collection search hook. Gemini does not have access to xAI document collections,
        /// so this is intentionally disabled until the RED knowledge base is migrated.
        /// </summary>
        private Task<string> SearchCollectionAsync(string query, string inspectionType, int maxResults = 5)
        {
            return Task.FromResult("");
        }

        /// <summary>
        /// Make API request with specified model and timeout.
        /// </summary>
        private async Task<string> MakeAiRequestWithFallback(string prompt, string imageDataUrl, AiRequestStyle style)
        {
            string primaryModel = style.Careful ? CAREFUL_MODEL : FAST_MODEL;
            string fallbackModel = style.Careful ? CAREFUL_LEGACY_MODEL : FAST_LEGACY_MODEL;
            int timeout = style.Careful ? 45 : PRIMARY_TIMEOUT_SECONDS;

            try
            {
                return await MakeApiRequestWithTimeout(prompt, imageDataUrl, primaryModel, timeout, style.Careful);
            }
            catch (OperationCanceledException) when (style.Careful)
            {
                OnModelFallback?.Invoke();
                return await MakeApiRequestWithTimeout(prompt, imageDataUrl, fallbackModel, 45);
            }
            catch (GeminiApiException ex) when (IsUnsupportedModelStatus(ex.StatusCode))
            {
                return await MakeApiRequestWithTimeout(prompt, imageDataUrl, fallbackModel, timeout);
            }
        }

        private async Task<string> GetFactCheckResponseText(string prompt, AiRequestStyle style, string? imageDataUrl)
        {
            string responseText = await MakeTextOnlyAiRequestWithFallback(prompt, style, maxOutputTokens: 700, imageDataUrl);
            return ExtractFactCheckText(responseText);
        }

        private async Task<string> GetFactCheckResponseText(string prompt, string? imageDataUrl)
        {
            string responseText = await MakeTextOnlyApiRequestWithTimeout(
                prompt,
                CAREFUL_LEGACY_MODEL,
                timeoutSeconds: 45,
                mediumReasoning: false,
                maxOutputTokens: 1600,
                imageDataUrl,
                enableGoogleSearch: true);

            return ExtractFactCheckText(responseText, includeSources: true);
        }

        private static string ExtractFactCheckText(string responseText, bool includeSources = false)
        {
            var result = JsonConvert.DeserializeObject<GeminiResponse>(responseText);
            string aiResponse = ExtractGeminiText(result);
            string cleaned = StripCodeFences(aiResponse)
                .Trim()
                .Trim('"', '\'');

            if (includeSources)
                cleaned = AppendGroundingSources(cleaned, result);

            return cleaned;
        }

        private static string AppendGroundingSources(string text, GeminiResponse? response)
        {
            var sources = response?.Candidates?
                .SelectMany(candidate => candidate.GroundingMetadata?.GroundingChunks ?? new List<GeminiGroundingChunk>())
                .Select(chunk => chunk.Web)
                .Where(web => web != null && !string.IsNullOrWhiteSpace(web.Uri))
                .GroupBy(web => web!.Uri!, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()!)
                .Take(3)
                .ToList() ?? new List<GeminiGroundingWeb>();

            if (sources.Count == 0)
                return text;

            var builder = new StringBuilder(text.Trim());
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine("Sources:");
            foreach (var source in sources)
            {
                string title = string.IsNullOrWhiteSpace(source.Title) ? "Source" : source.Title.Trim();
                builder.AppendLine($"- {title}: {source.Uri}");
            }

            return builder.ToString().Trim();
        }

        private static bool IsUsableFactCheckResponse(string aiResponse)
        {
            if (string.IsNullOrWhiteSpace(aiResponse))
                return false;

            string value = aiResponse.Trim();
            if (value.Length < 24)
                return false;

            if (!Regex.IsMatch(value, @"^(Looks correct|Check this|Need context):", RegexOptions.IgnoreCase))
                return false;

            return Regex.IsMatch(value, @"[.!?]\s*$");
        }

        private static string NormalizeFactCheckResponse(string aiResponse)
        {
            string value = StripCodeFences(aiResponse)
                .Trim()
                .Trim('"', '\'');

            if (string.IsNullOrWhiteSpace(value))
                return "";

            if (value.StartsWith("[") && value.EndsWith("]"))
            {
                try
                {
                    var entries = JsonConvert.DeserializeObject<List<string>>(value);
                    value = entries?.FirstOrDefault(entry => !string.IsNullOrWhiteSpace(entry))?.Trim() ?? value;
                }
                catch
                {
                    // Keep the original text if it was not actually a JSON array.
                }
            }

            value = Regex.Replace(value, @"\s+", " ").Trim();
            value = value.Trim('"', '\'');

            if (Regex.IsMatch(value, @"^(Looks correct|Check this|Need context):", RegexOptions.IgnoreCase))
                return EnsureSentenceEnding(value);

            Match loosePrefix = Regex.Match(value, @"^(Looks correct|Check this|Need context)\b\s*-?\s*", RegexOptions.IgnoreCase);
            if (loosePrefix.Success)
            {
                string prefix = loosePrefix.Groups[1].Value.ToLowerInvariant() switch
                {
                    "looks correct" => "Looks correct",
                    "need context" => "Need context",
                    _ => "Check this"
                };
                string body = value.Substring(loosePrefix.Length).TrimStart(':', '-', ' ');
                return EnsureSentenceEnding($"{prefix}: {body}");
            }

            string lower = value.ToLowerInvariant();
            string normalizedPrefix =
                lower.Contains("need") && lower.Contains("context") ? "Need context" :
                lower.Contains("correct") || lower.Contains("acceptable") || lower.Contains("technically sound") || lower.Contains("accurate") ? "Looks correct" :
                "Check this";

            return EnsureSentenceEnding($"{normalizedPrefix}: {value}");
        }

        private static string EnsureSentenceEnding(string value)
        {
            value = value.Trim();
            return Regex.IsMatch(value, @"[.!?]\s*$") ? value : value + ".";
        }

        private async Task<string> MakeTextOnlyAiRequestWithFallback(string prompt, AiRequestStyle style, int maxOutputTokens, string? imageDataUrl = null)
        {
            string primaryModel = style.Careful ? CAREFUL_MODEL : FAST_MODEL;
            string fallbackModel = style.Careful ? CAREFUL_LEGACY_MODEL : FAST_LEGACY_MODEL;
            int timeout = style.Careful ? 45 : PRIMARY_TIMEOUT_SECONDS;

            try
            {
                return await MakeTextOnlyApiRequestWithTimeout(prompt, primaryModel, timeout, style.Careful, maxOutputTokens, imageDataUrl);
            }
            catch (OperationCanceledException) when (style.Careful)
            {
                OnModelFallback?.Invoke();
                return await MakeTextOnlyApiRequestWithTimeout(prompt, fallbackModel, 45, mediumReasoning: false, maxOutputTokens, imageDataUrl);
            }
            catch (GeminiApiException ex) when (IsUnsupportedModelStatus(ex.StatusCode))
            {
                return await MakeTextOnlyApiRequestWithTimeout(prompt, fallbackModel, timeout, mediumReasoning: false, maxOutputTokens, imageDataUrl);
            }
        }

        private async Task<string> MakeTextOnlyApiRequestWithTimeout(string prompt, string model, int timeoutSeconds, bool mediumReasoning, int maxOutputTokens, string? imageDataUrl = null, bool enableGoogleSearch = false)
        {
            var generationConfig = new Dictionary<string, object>
            {
                ["temperature"] = 0.15,
                ["maxOutputTokens"] = maxOutputTokens,
                ["responseMimeType"] = "text/plain"
            };

            if (mediumReasoning)
            {
                generationConfig["thinkingConfig"] = new
                {
                    thinkingLevel = "medium"
                };
            }

            var parts = new List<object> { new { text = prompt } };
            if (!string.IsNullOrWhiteSpace(imageDataUrl))
            {
                string base64Image = imageDataUrl;
                const string jpegPrefix = "data:image/jpeg;base64,";
                if (base64Image.StartsWith(jpegPrefix, StringComparison.OrdinalIgnoreCase))
                    base64Image = base64Image.Substring(jpegPrefix.Length);

                parts.Add(new
                {
                    inline_data = new
                    {
                        mime_type = "image/jpeg",
                        data = base64Image
                    }
                });
            }

            var requestBody = new Dictionary<string, object>
            {
                ["contents"] = new[]
                {
                    new
                    {
                        role = "user",
                        parts = parts.ToArray()
                    }
                },
                ["generationConfig"] = generationConfig
            };

            if (enableGoogleSearch)
            {
                requestBody["tools"] = new[]
                {
                    new
                    {
                        google_search = new { }
                    }
                };
            }

            string jsonContent = JsonConvert.SerializeObject(requestBody);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            for (int attempt = 0; attempt < 3; attempt++)
            {
                using var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(string.Format(API_URL, model), content, cts.Token);
                var responseText = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                    return responseText;

                if (attempt < 2 && IsTransientApiStatus(response.StatusCode))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(700 * (attempt + 1)), cts.Token);
                    continue;
                }

                throw new GeminiApiException(response.StatusCode, responseText);
            }

            throw new GeminiApiException(HttpStatusCode.ServiceUnavailable, "Gemini API did not return a response.");
        }

        private async Task<string> MakeApiRequestWithTimeout(string prompt, string imageDataUrl, string model, int timeoutSeconds, bool mediumReasoning = false)
        {
            string base64Image = imageDataUrl;
            const string jpegPrefix = "data:image/jpeg;base64,";
            if (base64Image.StartsWith(jpegPrefix, StringComparison.OrdinalIgnoreCase))
                base64Image = base64Image.Substring(jpegPrefix.Length);

            var generationConfig = new Dictionary<string, object>
            {
                ["temperature"] = 0.35,
                ["maxOutputTokens"] = 800,
                ["responseMimeType"] = "application/json",
                ["responseJsonSchema"] = new
                {
                    type = "array",
                    items = new { type = "string" }
                }
            };

            if (mediumReasoning)
            {
                generationConfig["thinkingConfig"] = new
                {
                    thinkingLevel = "medium"
                };
            }

            var requestBody = new
            {
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new object[]
                        {
                            new { text = prompt },
                            new
                            {
                                inline_data = new
                                {
                                    mime_type = "image/jpeg",
                                    data = base64Image
                                }
                            }
                        }
                    }
                },
                generationConfig
            };

            string jsonContent = JsonConvert.SerializeObject(requestBody);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            for (int attempt = 0; attempt < 3; attempt++)
            {
                using var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(string.Format(API_URL, model), content, cts.Token);
                var responseText = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    return responseText;
                }

                if (attempt < 2 && IsTransientApiStatus(response.StatusCode))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(700 * (attempt + 1)), cts.Token);
                    continue;
                }

                throw new GeminiApiException(response.StatusCode, responseText);
            }

            throw new GeminiApiException(HttpStatusCode.ServiceUnavailable, "Gemini API did not return a response.");
        }

        private static bool IsTransientApiStatus(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.TooManyRequests
                || statusCode == HttpStatusCode.BadGateway
                || statusCode == HttpStatusCode.ServiceUnavailable
                || statusCode == HttpStatusCode.GatewayTimeout;
        }

        private static bool IsUnsupportedModelStatus(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.NotFound || statusCode == HttpStatusCode.BadRequest;
        }

        private static List<string> ParseTranscriptionOptions(string aiResponse)
        {
            string cleanedResponse = StripCodeFences(aiResponse);

            try
            {
                var parsed = JsonConvert.DeserializeObject<List<string>>(cleanedResponse);
                var cleaned = CleanTranscriptionOptions(parsed);
                if (cleaned.Count > 0) return cleaned;
            }
            catch
            {
                // Gemini can occasionally emit malformed JSON with raw newlines inside a value.
            }

            var quotedOptions = Regex.Matches(cleanedResponse, "\"(?:\\\\.|[^\"\\\\])*\"", RegexOptions.Singleline)
                .Cast<Match>()
                .Select(match =>
                {
                    try { return JsonConvert.DeserializeObject<string>(match.Value) ?? ""; }
                    catch { return match.Value.Trim('"'); }
                })
                .ToList();

            var cleanedQuoted = CleanTranscriptionOptions(quotedOptions);
            if (cleanedQuoted.Count > 0) return cleanedQuoted;

            string lineFriendly = cleanedResponse
                .Replace("\\r", "\n")
                .Replace("\\n", "\n")
                .Replace("[", "")
                .Replace("]", "")
                .Replace("\"", "");

            var lineOptions = lineFriendly
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim().TrimEnd(','))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            var cleanedLines = CleanTranscriptionOptions(lineOptions);
            if (cleanedLines.Count > 0) return cleanedLines;

            return CleanTranscriptionOptions(new[] { cleanedResponse });
        }

        private static string StripCodeFences(string text)
        {
            text = (text ?? "").Trim();
            if (!text.StartsWith("```")) return text;

            int firstNewline = text.IndexOf('\n');
            if (firstNewline > 0)
            {
                text = text.Substring(firstNewline + 1);
            }

            if (text.EndsWith("```"))
            {
                text = text.Substring(0, text.Length - 3);
            }

            return text.Trim();
        }

        private static List<string> CleanTranscriptionOptions(IEnumerable<string>? options)
        {
            if (options == null) return new List<string>();

            var cleaned = new List<string>();
            foreach (var option in options)
            {
                string value = CleanTranscriptionOption(option);
                if (value.Length == 0) continue;
                if (cleaned.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))) continue;

                cleaned.Add(value);
                if (cleaned.Count == 3) break;
            }

            if (cleaned.Count == 2)
            {
                cleaned.Add($"{cleaned[1]} / Additional values unreadable");
            }
            else if (cleaned.Count == 1)
            {
                string only = cleaned[0];
                cleaned.Add(ExpandSingleTranscriptionOption(only));
                cleaned.Add($"{cleaned[1]} / Additional label details unreadable");
            }

            return cleaned;
        }

        private static string ExpandSingleTranscriptionOption(string option)
        {
            if (string.IsNullOrWhiteSpace(option))
                return "Additional values unreadable";

            if (Regex.IsMatch(option, @"\b(plan|model|serial|u[-\s]?factor|shgc|r[-\s]?value|window|wall|ceiling|floor)\b\s*[:=]",
                    RegexOptions.IgnoreCase))
                return $"{option} / Additional values unreadable";

            return $"Value: {option}";
        }

        private static string CleanTranscriptionOption(string option)
        {
            if (string.IsNullOrWhiteSpace(option)) return "";

            string value = option
                .Replace("\\r", " ")
                .Replace("\\n", " ")
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace("\t", " ")
                .Trim();

            value = Regex.Replace(value, @"\s+", " ");
            value = value.Trim('[', ']', '"', '\'', ',', ' ');

            while (Regex.IsMatch(value, @"(?:\s*/\s*)?[^/=:]{1,35}[:=]\s*$"))
            {
                value = Regex.Replace(value, @"(?:\s*/\s*)?[^/=:]{1,35}[:=]\s*$", "").Trim();
            }

            return value.Trim().TrimEnd(',', '/', ' ');
        }

        /// <summary>
        /// Clean up a suggestion string, removing JSON artifacts and ensuring proper punctuation.
        /// </summary>
        private List<string> ParseSuggestionResponse(string aiResponse)
        {
            var suggestions = new List<string>();

            void AddCandidate(string? candidate)
            {
                string cleaned = CleanSuggestion(candidate ?? "");
                if (string.IsNullOrWhiteSpace(cleaned) || cleaned.Length <= 10)
                    return;

                if (suggestions.Any(existing => string.Equals(existing, cleaned, StringComparison.OrdinalIgnoreCase)))
                    return;

                suggestions.Add(cleaned);
            }

            try
            {
                var jsonSuggestions = JsonConvert.DeserializeObject<List<string>>(aiResponse);
                if (jsonSuggestions != null)
                {
                    foreach (string suggestion in jsonSuggestions)
                    {
                        AddCandidate(suggestion);
                        if (suggestions.Count >= 3) break;
                    }
                }
            }
            catch
            {
                // Some Gemini replies are useful but malformed JSON. Fall through to tolerant parsing.
            }

            if (suggestions.Count == 0)
            {
                foreach (Match match in Regex.Matches(aiResponse, "\"((?:\\\\.|[^\"])*)\""))
                {
                    string candidate = match.Groups[1].Value
                        .Replace("\\\"", "\"")
                        .Replace("\\n", " ")
                        .Replace("\\r", " ");

                    AddCandidate(candidate);
                    if (suggestions.Count >= 3) break;
                }
            }

            if (suggestions.Count == 0)
            {
                var lines = aiResponse.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    string candidate = line.Trim()
                        .TrimStart('-', '*', '1', '2', '3', '.', ' ', '"', '[', ']')
                        .TrimEnd('"', ']', ',', ' ');

                    AddCandidate(candidate);
                    if (suggestions.Count >= 3) break;
                }
            }

            return suggestions.Take(3).ToList();
        }

        private string CleanSuggestion(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s;
            
            // Trim whitespace
            s = s.Trim();
            
            // Remove trailing JSON artifacts: .", or ", or .]
            while (s.EndsWith("\",") || s.EndsWith(".',") || s.EndsWith("\"]") || s.EndsWith("',"))
            {
                s = s.TrimEnd(',', ']').TrimEnd('"', '\'').Trim();
            }
            
            // Remove leading/trailing quotes
            s = s.Trim('"', '\'');
            
            // Remove trailing commas, quotes that might remain
            s = s.TrimEnd(',', '"', '\'', ' ');
            
            // Ensure ends with period (if it has content and doesn't already)
            if (s.Length > 0 && !s.EndsWith(".") && !s.EndsWith("!") && !s.EndsWith("?"))
            {
                s = s + ".";
            }
            
            return s;
        }


        private static string GetInspectionNameForAi(string inspectionType)
        {
            return (inspectionType ?? "").Trim().ToUpperInvariant() switch
            {
                "CPP" => "Concrete Pre Pour - inspecting formwork, rebar, post-tension cables, vapor barrier, sleeves, and preparations before concrete placement",
                "CPR" => "Concrete Pre Pour Residential - inspecting formwork, rebar, post-tension cables, vapor barrier, sleeves, and preparations before concrete placement",
                "SRP" => "Slab Rebar Post Tension - inspecting slab reinforcement, post-tension cables, vapor barrier, forms, and embedments",
                "STR" => "Structural - inspecting structural framing, load paths, anchors, and engineered details",
                "FS" => "Framing Standard - inspecting structural framing, connections, hardware, and rough construction",
                "FSF" => "Framing Standard Final - final framing inspection for structural framing, connections, and hardware",
                "FWI" => "Framing Wall Inspection - inspecting wall framing, bracing, sheathing, and connections",
                "ME" => "Mechanical - inspecting HVAC equipment, ductwork, vents, and mechanical installation",
                "MP" => "Mechanical Plumbing - inspecting HVAC, plumbing, piping, drains, and related rough-in items",
                "TPC" => "Tile Post Construction - inspecting tile, waterproofing, and post-construction finish details",
                "TFF" => "Tile Flat Final - inspecting tile, flatwork, and final finish details",
                "SWI" => "Stucco Weather Inspection - inspecting stucco, weather barrier, flashing, lath, and drainage details",
                "HER" => "Home Energy Rating - inspecting insulation, air sealing, thermal envelope, and energy efficiency items",
                "IER" => "Interim Energy Rating - mid-construction energy inspection for insulation, air sealing, and thermal envelope items",
                "IAP" => "Indoor Air Plus - inspecting indoor air quality requirements, materials, ventilation, and related details",
                "PLY" => "Plumbing - inspecting plumbing lines, fixtures, drains, vents, and water/gas piping",
                "HEF" => "Home Electrical Final - final electrical inspection for devices, fixtures, panels, and safety items",
                "IEF" => "Interim Electrical Final - interim final electrical inspection for devices, fixtures, panels, and safety items",
                "HET" => "Home Electrical Test - electrical testing and verification inspection",
                "QIER" => "Interim Energy Rating - mid-construction energy inspection for insulation, air sealing, and thermal envelope items",
                "AFI" => "ACCA Test - HVAC system testing, airflow verification, and equipment performance inspection",
                "TRDI" => "Trade Inspection - inspecting trade-specific construction work and correction items",
                "TRSI" => "Trade Structural Inspection - inspecting trade-specific structural work and correction items",
                "BC" => "Building Code - inspecting building code compliance items",
                "BF" => "Building Final - final building inspection before occupancy or completion",
                "COH" => "Certificate of Habitability - final habitability inspection for occupancy readiness",
                "BWT" => "Builder Warranty - inspecting warranty-related construction items",
                "SCI" => "Specialty Construction Inspection - inspecting specialty construction details",
                "PPE" => "Pre Purchase Evaluation - inspecting visible condition and correction items",
                "SWD" => "Stucco Weather Drainage - inspecting exterior weather barriers, stucco, flashing, and drainage",
                "FRM" => "Framing - inspecting structural framing, connections, hardware, and rough construction",
                "FNL" => "Final - final inspection before occupancy or completion",
                "" => "",
                _ => inspectionType ?? ""
            };
        }

        private static string TruncateForDisplay(string value, int maxLength = 350)
        {
            if (string.IsNullOrWhiteSpace(value)) return "(empty)";
            value = value.Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
        }

        private static string NormalizeTone(string tone)
        {
            string value = (tone ?? "").Trim().ToLowerInvariant();
            if (value.Contains("strict")) return "Strict";
            if (value.Contains("technical")) return "Technical";
            return "Nice";
        }

        private static AiRequestStyle ParseAiRequestStyle(string tone)
        {
            string value = (tone ?? "").Trim().ToLowerInvariant();
            return new AiRequestStyle(
                NormalizeTone(tone),
                value.Contains("careful"));
        }

        private readonly record struct AiRequestStyle(string Tone, bool Careful);

        private static string ExtractGeminiText(GeminiResponse? response)
        {
            return response?.Candidates?
                .FirstOrDefault()?.Content?.Parts?
                .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Text))?.Text?
                .Trim() ?? "";
        }

        private class GeminiApiException : Exception
        {
            public HttpStatusCode StatusCode { get; }

            public GeminiApiException(HttpStatusCode statusCode, string responseText)
                : base($"Gemini API error: {statusCode} - {responseText}")
            {
                StatusCode = statusCode;
            }
        }
        
        private class GeminiResponse
        {
            [JsonProperty("candidates")]
            public List<GeminiCandidate>? Candidates { get; set; }
        }

        private class GeminiCandidate
        {
            [JsonProperty("content")]
            public GeminiContent? Content { get; set; }

            [JsonProperty("groundingMetadata")]
            public GeminiGroundingMetadata? GroundingMetadata { get; set; }
        }

        private class GeminiContent
        {
            [JsonProperty("parts")]
            public List<GeminiPart>? Parts { get; set; }
        }

        private class GeminiPart
        {
            [JsonProperty("text")]
            public string? Text { get; set; }
        }

        private class GeminiGroundingMetadata
        {
            [JsonProperty("groundingChunks")]
            public List<GeminiGroundingChunk>? GroundingChunks { get; set; }
        }

        private class GeminiGroundingChunk
        {
            [JsonProperty("web")]
            public GeminiGroundingWeb? Web { get; set; }
        }

        private class GeminiGroundingWeb
        {
            [JsonProperty("uri")]
            public string? Uri { get; set; }

            [JsonProperty("title")]
            public string? Title { get; set; }
        }
    }
}
