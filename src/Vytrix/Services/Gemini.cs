using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vytrix.Core;

namespace Vytrix.Services;

/// <summary>
/// The assistant on Google's Gemini, using the user's own free Google AI Studio key
/// (generateContent REST API). Same tools, rules and validation as the other engines.
/// </summary>
public sealed class GeminiAssistant
{
    /// <summary>
    /// Free models on Google's Standard tier (ai.google.dev pricing, November 2026), in the order tried.
    /// gemini-3.8-flash is the newest but was overloaded for free users (503 "high demand" and requests that
    /// never answered), while 3.6-flash and 3.5-flash-lite answered VYTRIX's real request in about a second.
    /// </summary>
    public static readonly string[] Models = ["gemini-3.6-flash", "gemini-3.5-flash-lite", "gemini-3.8-flash"];
    public static string DefaultModel => Models[0];
    public const string KeyPage = "https://aistudio.google.com/apikey";
    private const int MaxToolRounds = 8;

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    /// <summary>Per request; after this the next free model is tried.</summary>
    internal static TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
    private readonly List<JsonObject> _contents = [];
    private readonly string _key;
    private readonly string _baseUrl;
    private readonly string[] _models;
    private int _modelIndex;

    /// <summary>The model currently in use (it stays on whichever one last worked).</summary>
    public string Model => _models[_modelIndex];
    public int TurnCount { get; private set; }
    internal int MessageCount => _contents.Count;

    public GeminiAssistant(string apiKey, string? model = null, string? baseUrl = null)
    {
        _key = apiKey;
        _models = model is null ? Models : [model];
        _baseUrl = (baseUrl ?? "https://generativelanguage.googleapis.com").TrimEnd('/');
    }

    internal GeminiAssistant(string apiKey, string[] models, string? baseUrl)
    {
        _key = apiKey;
        _models = models;
        _baseUrl = (baseUrl ?? "https://generativelanguage.googleapis.com").TrimEnd('/');
    }

    /// <summary>Busy, rate-limited, timed out or retired: worth trying the next free model.</summary>
    internal static bool TryNextModel(GeminiException g) =>
        g.Status is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
            or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout or HttpStatusCode.NotFound;

    public void Reset() { _contents.Clear(); TurnCount = 0; }

    /// <summary>Tool specs in Gemini's functionDeclarations format. Tools without inputs get no parameters (Gemini rejects empty objects).</summary>
    internal static JsonArray ToolsJson()
    {
        var decls = new JsonArray();
        foreach (var s in AssistantSession.ToolSpecs)
        {
            var d = new JsonObject { ["name"] = s.Name, ["description"] = s.Description };
            var props = JsonNode.Parse(JsonSerializer.Serialize(s.Properties)) as JsonObject;
            if (props is { Count: > 0 })
                d["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = props,
                    ["required"] = new JsonArray(s.Required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
                };
            decls.Add(d);
        }
        return [new JsonObject { ["functionDeclarations"] = decls }];
    }

    public async Task<AssistantReply> SendAsync(string text, bool sharePcDetails, IProgress<string>? status = null, CancellationToken ct = default)
    {
        _contents.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) });
        TurnCount++;
        int turnStart = _contents.Count;
        // Every question starts with the most responsive model, and if every model is busy the list is
        // tried a second time after a short pause (Google's "high demand" errors are usually brief).
        _modelIndex = 0;
        int attemptsLeft = _models.Length * 2;
        while (true)
        {
            try { return await RunTurnAsync(sharePcDetails, status, ct); }
            catch (GeminiException g) when (TryNextModel(g) && --attemptsLeft > 0)
            {
                int next = (_modelIndex + 1) % _models.Length;
                Log.Warn("assistant", $"{Model} unavailable ({(int)g.Status}); trying {_models[next]}.");
                if (next == 0) { status?.Report("All free Gemini models are busy, trying again…"); await Task.Delay(CyclePause, ct); }
                else status?.Report("Gemini is busy, trying another free model…");
                _modelIndex = next;
                // Restart this question on the next model. Thought signatures belong to the model that made
                // them, so drop this turn's partial work and the signatures on earlier turns.
                if (_contents.Count > turnStart) _contents.RemoveRange(turnStart, _contents.Count - turnStart);
                foreach (var c in _contents)
                    if (c["parts"] is JsonArray parts)
                        foreach (var p in parts) (p as JsonObject)?.Remove("thoughtSignature");
            }
        }
    }

    private async Task<AssistantReply> RunTurnAsync(bool sharePcDetails, IProgress<string>? status, CancellationToken ct)
    {
        var actions = new List<AssistantAction>();
        string lastText = "";

        for (int round = 0; round <= MaxToolRounds; round++)
        {
            var request = new JsonObject
            {
                ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = AssistantSession.SystemPrompt }) },
                ["contents"] = new JsonArray(_contents.Select(c => (JsonNode)c.DeepClone()).ToArray()),
                ["tools"] = ToolsJson(),
                // gemini-3.8-flash thinks at "medium" by default before every reply, which made multi-step
                // answers feel stuck; "low" is plenty for this kind of help and much quicker.
                ["generationConfig"] = new JsonObject
                {
                    ["maxOutputTokens"] = 8192,
                    ["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = "low" },
                },
            };
            var body = await PostWithRetryAsync(request.ToJsonString(), round, status, ct);

            var root = JsonNode.Parse(body);
            if (root?["promptFeedback"]?["blockReason"] is not null || root?["candidates"] is not JsonArray { Count: > 0 } cands)
            {
                RollBackTurn();
                return new AssistantReply("Gemini declined to answer that one. Try asking about your PC's speed, games, startup, network or a specific tweak.", []);
            }
            var cand = cands[0]!;
            var finish = cand["finishReason"]?.GetValue<string>() ?? "";
            if (cand["content"] is not JsonObject content)
            {
                RollBackTurn();
                return new AssistantReply(finish is "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST"
                    ? "Gemini declined to answer that one. Try asking about your PC's speed, games, startup, network or a specific tweak."
                    : "Gemini sent an empty reply. Try again.", []);
            }

            // The model's turn goes back exactly as received: Gemini 3+ requires its thought signatures.
            content["role"] ??= "model";
            _contents.Add((JsonObject)content.DeepClone());

            var parts = content["parts"] as JsonArray ?? [];
            var texts = parts.Where(p => p?["text"] is not null && p?["thought"]?.GetValue<bool>() != true).Select(p => p!["text"]!.GetValue<string>()).ToList();
            if (texts.Count > 0 && string.Concat(texts).Trim().Length > 0) lastText = string.Concat(texts).Trim();
            var calls = parts.Where(p => p?["functionCall"] is not null).Select(p => p!["functionCall"]!).ToList();

            if (calls.Count == 0)
            {
                var reply = lastText.Length > 0 ? lastText : "Done.";
                if (finish == "MAX_TOKENS") reply += "\n\n(Reply cut short.)";
                // The lighter fallback model sometimes recommends things without calling suggest_actions.
                if (actions.Count == 0) actions.AddRange(LocalAssistant.InferActions(reply));
                return new AssistantReply(reply, actions);
            }

            // All results for this turn go back together in one user turn, in call order.
            var responses = new JsonArray();
            foreach (var call in calls)
            {
                var name = call["name"]?.GetValue<string>() ?? "";
                status?.Report(AssistantSession.StatusFor(name));
                string result;
                try { result = AssistantSession.RunTool(name, LocalAssistant.ParseArgs(call["args"]), sharePcDetails, actions); }
                catch (Exception ex) { result = $"Tool failed: {ex.Message}"; }
                var fr = new JsonObject { ["name"] = name, ["response"] = new JsonObject { ["result"] = result } };
                if (call["id"] is JsonNode id) fr["id"] = id.DeepClone();
                responses.Add(new JsonObject { ["functionResponse"] = fr });
            }
            _contents.Add(new JsonObject { ["role"] = "user", ["parts"] = responses });
        }
        return new AssistantReply((lastText + "\n\n(Stopped after looking up a lot of information. Ask again to continue.)").Trim(), actions);
    }

    /// <summary>Pauses before retrying the same model after a brief "busy" error; tests shorten these.</summary>
    internal static TimeSpan[] BusyRetryDelays { get; set; } = [TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4)];
    internal static TimeSpan CyclePause { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Sends one request to the current model. A brief "busy" error (5xx) is retried on the same model
    /// first, which keeps this question's work and thought signatures valid; anything else is thrown.
    /// </summary>
    private async Task<string> PostWithRetryAsync(string json, int round, IProgress<string>? status, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1beta/models/{Model}:generateContent")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            msg.Headers.Add("x-goog-api-key", _key);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            HttpResponseMessage resp;
            string body;
            try
            {
                resp = await Http.SendAsync(msg, timeout.Token);
                body = await resp.Content.ReadAsStringAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log.Warn("assistant", $"{Model} round {round + 1} timed out after {sw.Elapsed.TotalSeconds:N0}s.");
                throw new GeminiException(HttpStatusCode.RequestTimeout, null);
            }
            using var _ = resp;
            Log.Info("assistant", $"{Model} round {round + 1}: HTTP {(int)resp.StatusCode} in {sw.Elapsed.TotalSeconds:N1}s.");
            if (resp.IsSuccessStatusCode) return body;
            bool busy = (int)resp.StatusCode >= 500;
            if (busy && attempt < BusyRetryDelays.Length)
            {
                status?.Report("Gemini is busy, retrying…");
                await Task.Delay(BusyRetryDelays[attempt], ct);
                continue;
            }
            throw new GeminiException(resp.StatusCode, ErrorMessage(body));
        }
    }

    private void RollBackTurn()
    {
        // Drop the unanswered question (and anything after it) so the chat can continue.
        int lastUser = _contents.FindLastIndex(c => c["role"]?.GetValue<string>() == "user" && c["parts"]?[0]?["text"] is not null);
        if (lastUser >= 0) _contents.RemoveRange(lastUser, _contents.Count - lastUser);
        TurnCount = Math.Max(0, TurnCount - 1);
    }

    private static string? ErrorMessage(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>(); } catch { return null; }
    }

    /// <summary>Plain-language message for a failure; never includes the key.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        GeminiException { Status: HttpStatusCode.RequestTimeout or HttpStatusCode.ServiceUnavailable } => "Google's free Gemini models are all busy right now. Try again in a few minutes, or switch to the free local AI in AI settings.",
        GeminiException { Status: HttpStatusCode.TooManyRequests } => "You've reached Gemini's free limit for now. Wait a minute (or until tomorrow for the daily limit), or switch to the free local AI in AI settings.",
        GeminiException g when g.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            || (g.Status == HttpStatusCode.BadRequest && (g.Detail ?? "").Contains("API key", StringComparison.OrdinalIgnoreCase))
            => "Your Gemini API key was rejected. Check it in AI settings, or create a new one in Google AI Studio.",
        GeminiException { Status: HttpStatusCode.NotFound } => "Google no longer offers VYTRIX's free Gemini models to this key. Check for a VYTRIX update.",
        GeminiException { Status: HttpStatusCode.BadRequest } g when (g.Detail ?? "").Contains("location", StringComparison.OrdinalIgnoreCase)
            => "Gemini's free tier isn't available in your region. Use the free local AI in AI settings instead.",
        GeminiException g when (int)g.Status >= 500 => "Gemini is busy right now. Try again in a minute.",
        GeminiException => "Gemini couldn't answer that request. Try again, or start a new chat.",
        HttpRequestException => "Couldn't reach Gemini. Check your internet connection.",
        TaskCanceledException t when !t.CancellationToken.IsCancellationRequested => "Gemini took too long to answer. Try again.",
        OperationCanceledException => "Stopped.",
        _ => "Something went wrong talking to Gemini. Try again.",
    };
}

public sealed class GeminiException(HttpStatusCode status, string? detail) : Exception($"Gemini returned {(int)status}")
{
    public HttpStatusCode Status { get; } = status;
    /// <summary>Google's own error text (never contains the key); used only to pick a friendly message.</summary>
    public string? Detail { get; } = detail;
}
