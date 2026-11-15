using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Vytrix.Core;

namespace Vytrix.Services;

/// <summary>A free, open AI model VYTRIX can run on the user's PC through Ollama.</summary>
public sealed record LocalModel(string Id, string Name, string Size, string Note);

/// <summary>
/// Talks to Ollama (https://ollama.com), a free app that runs open AI models on this PC.
/// Everything stays on 127.0.0.1: no account, no key, nothing leaves the computer.
/// </summary>
public static class Ollama
{
    public static readonly LocalModel[] Models =
    [
        new("qwen3:4b", "Standard", "about 2.5 GB", "Fast on most PCs with 8 GB of RAM or a gaming GPU."),
        new("qwen3:8b", "Best quality", "about 5.2 GB", "Smarter answers. Best with a GPU that has 8 GB of video memory or more."),
    ];

    public const string DownloadPage = "https://ollama.com/download/windows";

    /// <summary>Overridable for tests (a local fake server).</summary>
    public static string BaseUrl { get; set; } = Environment.GetEnvironmentVariable("VYTRIX_OLLAMA_URL") is { Length: > 0 } u ? u.TrimEnd('/') : "http://127.0.0.1:11434";

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static string? ExePath
    {
        get
        {
            foreach (var p in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "ollama.exe"),
            })
                if (File.Exists(p)) return p;
            return null;
        }
    }

    public static bool Installed => ExePath is not null;

    /// <summary>Ollama's version if its local server is answering, else null.</summary>
    public static async Task<string?> VersionAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var json = await Http.GetStringAsync(BaseUrl + "/api/version", cts.Token);
            return JsonNode.Parse(json)?["version"]?.GetValue<string>();
        }
        catch { return null; }
    }

    /// <summary>Starts Ollama's background server if it's installed but not running. True once it answers.</summary>
    public static async Task<bool> EnsureRunningAsync(CancellationToken ct = default)
    {
        if (await VersionAsync(ct) is not null) return true;
        var exe = ExePath;
        if (exe is null) return false;
        // Prefer Ollama's own tray app (it runs the server the way Ollama expects); fall back to the bare server.
        var tray = Path.Combine(Path.GetDirectoryName(exe)!, "ollama app.exe");
        try
        {
            if (File.Exists(tray)) Process.Start(new ProcessStartInfo(tray) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
            else Process.Start(new ProcessStartInfo(exe, "serve") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch (Exception ex) { Log.Warn("ollama", $"Could not start Ollama: {ex.Message}"); return false; }
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(500, ct);
            if (await VersionAsync(ct) is not null) { Log.Info("ollama", "Started Ollama."); return true; }
        }
        return false;
    }

    /// <summary>Installs Ollama for this Windows user with winget (Windows' own package manager).</summary>
    public static async Task<(bool Ok, string Message)> InstallAsync(CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo("winget", "install --id Ollama.Ollama --exact --silent --scope user --accept-package-agreements --accept-source-agreements --disable-interactivity")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            _ = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            Log.Info("ollama", $"winget install finished with exit code {p.ExitCode}.");
            if (Installed) return (true, "Ollama is installed.");
            return (false, "Ollama didn't install automatically. Use \"Get it from ollama.com\" instead.");
        }
        catch (OperationCanceledException) { return (false, "Install cancelled."); }
        catch (Exception ex)
        {
            Log.Warn("ollama", $"winget unavailable: {ex.Message}");
            return (false, "Windows' package manager isn't available here. Use \"Get it from ollama.com\" instead.");
        }
    }

    public static async Task<List<string>> InstalledModelsAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await Http.GetStringAsync(BaseUrl + "/api/tags", ct);
            return JsonNode.Parse(json)?["models"]?.AsArray().Select(m => m?["name"]?.GetValue<string>() ?? "").Where(n => n.Length > 0).ToList() ?? [];
        }
        catch { return []; }
    }

    public static async Task<bool> HasModelAsync(string model, CancellationToken ct = default) =>
        (await InstalledModelsAsync(ct)).Any(n => n.Equals(model, StringComparison.OrdinalIgnoreCase) || n.Equals(model + ":latest", StringComparison.OrdinalIgnoreCase));

    /// <summary>Downloads a model, reporting (status, 0..1 or -1 when unknown).</summary>
    public static async Task<(bool Ok, string Message)> PullAsync(string model, IProgress<(string Status, double Fraction)>? progress, CancellationToken ct = default)
    {
        try
        {
            var body = new StringContent(JsonSerializer.Serialize(new { model, stream = true }), Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/pull") { Content = body };
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
            string? line, last = null;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (line.Length == 0) continue;
                var (status, fraction, error) = ParsePullLine(line);
                if (error is not null) return (false, "Download failed: " + error);
                last = status;
                progress?.Report((status, fraction));
            }
            return last == "success" ? (true, "AI model downloaded.") : (false, "The download stopped before it finished. Try again.");
        }
        catch (OperationCanceledException) { return (false, "Download cancelled."); }
        catch (Exception ex) { return (false, "Download failed: " + ex.Message); }
    }

    internal static (string Status, double Fraction, string? Error) ParsePullLine(string line)
    {
        var n = JsonNode.Parse(line);
        if (n?["error"]?.GetValue<string>() is { } err) return ("error", -1, err);
        var status = n?["status"]?.GetValue<string>() ?? "";
        double total = n?["total"]?.GetValue<double>() ?? 0, done = n?["completed"]?.GetValue<double>() ?? 0;
        return (status, total > 0 ? Math.Clamp(done / total, 0, 1) : -1, null);
    }
}

/// <summary>The assistant running on a local Ollama model. Same tools and rules as the Claude engine.</summary>
public sealed class LocalAssistant
{
    private const int MaxToolRounds = 6;
    private readonly List<JsonObject> _messages = [];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public string Model { get; }
    public int TurnCount { get; private set; }
    internal int MessageCount => _messages.Count;

    public LocalAssistant(string model) => Model = model;

    public void Reset() { _messages.Clear(); TurnCount = 0; }

    // Small local models follow a few firm rules better than a long brief.
    internal static string SystemPrompt => AssistantSession.SystemPrompt + """


        Extra rules:
        - Call get_pc_overview or list_tweaks before giving advice about this PC.
        - Use tweak ids exactly as list_tweaks returns them; never invent one.
        - Whenever you recommend a tweak or a profile, call suggest_actions in the same turn (for a profile, use open_page with target "optimization").
        - Reply in plain sentences and short "- " bullets, under 150 words.
        """;

    private static JsonArray ToolsJson() => new(AssistantSession.ToolSpecs.Select(s => (JsonNode)new JsonObject
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = s.Name,
            ["description"] = s.Description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = JsonNode.Parse(JsonSerializer.Serialize(s.Properties)),
                ["required"] = new JsonArray(s.Required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
            },
        },
    }).ToArray());

    public async Task<AssistantReply> SendAsync(string text, IProgress<string>? status = null, CancellationToken ct = default)
    {
        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });
        TurnCount++;
        var actions = new List<AssistantAction>();
        var answer = new StringBuilder();

        for (int round = 0; round <= MaxToolRounds; round++)
        {
            var request = new JsonObject
            {
                ["model"] = Model,
                ["stream"] = false,
                // Qwen3 still writes its reasoning into the answer when thinking is "off"; with it on,
                // Ollama returns the reasoning in a separate field that VYTRIX simply never shows.
                ["think"] = true,
                ["keep_alive"] = "10m",
                ["options"] = new JsonObject { ["num_ctx"] = 16384, ["temperature"] = 0.3 },
                ["tools"] = ToolsJson(),
                ["messages"] = new JsonArray(new[] { (JsonNode)new JsonObject { ["role"] = "system", ["content"] = SystemPrompt } }
                    .Concat(_messages.Select(m => m.DeepClone())).ToArray()),
            };
            using var resp = await Http.PostAsync(Ollama.BaseUrl + "/api/chat", new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new LocalAiException(ParseError(body) ?? $"The local AI returned an error ({(int)resp.StatusCode}).");

            var msg = JsonNode.Parse(body)?["message"] as JsonObject ?? throw new LocalAiException("The local AI sent an empty reply.");
            var content = CleanContent(msg["content"]?.GetValue<string>() ?? "");
            var calls = msg["tool_calls"] as JsonArray;

            var echo = new JsonObject { ["role"] = "assistant", ["content"] = content };
            if (calls is { Count: > 0 }) echo["tool_calls"] = calls.DeepClone();
            _messages.Add(echo);
            if (content.Length > 0) { if (answer.Length > 0) answer.Append("\n\n"); answer.Append(content); }

            if (calls is not { Count: > 0 })
            {
                // Local models tend to restate their earlier text in the final turn; keep just the final text when there is one.
                var final = content.Length > 0 ? content : answer.Length > 0 ? answer.ToString() : "I couldn't come up with an answer. Try rephrasing.";
                if (actions.Count == 0) actions.AddRange(InferActions(final));
                return new AssistantReply(final, actions);
            }

            foreach (var call in calls)
            {
                var name = call?["function"]?["name"]?.GetValue<string>() ?? "";
                status?.Report(AssistantSession.StatusFor(name));
                string result;
                try { result = AssistantSession.RunTool(name, ParseArgs(call?["function"]?["arguments"]), share: true, actions); }
                catch (Exception ex) { result = $"Tool failed: {ex.Message}"; }
                _messages.Add(new JsonObject { ["role"] = "tool", ["tool_name"] = name, ["content"] = result });
            }
        }
        return new AssistantReply((answer.ToString() + "\n\n(Stopped after looking up a lot of information. Ask again to continue.)").Trim(), actions);
    }

    /// <summary>Removes any reasoning a model prints inline, with or without the opening tag.</summary>
    internal static string CleanContent(string s)
    {
        int close = s.LastIndexOf("</think>", StringComparison.Ordinal);
        if (close >= 0) s = s[(close + "</think>".Length)..];
        return Regex.Replace(s, @"<think>.*?(</think>|$)", "", RegexOptions.Singleline).Trim();
    }

    /// <summary>
    /// Small local models often recommend something in words without calling suggest_actions.
    /// Offer buttons for tweaks and profiles the answer names, after the usual validation.
    /// </summary>
    internal static List<AssistantAction> InferActions(string answer)
    {
        var found = new List<AssistantAction>();
        foreach (var t in TweakCatalog.All)
        {
            if (found.Count >= 3) break;
            if (answer.Contains(t.Name, StringComparison.OrdinalIgnoreCase) || answer.Contains(t.Id, StringComparison.OrdinalIgnoreCase))
                if (AssistantSession.Validate("apply_tweak", t.Id, "Mentioned in the answer.", out _) is { } a) found.Add(a);
        }
        if (Profiles.All.Any(p => Regex.IsMatch(answer, $@"\b{Regex.Escape(p.Name)}\b\W{{0,4}}profile", RegexOptions.IgnoreCase)))
            if (AssistantSession.Validate("open_page", "optimization", "Review and apply a profile.", out _) is { } o) found.Add(o with { Label = "Open profiles" });
        return found;
    }

    /// <summary>Ollama sends arguments as an object; some models send a JSON string instead.</summary>
    internal static IReadOnlyDictionary<string, JsonElement> ParseArgs(JsonNode? args)
    {
        try
        {
            var json = args switch
            {
                null => "{}",
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                _ => args.ToJsonString(),
            };
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                : new Dictionary<string, JsonElement>();
        }
        catch { return new Dictionary<string, JsonElement>(); }
    }

    private static string? ParseError(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?.GetValue<string>() is { } e ? "The local AI reported: " + e : null; } catch { return null; }
    }

    public static string Describe(Exception ex) => ex switch
    {
        LocalAiException l => l.Message,
        HttpRequestException => "Couldn't reach the local AI. Make sure Ollama is running, then try again.",
        TaskCanceledException t when !t.CancellationToken.IsCancellationRequested => "The local AI took too long to answer. A smaller model may run better on this PC.",
        OperationCanceledException => "Stopped.",
        _ => "Something went wrong with the local AI. Try again.",
    };
}

public sealed class LocalAiException(string message) : Exception(message);
