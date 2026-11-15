using Vytrix.Services;
using Xunit;

namespace Vytrix.Tests;

public class HelpTests
{
    // Keep in sync with the sidebar in MainWindow.xaml.cs.
    private static readonly HashSet<string> PageKeys =
        ["overview", "assistant", "optimization", "gaming", "network", "memory", "storage", "tweaks", "startup", "restore", "hardware", "apps", "services", "benchmark", "settings", "account", "help"];

    private static IEnumerable<string> AllText() =>
        HelpContent.Topics.SelectMany(t => t.Blocks.SelectMany(b => b switch
        {
            HelpPara p => [p.Text],
            HelpSteps s => s.Items,
            HelpBullets l => l.Items,
            HelpTable tb => tb.Rows.SelectMany(r => new[] { r.Key, r.Value }),
            _ => [],
        }));

    [Fact]
    public void Topic_ids_are_unique_and_every_topic_has_content()
    {
        Assert.Equal(HelpContent.Topics.Length, HelpContent.Topics.Select(t => t.Id).Distinct().Count());
        Assert.All(HelpContent.Topics, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Title));
            Assert.False(string.IsNullOrWhiteSpace(t.Summary));
            Assert.NotEmpty(t.Blocks);
            Assert.StartsWith("Ico", t.Icon);
        });
    }

    [Fact]
    public void Jump_buttons_point_at_real_pages()
    {
        foreach (var t in HelpContent.Topics.Where(t => t.PageKey is not null))
        {
            Assert.Contains(t.PageKey!, PageKeys);
            Assert.False(string.IsNullOrWhiteSpace(t.PageLabel));
        }
        Assert.All(HelpContent.QuickStart, q => Assert.Contains(q.PageKey, PageKeys));
    }

    [Fact]
    public void Bold_markers_are_balanced() =>
        Assert.All(AllText(), s => Assert.True(s.Split("**").Length % 2 == 1, $"Unbalanced ** in: {s}"));

    [Fact]
    public void Badge_names_match_the_app()
    {
        var badges = HelpContent.ById("tweaks")!.Blocks.OfType<HelpTable>().Single().Rows.Select(r => r.Key).ToHashSet();
        // The labels Kit.StateBadge and TweakView actually display.
        foreach (var label in new[] { "Applied by VYTRIX", "Already on", "Not applied", "Partially applied", "Not supported" })
            Assert.Contains(label, badges);
    }

    [Fact]
    public void Search_finds_topics_by_body_text_and_ignores_case()
    {
        Assert.True(HelpContent.ById("faq")!.Matches("SMARTSCREEN") || HelpContent.ById("faq")!.Matches("code-signed"));
        Assert.True(HelpContent.ById("restore")!.Matches("roll back everything"));
        Assert.False(HelpContent.ById("themes")!.Matches("winsock"));
        Assert.All(HelpContent.Topics, t => Assert.True(t.Matches("")));
        Assert.DoesNotContain(HelpContent.Topics, t => t.SearchText.Contains("**"));
    }

    [Fact]
    public void Help_never_contains_web_links_or_contact_details() =>
        Assert.All(AllText(), s =>
        {
            Assert.DoesNotContain("http", s, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("@", s);
        });
}
