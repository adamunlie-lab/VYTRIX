using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Vytrix.Services;
using Xunit;

namespace Vytrix.Tests;

/// <summary>
/// Drives the assistant against a local fake of the Messages API, so the whole tool loop,
/// request shape and error handling are tested without a real key or any network traffic.
/// </summary>
public class AssistantTests
{
    internal sealed class FakeClaude : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Queue<(int Status, string Body)> _replies = new();
        public List<(Dictionary<string, string> Headers, JsonDocument Body)> Requests { get; } = [];
        public List<string> Paths { get; } = [];
        public string Url { get; }

        public FakeClaude(params (int, string)[] replies)
        {
            foreach (var r in replies) _replies.Enqueue(r);
            var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _ = Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                using var sr = new StreamReader(ctx.Request.InputStream);
                var body = await sr.ReadToEndAsync();
                var headers = ctx.Request.Headers.AllKeys.Where(k => k is not null).ToDictionary(k => k!.ToLowerInvariant(), k => ctx.Request.Headers[k]!);
                lock (Requests) { Requests.Add((headers, JsonDocument.Parse(body))); Paths.Add(ctx.Request.Url!.AbsolutePath); }
                var (status, reply) = _replies.Count > 0 ? _replies.Dequeue() : (500, """{"type":"error","error":{"type":"api_error","message":"no more replies"}}""");
                var bytes = Encoding.UTF8.GetBytes(reply);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                ctx.Response.Headers["request-id"] = "req_test";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        }

        public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch { } }
    }

    private static string Msg(string stopReason, params object[] content) => JsonSerializer.Serialize(new
    {
        id = "msg_test", type = "message", role = "assistant", model = AssistantSession.Model,
        content, stop_reason = stopReason, stop_sequence = (string?)null, stop_details = (object?)null,
        usage = new { input_tokens = 12, output_tokens = 7, cache_creation_input_tokens = 0, cache_read_input_tokens = 0 },
    });

    private static object Text(string t) => new { type = "text", text = t, citations = (object?)null };
    private static object Think(string sig) => new { type = "thinking", thinking = "", signature = sig };
    private static object Use(string id, string name, object input) => new { type = "tool_use", id, name, input };

    [Fact]
    public async Task Tool_loop_runs_tools_echoes_thinking_and_returns_validated_buttons()
    {
        using var fake = new FakeClaude(
            (200, Msg("tool_use", Think("sig-1"), Text("Let me look."), Use("toolu_1", "list_tweaks", new { }))),
            (200, Msg("tool_use", Use("toolu_2", "suggest_actions", new
            {
                actions = new object[]
                {
                    new { kind = "open_page", target = "restore", reason = "See what changed." },
                    new { kind = "open_page", target = "nowhere", reason = "Bad page." },
                    new { kind = "apply_tweak", target = "not.a.tweak", reason = "Bad tweak." },
                },
            }))),
            (200, Msg("end_turn", Text("Here's what I found."))));

        var s = new AssistantSession("sk-ant-test-key-0000000000000000000000", fake.Url);
        var reply = await s.SendAsync("Why is my PC slow?", sharePcDetails: true);

        Assert.Contains("Let me look.", reply.Text);
        Assert.Contains("Here's what I found.", reply.Text);
        var action = Assert.Single(reply.Actions);
        Assert.Equal(("open_page", "restore", "Open Restore Center"), (action.Kind, action.Target, action.Label));
        Assert.Equal(3, fake.Requests.Count);

        // First request: model, key header, fallbacks, effort, tools, caching.
        var (h0, b0) = fake.Requests[0];
        // This SDK release sends the API key as a bearer token.
        Assert.Equal("Bearer sk-ant-test-key-0000000000000000000000", h0["authorization"]);
        Assert.Contains("server-side-fallback-2026-07-01", h0["anthropic-beta"]);
        var r0 = b0.RootElement;
        Assert.Equal("claude-opus-5-5", r0.GetProperty("model").GetString());
        Assert.Equal("default", r0.GetProperty("fallbacks").GetString());
        Assert.Equal("medium", r0.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal(7, r0.GetProperty("tools").GetArrayLength());
        Assert.Equal("ephemeral", r0.GetProperty("cache_control").GetProperty("type").GetString());
        Assert.False(r0.TryGetProperty("thinking", out _));   // Opus 5.5: thinking is always on; never sent disabled

        // Second request: thinking block echoed unchanged, then the tool result for toolu_1.
        var msgs = fake.Requests[1].Body.RootElement.GetProperty("messages");
        Assert.Equal(3, msgs.GetArrayLength());
        var echoed = msgs[1].GetProperty("content");
        Assert.Equal("thinking", echoed[0].GetProperty("type").GetString());
        Assert.Equal("sig-1", echoed[0].GetProperty("signature").GetString());
        Assert.Equal("tool_use", echoed[2].GetProperty("type").GetString());
        var result = msgs[2].GetProperty("content")[0];
        Assert.Equal("tool_result", result.GetProperty("type").GetString());
        Assert.Equal("toolu_1", result.GetProperty("tool_use_id").GetString());
        Assert.Contains("gaming.gamemode", result.GetProperty("content").ToString());

        // Third request: the suggest_actions result reports the rejected suggestions back to Claude.
        var last = fake.Requests[2].Body.RootElement.GetProperty("messages");
        var suggestResult = last[last.GetArrayLength() - 1].GetProperty("content")[0].GetProperty("content").ToString();
        Assert.Contains("Showed 1 button", suggestResult);
        Assert.Contains("'nowhere' is not a page id", suggestResult);
        Assert.Contains("'not.a.tweak' is not a tweak id", suggestResult);

        // The conversation keeps its history for the next question.
        Assert.Equal(6, s.MessageCount);
    }

    [Fact]
    public async Task Refusal_gives_a_friendly_reply_and_keeps_the_chat_usable()
    {
        using var fake = new FakeClaude((200, Msg("refusal")));
        var s = new AssistantSession("sk-ant-test-key-0000000000000000000000", fake.Url);
        var reply = await s.SendAsync("something off-topic", sharePcDetails: true);
        Assert.Contains("can't help", reply.Text);
        Assert.Empty(reply.Actions);
        Assert.Equal(0, s.MessageCount);
    }

    [Fact]
    public async Task Rejected_key_is_explained_without_echoing_the_key()
    {
        using var fake = new FakeClaude((401, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}"""));
        var s = new AssistantSession("sk-ant-test-key-SECRET0000000000000000", fake.Url);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => s.SendAsync("hi", sharePcDetails: true));
        var text = AssistantSession.Describe(ex);
        Assert.Contains("API key was rejected", text);
        Assert.DoesNotContain("SECRET", text);
    }

    [Fact]
    public void Without_sharing_no_pc_details_reach_the_model()
    {
        var actions = new List<AssistantAction>();
        var empty = new Dictionary<string, JsonElement>();
        foreach (var tool in new[] { "get_pc_overview", "get_top_apps", "get_recent_changes" })
            Assert.Contains("turned off sharing", AssistantSession.RunTool(tool, empty, share: false, actions));
        var list = AssistantSession.RunTool("list_tweaks", empty, share: false, actions);
        Assert.DoesNotContain("already on", list);
        Assert.DoesNotContain(Environment.MachineName, AssistantSession.RunTool("get_pc_overview", empty, share: true, actions));
        Assert.DoesNotContain(Environment.UserName, AssistantSession.RunTool("get_top_apps", empty, share: true, actions), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Suggestions_never_offer_impossible_actions()
    {
        foreach (var t in TweakCatalog.All)
        {
            var state = t.GetState();
            var on = AssistantSession.Validate("apply_tweak", t.Id, "r", out _);
            var off = AssistantSession.Validate("turn_off_tweak", t.Id, "r", out _);
            if (state == TweakState.Unsupported) { Assert.Null(on); Assert.Null(off); }
            if (state == TweakState.Applied) Assert.Null(on);
            if (state == TweakState.NotApplied) Assert.Null(off);
        }
        Assert.Null(AssistantSession.Validate("delete_files", "C:\\", "r", out var p));
        Assert.Contains("Unknown kind", p);
    }

    [Fact]
    public void Key_format_check_catches_paste_mistakes()
    {
        Assert.True(AssistantKey.LooksValid("sk-ant-api03-" + new string('a', 40)));
        Assert.False(AssistantKey.LooksValid(""));
        Assert.False(AssistantKey.LooksValid("hello"));
        Assert.False(AssistantKey.LooksValid("sk-ant-api03 " + new string('a', 40)));
    }

    [Fact]
    public void Key_is_stored_encrypted()
    {
        var key = "sk-ant-api03-" + new string('z', 40);
        try
        {
            AssistantKey.Save(key);
            var raw = File.ReadAllBytes(Path.Combine(Vytrix.Core.AppPaths.Root, "assistant-key.bin"));
            Assert.DoesNotContain("zzzzzzzz", Encoding.UTF8.GetString(raw));
            Assert.Equal(key, AssistantKey.Load());
        }
        finally { AssistantKey.Delete(); }
        Assert.False(AssistantKey.Exists);
    }

    [Fact]
    public void System_prompt_and_tools_are_stable_for_caching() =>
        Assert.DoesNotContain(DateTime.Now.Year.ToString(), AssistantSession.SystemPrompt);
}
