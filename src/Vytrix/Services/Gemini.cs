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
    /// <summary>Free on Google's Standard tier (checked against ai.google.dev pricing, November 2026).</summary>
    public const string DefaultModel = "gemini-3.8-flash";
    public const string KeyPage = "https://aistudio.google.com/apikey";
    private const int MaxToolRounds = 8;

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    /// <summary>Per request; a whole answer can take several requests.</summary>
    internal static TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(90);
    private readonly List<JsonObject> _contents = [];
    private readonly string _key;
    private readonly string _baseUrl;

    public string Model { get; }
    public int TurnCount { get; private set; }
    internal int MessageCount => _contents.Count;

    public GeminiAssistant(string apiKey, string model = DefaultModel, string? baseUrl = null)
    {
        _key = apiKey;
        Model = model;
        _baseUrl = (baseUrl ?? "https://generativelanguage.googleapis.com").TrimEnd('/');
    }

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
            using var msg = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1beta/models/{Model}:generateContent")
            {
                Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
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
                Log.Warn("assistant", $"Gemini round {round + 1} timed out after {sw.Elapsed.TotalSeconds:N0}s.");
                throw new GeminiException(HttpStatusCode.RequestTimeout, null);
            }
            using var _ = resp;
            Log.Info("assistant", $"Gemini round {round + 1}: HTTP {(int)resp.StatusCode} in {sw.Elapsed.TotalSeconds:N1}s.");
            if (!resp.IsSuccessStatusCode) throw new GeminiException(resp.StatusCode, ErrorMessage(body));

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
        GeminiException { Status: HttpStatusCode.RequestTimeout } => "Gemini didn't answer within 90 seconds. Try again, or ask a shorter question.",
        GeminiException { Status: HttpStatusCode.TooManyRequests } => "You've reached Gemini's free limit for now. Wait a minute (or until tomorrow for the daily limit), or switch to the free local AI in AI settings.",
        GeminiException g when g.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            || (g.Status == HttpStatusCode.BadRequest && (g.Detail ?? "").Contains("API key", StringComparison.OrdinalIgnoreCase))
            => "Your Gemini API key was rejected. Check it in AI settings, or create a new one in Google AI Studio.",
        GeminiException { Status: HttpStatusCode.NotFound } => $"Google doesn't offer the {DefaultModel} model to this key. It may have been renamed; check for a VYTRIX update.",
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
