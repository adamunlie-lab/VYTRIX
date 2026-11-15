using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Vytrix.Core;
using Vytrix.Native;

namespace Vytrix.Services;

/// <summary>A button the assistant offers. Nothing runs until the user clicks it.</summary>
public sealed record AssistantAction(string Kind, string Target, string Label, string Reason);

/// <summary>One finished assistant reply: its text and any actions it suggested.</summary>
public sealed record AssistantReply(string Text, IReadOnlyList<AssistantAction> Actions);

/// <summary>An API key stored in a file, encrypted with Windows DPAPI for this Windows account only.</summary>
public sealed class StoredKey(string fileName, string label)
{
    private string FilePath => Path.Combine(AppPaths.Root, fileName);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VYTRIX assistant key v1");

    public bool Exists => File.Exists(FilePath);

    public string? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) { Log.Warn("assistant", $"Stored {label} key could not be read: {ex.GetType().Name}"); return null; }
    }

    public void Save(string key)
    {
        File.WriteAllBytes(FilePath, ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser));
        Log.Info("assistant", $"{label} key saved (encrypted for this Windows account).");
    }

    public void Delete()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
        Log.Info("assistant", $"{label} key removed.");
    }
}

/// <summary>The user's Anthropic API key.</summary>
public static class AssistantKey
{
    private static readonly StoredKey Store = new("assistant-key.bin", "Anthropic API");
    public static bool Exists => Store.Exists;
    public static string? Load() => Store.Load();
    public static void Save(string key) => Store.Save(key);
    public static void Delete() => Store.Delete();

    /// <summary>Anthropic API keys start with sk-ant-. This only catches obvious paste mistakes.</summary>
    public static bool LooksValid(string? key) => key is not null && key.Trim().StartsWith("sk-ant-", StringComparison.Ordinal) && key.Trim().Length > 30 && !key.Trim().Contains(' ');
}

/// <summary>The user's free Google AI Studio (Gemini) API key.</summary>
public static class GeminiKey
{
    private static readonly StoredKey Store = new("gemini-key.bin", "Gemini API");
    public static bool Exists => Store.Exists;
    public static string? Load() => Store.Load();
    public static void Save(string key) => Store.Save(key);
    public static void Delete() => Store.Delete();

    /// <summary>Google API keys are about 39 characters with no spaces. This only catches obvious paste mistakes.</summary>
    public static bool LooksValid(string? key) => key is not null && key.Trim().Length is >= 30 and <= 200 && !key.Trim().Any(char.IsWhiteSpace) && !key.Trim().StartsWith("sk-ant-", StringComparison.Ordinal);
}

/// <summary>
/// The VYTRIX assistant: Claude with read-only tools over VYTRIX's own data, plus one tool that
/// proposes actions as buttons. The assistant can never change a setting itself.
/// </summary>
public sealed class AssistantSession
{
    public const string Model = "claude-opus-5-5";
    private const int MaxToolRounds = 8;

    /// <summary>Pages the assistant may link to (keep in sync with the sidebar).</summary>
    public static readonly IReadOnlyDictionary<string, string> Pages = new Dictionary<string, string>
    {
        ["overview"] = "Overview", ["optimization"] = "Optimization", ["gaming"] = "Gaming", ["network"] = "Network",
        ["memory"] = "Memory", ["storage"] = "Storage", ["tweaks"] = "Tweaks", ["startup"] = "Startup",
        ["restore"] = "Restore Center", ["hardware"] = "Hardware", ["apps"] = "Installed Apps", ["services"] = "Services",
        ["benchmark"] = "Benchmark", ["settings"] = "Settings", ["account"] = "Account", ["help"] = "Help",
    };

    private readonly List<BetaMessageParam> _messages = [];
    private readonly AnthropicClient _client;

    public AssistantSession(string apiKey) : this(apiKey, null) { }

    /// <summary>Tests point the session at a local fake server through <paramref name="baseUrl"/>.</summary>
    internal AssistantSession(string apiKey, string? baseUrl)
    {
        _client = baseUrl is null
            ? new AnthropicClient { ApiKey = apiKey }
            : new AnthropicClient { ApiKey = apiKey, BaseUrl = baseUrl, MaxRetries = 0 };
    }

    /// <summary>The conversation so far, for tests.</summary>
    internal int MessageCount => _messages.Count;

    public int TurnCount { get; private set; }

    public void Reset() { _messages.Clear(); TurnCount = 0; }

    internal static string SystemPrompt =>
        """
        You are the assistant built into VYTRIX, a Windows 10/11 optimization app. You help one person get a faster, smoother, quieter PC, and you explain things in plain, friendly language for someone who isn't technical.

        How VYTRIX works:
        - Every change is a documented Windows setting, recorded first and undoable from the Restore Center.
        - VYTRIX never touches Defender, Firewall, Windows Update or core services, and has no placebo tweaks (no RAM cleaners, no "ping boosters").
        - Some tweaks need administrator mode ("Relaunch as administrator" at the bottom of the sidebar).
        - A tweak shown as "Already on" was already set in Windows before VYTRIX.

        Use your tools to look at this PC before giving PC-specific advice; never guess readings, and if a reading is unavailable say so. Only recommend tweaks that exist in the tweak list, by their exact id. When you recommend something the person can act on, call suggest_actions so they get buttons; you cannot change anything yourself, and nothing happens until they click. Don't suggest turning on a tweak that is already on, and mention anything that needs administrator mode or a restart.

        Tweak ids are for tool calls only: in your reply, call each tweak by its name (for example "Game Mode"), never its id.

        Be honest about trade-offs and about what won't help. Keep answers short: a sentence or two of answer first, then at most a few bullets. No headings, no tables, and no links to websites.
        """;

    /// <summary>One tool, described once and shared by the Claude and local engines.</summary>
    public sealed record ToolSpec(string Name, string Description, object Properties, string[] Required)
    {
        public Dictionary<string, JsonElement> PropertyMap =>
            JsonSerializer.SerializeToElement(Properties).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private static ToolSpec Tool(string name, string description, object properties, string[] required) => new(name, description, properties, required);

    public static readonly ToolSpec[] ToolSpecs =
    [
        Tool("get_pc_overview", "Live readings and hardware for this PC: CPU, GPU, RAM, disks, temperatures (when available), Windows version, power plan, admin mode, startup app and process counts.", new { }, []),
        Tool("list_tweaks", "Every VYTRIX tweak with its id, name, component, current state on this PC, risk, and whether it needs admin.", new { }, []),
        Tool("get_tweak", "Full details for one tweak: what it changes, its effect, risk, compatibility, when it takes effect, and its state.",
            new { id = new { type = "string", description = "Tweak id from list_tweaks" } }, ["id"]),
        Tool("list_profiles", "The one-click optimization profiles and which tweaks each one applies.", new { }, []),
        Tool("get_top_apps", "The apps and background processes using the most memory right now (names and memory only).", new { }, []),
        Tool("get_recent_changes", "Changes VYTRIX has made on this PC that can still be undone, newest first.", new { }, []),
        Tool("suggest_actions",
            "Show the person buttons for things you recommend. kind is apply_tweak (turn a tweak on), turn_off_tweak, or open_page. target is the tweak id or page id. Nothing changes until they click, and tweaks still ask for confirmation.",
            new
            {
                actions = new
                {
                    type = "array", minItems = 1, maxItems = 5,
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            kind = new { type = "string", @enum = new[] { "apply_tweak", "turn_off_tweak", "open_page" } },
                            target = new { type = "string", description = "Tweak id, or one of: " + string.Join(", ", Pages.Keys) },
                            reason = new { type = "string", description = "One short sentence on why" },
                        },
                        required = new[] { "kind", "target", "reason" },
                    },
                },
            }, ["actions"]),
    ];

    private static readonly BetaToolUnion[] Tools = ToolSpecs.Select(s => (BetaToolUnion)new BetaTool
    {
        Name = s.Name,
        Description = s.Description,
        InputSchema = new() { Properties = s.PropertyMap, Required = s.Required },
    }).ToArray();

    /// <summary>Sends one message and runs the tool loop until Claude answers.</summary>
    public async Task<AssistantReply> SendAsync(string text, bool sharePcDetails, IProgress<string>? status = null, CancellationToken ct = default)
    {
        _messages.Add(new BetaUserMessageParam { Content = text });
        TurnCount++;
        var actions = new List<AssistantAction>();
        var answer = new StringBuilder();

        for (int round = 0; round <= MaxToolRounds; round++)
        {
            var response = await _client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = Model,
                MaxTokens = 16000,
                Betas = ["server-side-fallback-2026-07-01"],
                Fallbacks = new Default(),
                OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
                System = SystemPrompt,
                Tools = Tools,
                CacheControl = new BetaCacheControlEphemeral(),
                Messages = [.. _messages],
            }, ct);

            if (response.StopReason == "refusal")
            {
                _messages.RemoveAt(_messages.Count - 1);   // drop the refused question so the chat can continue
                TurnCount--;
                return new AssistantReply("I can't help with that one. Try asking about your PC's speed, games, startup, network or a specific tweak.", []);
            }

            var echo = new List<BetaContentBlockParam>();
            var results = new List<BetaContentBlockParam>();
            foreach (var block in response.Content)
            {
                if (block.TryPickText(out var t)) { echo.Add(new BetaTextBlockParam { Text = t.Text }); answer.Append(t.Text); }
                else if (block.TryPickThinking(out var th)) echo.Add(new BetaThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
                else if (block.TryPickRedactedThinking(out var rt)) echo.Add(new BetaRedactedThinkingBlockParam { Data = rt.Data });
                else if (block.TryPickToolUse(out var tu))
                {
                    echo.Add(new BetaToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                    status?.Report(StatusFor(tu.Name));
                    string result; bool isError = false;
                    try { result = RunTool(tu.Name, tu.Input, sharePcDetails, actions); }
                    catch (Exception ex) { result = $"Tool failed: {ex.Message}"; isError = true; }
                    results.Add(new BetaToolResultBlockParam { ToolUseID = tu.ID, Content = result, IsError = isError });
                }
            }
            _messages.Add(new BetaAssistantMessageParam { Content = echo });

            if (results.Count == 0 || response.StopReason != "tool_use")
            {
                var reply = answer.ToString().Trim();
                if (response.StopReason == "max_tokens") reply += "\n\n(Reply cut short.)";
                return new AssistantReply(reply.Length > 0 ? reply : "Done.", actions);
            }
            _messages.Add(new BetaUserMessageParam { Content = results });
            if (answer.Length > 0) answer.Append("\n\n");
        }
        return new AssistantReply(answer.ToString().Trim() + "\n\n(Stopped after looking up a lot of information. Ask again to continue.)", actions);
    }

    /// <summary>Plain-language message for an API failure; never includes the key.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        AnthropicUnauthorizedException => "Your API key was rejected. Check it in the assistant's settings, or create a new one in the Anthropic Console.",
        AnthropicForbiddenException => "Your Anthropic account doesn't have access to this model. Check your plan in the Anthropic Console.",
        AnthropicRateLimitException => "Too many requests right now. Wait a moment and try again.",
        AnthropicOverloadedException or Anthropic5xxException or AnthropicServiceUnavailableException => "Claude is busy right now. Try again in a minute.",
        AnthropicBadRequestException b when b.Message.Contains("credit", StringComparison.OrdinalIgnoreCase) => "Your Anthropic account is out of credit. Add credit in the Anthropic Console.",
        AnthropicIOException or HttpRequestException or TaskCanceledException => "Couldn't reach Claude. Check your internet connection.",
        OperationCanceledException => "Stopped.",
        _ => "Something went wrong talking to Claude. Try again.",
    };

    internal static string StatusFor(string tool) => tool switch
    {
        "get_pc_overview" => "Checking your PC…",
        "list_tweaks" or "get_tweak" => "Looking at your tweaks…",
        "list_profiles" => "Comparing profiles…",
        "get_top_apps" => "Checking what's running…",
        "get_recent_changes" => "Reading your change history…",
        "suggest_actions" => "Preparing suggestions…",
        _ => "Working…",
    };

    // ───────────────────────── Tools (read-only, plus suggest_actions)

    private const string NotShared = "The person has turned off sharing PC details with the assistant. Answer generally, and tell them they can turn sharing back on at the top of the Assistant page for advice about their own PC.";

    internal static string RunTool(string name, IReadOnlyDictionary<string, JsonElement> input, bool share, List<AssistantAction> actions) => name switch
    {
        "get_pc_overview" => share ? PcOverview() : NotShared,
        "list_tweaks" => share ? TweakList(withState: true) : TweakList(withState: false),
        "get_tweak" => TweakDetails(Str(input, "id"), share),
        "list_profiles" => Json(Profiles.All.Select(p => new { id = p.Id, name = p.Name, tagline = p.Tagline, description = p.Description, tweaks = p.TweakIds, powerPlan = p.PowerPlan })),
        "get_top_apps" => share ? TopApps() : NotShared,
        "get_recent_changes" => share ? RecentChanges() : NotShared,
        "suggest_actions" => Suggest(input, actions),
        _ => $"Unknown tool {name}.",
    };

    private static string Str(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Json(object o) => JsonSerializer.Serialize(o);

    private static object? Reading(double v, Func<double, object> fmt) => v < 0 ? "unavailable" : fmt(v);

    private static string PcOverview()
    {
        var t = Telemetry.Instance;
        var s = t.Last;
        var m = t.Memory;
        var gpu = SystemInfo.PrimaryGpu;
        return Json(new
        {
            windows = $"{SystemInfo.OsName} {SystemInfo.OsVersion} (build {SystemInfo.OsBuild})",
            pcType = SystemInfo.IsLaptop ? "laptop" : "desktop",
            cpu = new { model = SystemInfo.Cpu.Brand?.Trim(), cores = SystemInfo.Cpu.PhysicalCores, threads = SystemInfo.Cpu.LogicalProcessors, usagePercent = t.HasSample ? Reading(s.CpuPercent, v => Math.Round(v)) : "measuring", temperatureC = t.HasSample ? Reading(s.AcpiTempC, v => Math.Round(v)) : "measuring" },
            gpu = gpu is { } g ? new { model = g.Name?.Trim(), vramGB = Math.Round(g.DedicatedVideoMemory / 1073741824.0, 1), usagePercent = t.HasSample ? Reading(s.GpuPercent, v => Math.Round(v)) : "measuring", temperatureC = t.HasSample ? Reading(s.GpuTempC, v => Math.Round(v)) : "measuring", hardwareSchedulingSupported = g.HagsSupported == 1, hardwareSchedulingOn = g.HagsEnabled == 1 } : null,
            memory = m.TotalPhys > 0 ? new { totalGB = Math.Round(m.TotalPhys / 1073741824.0, 1), inUsePercent = m.LoadPercent, modules = SystemInfo.MemoryModules.Select(x => new { sizeGB = x.Capacity / 1073741824, speedMHz = x.SpeedMhz }) } : null,
            disks = SystemInfo.Disks.Select(d => new { model = d.Model, type = d.MediaType, bus = d.BusType, health = d.Health, sizeGB = d.Size / 1000000000 }),
            drives = SystemInfo.Volumes().Select(v => new { drive = v.Name, totalGB = v.Total / 1000000000, freeGB = v.Free / 1000000000 }),
            powerPlan = SafeOr(() => PowerService.Active?.Name, "unknown"),
            administratorMode = Elevation.IsAdmin,
            startupAppsEnabled = SafeOr(() => StartupService.EnabledCount(), -1),
            processes = new { total = t.TotalProcesses, background = t.BackgroundProcesses },
            uptimeHours = Math.Round(Environment.TickCount64 / 3600000.0, 1),
        });
    }

    private static T SafeOr<T>(Func<T> f, T fallback) { try { return f(); } catch { return fallback; } }

    private static string StateText(Tweak t)
    {
        var s = SafeOr(t.GetState, TweakState.NotApplied);
        return s switch
        {
            TweakState.Applied => t.AppliedByVytrix ? "on (applied by VYTRIX)" : "already on in Windows",
            TweakState.Partial => "partially applied",
            TweakState.Unsupported => "not supported on this PC: " + t.UnsupportedReason,
            _ => "off",
        };
    }

    private static string TweakList(bool withState) => Json(TweakCatalog.All.Select(t => new
    {
        id = t.Id, name = t.Name, component = t.ComponentLabel, summary = t.Description,
        state = withState ? StateText(t) : "unknown (PC details not shared)",
        risk = t.RiskLabel, needsAdmin = t.RequiresAdmin,
    }));

    private static string TweakDetails(string id, bool share)
    {
        var t = TweakCatalog.ById(id);
        if (t is null) return $"No tweak with id '{id}'. Call list_tweaks for valid ids.";
        return Json(new
        {
            id = t.Id, name = t.Name, component = t.ComponentLabel, summary = t.Description, effect = t.ExpectedEffect,
            explanation = t.Explanation, risk = t.RiskLabel, compatibility = t.Compatibility,
            takesEffect = t.AppliesAfter ?? "Immediately", needsAdmin = t.RequiresAdmin,
            state = share ? StateText(t) : "unknown (PC details not shared)",
        });
    }

    private static string TopApps() => Json(ProcessService.Snapshot()
        .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
        .Select(g => new { name = g.Key, instances = g.Count(), memoryMB = g.Sum(p => (long)p.WorkingSet) / 1048576, kind = g.First().ClassLabel })
        .OrderByDescending(x => x.memoryMB).Take(15));

    private static string RecentChanges() => Json(Journal.All.Where(r => !r.RolledBack).Take(20)
        .Select(r => new { when = r.Time.ToString("yyyy-MM-dd HH:mm"), what = r.Title, kind = r.Kind.ToString(), undoable = r.Reversible }));

    private static string Suggest(IReadOnlyDictionary<string, JsonElement> input, List<AssistantAction> actions)
    {
        if (!input.TryGetValue("actions", out var arr) || arr.ValueKind != JsonValueKind.Array) return "Provide an actions array.";
        var problems = new List<string>();
        int added = 0;
        foreach (var a in arr.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Object) continue;
            string S(string k) => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var (kind, target, reason) = (S("kind"), S("target").Trim(), S("reason"));
            var action = Validate(kind, target, reason, out var problem);
            if (action is null) { problems.Add(problem); continue; }
            if (actions.Any(x => x.Kind == action.Kind && x.Target == action.Target)) continue;
            if (actions.Count >= 6) { problems.Add("Too many suggestions; extra ones were dropped."); break; }
            actions.Add(action);
            added++;
        }
        var msg = added > 0 ? $"Showed {added} button(s) to the person. Nothing has changed; they decide whether to click." : "No buttons were shown.";
        return problems.Count > 0 ? msg + " Problems: " + string.Join(" ", problems) : msg;
    }

    internal static AssistantAction? Validate(string kind, string target, string reason, out string problem)
    {
        problem = "";
        switch (kind)
        {
            case "open_page":
                if (Pages.TryGetValue(target, out var page)) return new AssistantAction(kind, target, $"Open {page}", reason);
                problem = $"'{target}' is not a page id.";
                return null;
            case "apply_tweak":
            case "turn_off_tweak":
                var t = TweakCatalog.ById(target);
                if (t is null) { problem = $"'{target}' is not a tweak id."; return null; }
                var state = SafeOr(t.GetState, TweakState.NotApplied);
                if (state == TweakState.Unsupported) { problem = $"{t.Name} isn't supported on this PC."; return null; }
                if (kind == "apply_tweak" && state == TweakState.Applied) { problem = $"{t.Name} is already on."; return null; }
                if (kind == "turn_off_tweak" && state == TweakState.NotApplied) { problem = $"{t.Name} is already off."; return null; }
                return new AssistantAction(kind, target, kind == "apply_tweak" ? $"Turn on: {t.Name}" : $"Turn off: {t.Name}", reason);
            default:
                problem = $"Unknown kind '{kind}'.";
                return null;
        }
    }
}
