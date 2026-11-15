using System.Text.Json;
using Vytrix.Services;
using Xunit;

namespace Vytrix.Tests;

/// <summary>The Gemini engine, driven against a fake of Google's generateContent API.</summary>
public class GeminiTests
{
    private static string Reply(string finish, params object[] parts) => JsonSerializer.Serialize(new
    {
        candidates = new[] { new { content = new { role = "model", parts }, finishReason = finish, index = 0 } },
        usageMetadata = new { promptTokenCount = 10, candidatesTokenCount = 5 },
    });

    private const string Key = "AIzaFAKEKEYFORTESTSONLY0000000000000000";

    [Fact]
    public async Task Tool_loop_echoes_thought_signatures_and_returns_validated_buttons()
    {
        using var fake = new AssistantTests.FakeClaude(
            (200, Reply("STOP",
                new { text = "private reasoning", thought = true },
                new { text = "Let me check." },
                new { functionCall = new { name = "list_tweaks", args = new { } }, thoughtSignature = "sigA" },
                new { functionCall = new { name = "suggest_actions", args = new { actions = new object[] { new { kind = "open_page", target = "help", reason = "Learn more." } } } } })),
            (200, Reply("STOP", new { text = "Here's what I found." })));

        var g = new GeminiAssistant(Key, baseUrl: fake.Url);
        var reply = await g.SendAsync("Why is my PC slow?", sharePcDetails: true);

        Assert.Equal("Here's what I found.", reply.Text);
        Assert.DoesNotContain("private reasoning", reply.Text);
        var a = Assert.Single(reply.Actions);
        Assert.Equal(("open_page", "help"), (a.Kind, a.Target));

        var (h0, b0) = fake.Requests[0];
        Assert.Equal(Key, h0["x-goog-api-key"]);
        var r0 = b0.RootElement;
        Assert.Equal("low", r0.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString());
        Assert.Contains("VYTRIX", r0.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        var decls = r0.GetProperty("tools")[0].GetProperty("functionDeclarations");
        Assert.Equal(AssistantSession.ToolSpecs.Length, decls.GetArrayLength());
        foreach (var d in decls.EnumerateArray())
            if (d.GetProperty("name").GetString() is "list_tweaks" or "get_pc_overview") Assert.False(d.TryGetProperty("parameters", out _));   // Gemini rejects empty objects
        Assert.Equal("object", decls.EnumerateArray().First(d => d.GetProperty("name").GetString() == "get_tweak").GetProperty("parameters").GetProperty("type").GetString());

        // Second request: the model turn is sent back exactly (signature kept), then both results in one user turn, in order.
        var c1 = fake.Requests[1].Body.RootElement.GetProperty("contents");
        Assert.Equal(3, c1.GetArrayLength());
        Assert.Equal("model", c1[1].GetProperty("role").GetString());
        Assert.Equal("sigA", c1[1].GetProperty("parts")[2].GetProperty("thoughtSignature").GetString());
        Assert.Equal("user", c1[2].GetProperty("role").GetString());
        var frs = c1[2].GetProperty("parts");
        Assert.Equal(2, frs.GetArrayLength());
        Assert.Equal("list_tweaks", frs[0].GetProperty("functionResponse").GetProperty("name").GetString());
        Assert.Contains("gaming.gamemode", frs[0].GetProperty("functionResponse").GetProperty("response").GetProperty("result").GetString());
        Assert.Equal("suggest_actions", frs[1].GetProperty("functionResponse").GetProperty("name").GetString());
        Assert.Equal(4, g.MessageCount);
    }

    [Fact]
    public async Task Free_limit_is_explained()
    {
        using var fake = new AssistantTests.FakeClaude((429, """{"error":{"code":429,"message":"Resource has been exhausted (e.g. check quota).","status":"RESOURCE_EXHAUSTED"}}"""));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => new GeminiAssistant(Key, "gemini-3.6-flash", fake.Url).SendAsync("hi", true));
        Assert.Contains("free limit", GeminiAssistant.Describe(ex));
    }

    [Fact]
    public async Task Bad_key_is_explained_without_echoing_it()
    {
        using var fake = new AssistantTests.FakeClaude((400, """{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT"}}"""));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => new GeminiAssistant(Key, "gemini-3.6-flash", fake.Url).SendAsync("hi", true));
        var text = GeminiAssistant.Describe(ex);
        Assert.Contains("key was rejected", text);
        Assert.DoesNotContain(Key, text);
    }

    [Fact]
    public async Task Blocked_prompt_gives_a_friendly_reply_and_keeps_the_chat_usable()
    {
        using var fake = new AssistantTests.FakeClaude((200, """{"promptFeedback":{"blockReason":"SAFETY"}}"""));
        var g = new GeminiAssistant(Key, baseUrl: fake.Url);
        var reply = await g.SendAsync("something off-topic", true);
        Assert.Contains("declined", reply.Text);
        Assert.Equal(0, g.MessageCount);
        Assert.Equal(0, g.TurnCount);
    }

    [Fact]
    public async Task A_hung_request_times_out_with_a_clear_message()
    {
        // A listener that accepts the connection but never answers.
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        var old = GeminiAssistant.RequestTimeout;
        GeminiAssistant.RequestTimeout = TimeSpan.FromSeconds(2);
        try
        {
            var g = new GeminiAssistant(Key, baseUrl: $"http://127.0.0.1:{port}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<GeminiException>(() => g.SendAsync("hi", true));
            // Every free model is tried once, each with the short timeout.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15));
            Assert.Contains("all busy", GeminiAssistant.Describe(ex));
        }
        finally { GeminiAssistant.RequestTimeout = old; l.Stop(); }
    }

    [Fact]
    public async Task An_overloaded_model_falls_back_to_the_next_free_model_and_restarts_the_turn()
    {
        using var fake = new AssistantTests.FakeClaude(
            // First model: starts a tool call, then is overloaded on the follow-up.
            (200, Reply("STOP", new { functionCall = new { name = "list_profiles", args = new { } }, thoughtSignature = "sig-from-A" })),
            (503, """{"error":{"code":503,"message":"This model is currently experiencing high demand.","status":"UNAVAILABLE"}}"""),
            // Second model answers the whole question from the start.
            (200, Reply("STOP", new { text = "All good." })));
        var g = new GeminiAssistant(Key, ["model-a", "model-b"], fake.Url);
        var reply = await g.SendAsync("Is my PC OK?", true);

        Assert.Equal("All good.", reply.Text);
        Assert.Equal("model-b", g.Model);   // stays on the model that worked
        Assert.Equal(["/v1beta/models/model-a:generateContent", "/v1beta/models/model-a:generateContent", "/v1beta/models/model-b:generateContent"], fake.Paths);
        // The retry starts the question over, without model A's partial tool exchange or its signature.
        var c = fake.Requests[2].Body.RootElement.GetProperty("contents");
        Assert.Equal(1, c.GetArrayLength());
        Assert.DoesNotContain("sig-from-A", fake.Requests[2].Body.RootElement.GetRawText());
        Assert.Equal(2, g.MessageCount);
    }

    [Fact]
    public void Free_model_list_starts_with_a_responsive_model() =>
        Assert.Equal("gemini-3.6-flash", GeminiAssistant.DefaultModel);

    [Fact]
    public void Key_format_check_catches_paste_mistakes()
    {
        Assert.True(GeminiKey.LooksValid(Key));
        Assert.False(GeminiKey.LooksValid("short"));
        Assert.False(GeminiKey.LooksValid("AIza FAKE KEY WITH SPACES 00000000000000"));
        Assert.False(GeminiKey.LooksValid("sk-ant-api03-" + new string('a', 40)));   // an Anthropic key pasted in the wrong box
    }
}
