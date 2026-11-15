using System.Text.Json;
using System.Text.Json.Nodes;
using Vytrix.Services;
using Xunit;

namespace Vytrix.Tests;

/// <summary>The free local assistant, driven against a fake of Ollama's local API.</summary>
public class LocalAITests
{
    private static string Chat(object message) => JsonSerializer.Serialize(new { model = "qwen3:4b", created_at = "2026-11-15T00:00:00Z", message, done = true, done_reason = "stop" });

    [Fact]
    public async Task Tool_loop_runs_tools_and_returns_validated_buttons()
    {
        using var fake = new AssistantTests.FakeClaude(
            (200, Chat(new
            {
                role = "assistant", content = "Let me check your tweaks first.",
                tool_calls = new object[]
                {
                    new { function = new { name = "list_tweaks", arguments = new { } } },
                    new { function = new { name = "suggest_actions", arguments = new { actions = new object[] { new { kind = "open_page", target = "help", reason = "Learn more." }, new { kind = "apply_tweak", target = "made.up", reason = "x" } } } } },
                },
            })),
            (200, Chat(new { role = "assistant", content = "<think>let me reason</think>Here's what I'd do." })));
        Ollama.BaseUrl = fake.Url;
        var s = new LocalAssistant("qwen3:4b");
        var reply = await s.SendAsync("How do I speed up my PC?");

        Assert.Equal("Here's what I'd do.", reply.Text);
        var a = Assert.Single(reply.Actions);
        Assert.Equal(("open_page", "help"), (a.Kind, a.Target));

        var r0 = fake.Requests[0].Body.RootElement;
        Assert.Equal("qwen3:4b", r0.GetProperty("model").GetString());
        Assert.False(r0.GetProperty("stream").GetBoolean());
        Assert.True(r0.GetProperty("think").GetBoolean());   // keeps reasoning out of the answer text
        Assert.Equal(AssistantSession.ToolSpecs.Length, r0.GetProperty("tools").GetArrayLength());
        Assert.Equal("function", r0.GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.Equal("object", r0.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
        Assert.Equal("system", r0.GetProperty("messages")[0].GetProperty("role").GetString());

        var m1 = fake.Requests[1].Body.RootElement.GetProperty("messages");
        // system, user, assistant (with tool_calls), tool, tool
        Assert.Equal(5, m1.GetArrayLength());
        Assert.True(m1[2].TryGetProperty("tool_calls", out _));
        Assert.Equal("tool", m1[3].GetProperty("role").GetString());
        Assert.Equal("list_tweaks", m1[3].GetProperty("tool_name").GetString());
        Assert.Contains("gaming.gamemode", m1[3].GetProperty("content").GetString());
        Assert.Contains("'made.up' is not a tweak id", m1[4].GetProperty("content").GetString());
        Assert.Equal(5, s.MessageCount);   // user, assistant, tool, tool, assistant
    }

    [Fact]
    public async Task Missing_model_error_is_shown_in_plain_words()
    {
        using var fake = new AssistantTests.FakeClaude((404, """{"error":"model 'qwen3:4b' not found"}"""));
        Ollama.BaseUrl = fake.Url;
        var ex = await Assert.ThrowsAsync<LocalAiException>(() => new LocalAssistant("qwen3:4b").SendAsync("hi"));
        Assert.Contains("not found", LocalAssistant.Describe(ex));
    }

    [Fact]
    public void Pull_progress_lines_are_parsed()
    {
        Assert.Equal(("pulling manifest", -1.0, (string?)null), Ollama.ParsePullLine("""{"status":"pulling manifest"}"""));
        var (s, f, e) = Ollama.ParsePullLine("""{"status":"pulling abc","digest":"sha256:abc","total":2000,"completed":500}""");
        Assert.Equal(0.25, f, 3);
        Assert.Null(e);
        Assert.Equal("success", Ollama.ParsePullLine("""{"status":"success"}""").Status);
        Assert.Equal("disk full", Ollama.ParsePullLine("""{"error":"disk full"}""").Error);
    }

    [Fact]
    public void Arguments_sent_as_a_json_string_are_accepted()
    {
        var args = LocalAssistant.ParseArgs(JsonValue.Create("""{"id":"gaming.gamemode"}"""));
        Assert.Equal("gaming.gamemode", args["id"].GetString());
        Assert.Empty(LocalAssistant.ParseArgs(null));
        Assert.Empty(LocalAssistant.ParseArgs(JsonValue.Create("not json")));
    }

    [Fact]
    public void Inline_reasoning_is_removed()
    {
        Assert.Equal("Answer.", LocalAssistant.CleanContent("<think>\nhmm\n</think>\n\nAnswer."));
        // Seen for real with qwen3:4b: reasoning with only the closing tag.
        Assert.Equal("Answer.", LocalAssistant.CleanContent("Okay, the user wants...\nSo the best action is X.\n</think>\n\nAnswer."));
        Assert.Equal("Plain answer.", LocalAssistant.CleanContent("Plain answer."));
    }

    [Fact]
    public void Recommendations_in_words_still_get_buttons()
    {
        var a = LocalAssistant.InferActions("Your PC is ready for games. Apply the **Gaming** profile for the smoothest play.");
        Assert.Contains(a, x => x.Kind == "open_page" && x.Target == "optimization" && x.Label == "Open profiles");
        Assert.Empty(LocalAssistant.InferActions("Your PC looks healthy. Nothing to change."));
        // Tweaks named in the answer only become buttons when they can actually be turned on.
        foreach (var x in LocalAssistant.InferActions(string.Join(", ", TweakCatalog.All.Select(t => t.Name))).Where(x => x.Kind == "apply_tweak"))
            Assert.Equal(TweakState.NotApplied, TweakCatalog.ById(x.Target)!.GetState() == TweakState.Partial ? TweakState.NotApplied : TweakCatalog.ById(x.Target)!.GetState());
    }

    [Fact]
    public async Task Version_check_reports_not_running_quickly()
    {
        Ollama.BaseUrl = "http://127.0.0.1:1";   // nothing listens here
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(await Ollama.VersionAsync());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4));
    }
}
