namespace Vytrix.Services;

/// <summary>One piece of a help topic. Text may contain **bold** spans.</summary>
public abstract record HelpBlock;
public sealed record HelpPara(string Text) : HelpBlock;
public sealed record HelpSteps(params string[] Items) : HelpBlock;
public sealed record HelpBullets(params string[] Items) : HelpBlock;
public sealed record HelpTable(string KeyHeader, string ValueHeader, params (string Key, string Value)[] Rows) : HelpBlock;

/// <summary>A help topic. <see cref="PageKey"/> is the page its "Open" button goes to (null = none).</summary>
public sealed record HelpTopic(string Id, string Title, string Icon, string Summary, string? PageKey, string? PageLabel, string Keywords, params HelpBlock[] Blocks)
{
    /// <summary>All searchable text in the topic, without bold markers.</summary>
    public string SearchText => string.Join(" ", new[] { Title, Summary, Keywords }.Concat(Blocks.SelectMany(b => b switch
    {
        HelpPara p => [p.Text],
        HelpSteps s => s.Items,
        HelpBullets l => l.Items,
        HelpTable t => t.Rows.SelectMany(r => new[] { r.Key, r.Value }),
        _ => [],
    }))).Replace("**", "");

    public bool Matches(string q) => string.IsNullOrWhiteSpace(q) || SearchText.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>The in-app user guide shown on the Help page and in global search.</summary>
public static class HelpContent
{
    public static readonly (string Title, string Text, string PageKey, string PageLabel)[] QuickStart =
    [
        ("Pick a profile", "Open Optimization and choose Gaming, Competitive or another profile. You see every change before anything happens.", "optimization", "Open Optimization"),
        ("Fine-tune with tweaks", "Flip individual Windows settings on or off. Each one explains what it does, and how risky it is.", "tweaks", "Open Tweaks"),
        ("Undo anytime", "Every change is recorded first. Roll back one change, or everything, from the Restore Center.", "restore", "Open Restore Center"),
    ];

    public static readonly HelpTopic[] Topics =
    [
        new("start", "Getting started", "IcoShield",
            "Standard mode, administrator mode, and finding things fast.",
            null, null, "admin administrator uac standard mode relaunch search ctrl+k install portable",
            new HelpPara("VYTRIX starts in **standard mode**, which can only change settings for your own Windows account."),
            new HelpPara("Settings that affect the whole PC need administrator rights: services, DNS, restore points, the benchmark and some tweaks. Click **Relaunch as administrator** at the bottom of the sidebar and approve the Windows prompt. The status panel turns green in administrator mode."),
            new HelpPara("Press **Ctrl+K** anywhere to search pages, tweaks, profiles and these help topics.")),

        new("pages", "What each page does", "IcoList",
            "A one-line tour of the sidebar.",
            null, null, "pages sidebar navigation tour",
            new HelpTable("Page", "Use it to",
                ("Overview", "Live CPU, GPU, RAM, network and disk use, temperatures where your PC reports them, and health checks"),
                ("Optimization", "Apply a profile, see recommendations, manage background apps and visual effects"),
                ("Gaming", "Game Mode, GPU scheduling, power plans and per-game launch profiles"),
                ("Network", "Test ping, jitter and packet loss, switch DNS, flush DNS or reset networking"),
                ("Memory", "See what's using your RAM, by app"),
                ("Storage", "Scan for temporary files and caches, preview them, clean up, find large folders"),
                ("Tweaks", "All tweaks, grouped by CPU, GPU, Memory, Storage, Network, Input, Display, Windows & startup and Privacy"),
                ("Startup", "Turn apps that launch with Windows on or off"),
                ("Restore Center", "Undo changes, make restore points, read the activity log"),
                ("Hardware", "Your exact CPU, GPU, memory, motherboard, BIOS and disks"),
                ("Installed Apps", "Search your apps and open their own uninstallers"),
                ("Services", "View Windows services; core and security services are locked"),
                ("Benchmark", "Score your disk, memory and CPU with Windows' own assessment tool"),
                ("Settings", "Theme, accent colour, music volume, motion, updates, reports and the Terms")),
            new HelpPara("If a reading says **Unavailable**, your PC doesn't report it to Windows. VYTRIX never shows a made-up number.")),

        new("profiles", "One-click profiles", "IcoOptimize",
            "Apply a whole set of tweaks and a power plan at once.",
            "optimization", "Open Optimization", "profile balanced gaming competitive creator custom power plan revert",
            new HelpTable("Profile", "What it does",
                ("Balanced", "Keeps Windows' own performance and power settings; removes ads, tips and tracking"),
                ("Gaming", "Game Mode on, background recording off, better windowed games, GPU scheduling where supported, performance power plan"),
                ("Competitive", "Everything in Gaming, plus raw mouse input, fewer visual effects, no web results in Start, no background Store apps"),
                ("Creator", "Full CPU speed for background renders, recording off to free the video encoder, performance power plan; visual effects stay on"),
                ("Custom", "Exactly the tweaks and power plan you choose, nothing else")),
            new HelpSteps(
                "Click a profile. A review lists every change and marks the ones that are already on.",
                "Click **Apply profile**. In administrator mode, VYTRIX makes a Windows restore point first.",
                "Changed your mind? Click **Revert** on the profile, or roll back in the Restore Center.")),

        new("tweaks", "Tweaks and their badges", "IcoTweaks",
            "Flip a switch on to apply a setting; flip it off to restore your previous value.",
            "tweaks", "Open Tweaks", "tweak badge already on applied by vytrix not applied partially not supported risk details switch",
            new HelpPara("Each tweak card shows what it changes and the **Effect** you'll notice. **Show details** adds the full explanation, risk, compatibility, when it takes effect, and the exact settings it changes."),
            new HelpTable("Badge", "Meaning",
                ("Applied by VYTRIX", "VYTRIX turned it on. Turning it off restores your previous value."),
                ("Already on", "It was on before VYTRIX, through Windows Settings, a default or a policy. Hover the badge to see where."),
                ("Not applied", "It's off. Flip the switch to turn it on."),
                ("Partially applied", "Only some of its settings are on. Flip the switch to finish it, or off to restore it."),
                ("Not supported", "Your PC or Windows version can't use it. The tooltip says why.")),
            new HelpPara("Higher-risk tweaks ask you to confirm first. Turning off an **Already on** tweak really turns it off; if it's controlled somewhere VYTRIX deliberately doesn't change, such as a policy, VYTRIX tells you where.")),

        new("restore", "Undo anything", "IcoRestore",
            "Everything VYTRIX changes is recorded first, so you can always go back.",
            "restore", "Open Restore Center", "undo rollback roll back restore point backup journal log uninstall",
            new HelpBullets(
                "**Roll back** a single change from the history list.",
                "**Roll back everything** returns Windows to how it was before VYTRIX, newest change first.",
                "**Create restore point** makes a Windows System Restore point (administrator mode). Windows allows one every 24 hours, and VYTRIX tells you if it skipped one.",
                "Open the backup and log folders, and read today's activity log."),
            new HelpPara("VYTRIX never touches Microsoft Defender, Windows Firewall, Windows Update or core Windows services, and cleanup never touches your personal folders.")),

        new("themes", "Themes and carols", "IcoPalette",
            "Christmas, Cherry Blossom or Graphite, plus free music-box carols.",
            "settings", "Open Settings", "theme christmas cherry blossom graphite accent snow petals reduce motion music carols volume",
            new HelpPara("Switch themes from the palette button in the title bar or in **Settings**. Settings also has accent colours, falling snow and petals, and **Reduce motion**. The scenery only moves while VYTRIX is the active window, so it never slows down a game."),
            new HelpPara("Press **Play carols** on the Overview or **Carols** in the title bar for music-box versions of five carols that are free for anyone to use. VYTRIX makes them on your PC the first time (about 3 seconds, no internet). Music never starts by itself, pauses when you minimise VYTRIX and stops when you close it.")),

        new("updates", "Updates", "IcoRefresh",
            "VYTRIX updates itself, and nothing installs without your OK.",
            "settings", "Open Settings", "update auto update version download install github",
            new HelpSteps(
                "VYTRIX checks for a new version when it starts and every 6 hours.",
                "A new version downloads in the background and its fingerprint is checked, so a tampered file is rejected.",
                "You choose **Install now** or **Later**. Installing shows one Windows prompt, then VYTRIX restarts on the new version."),
            new HelpPara("Your settings, change history and backups are kept. Turn off automatic checks in **Settings**, under **Updates**.")),

        new("faq", "Common questions", "IcoInfo",
            "Quick answers to the things people ask most.",
            null, null, "faq troubleshooting temperature unavailable smartscreen privacy data uninstall reports dns ipv6",
            new HelpTable("Question", "Answer",
                ("A switch won't turn on", "It probably needs administrator mode. Click **Relaunch as administrator** in the sidebar."),
                ("CPU temperature says Unavailable", "Many PCs don't report it to Windows without a special driver, which VYTRIX deliberately doesn't install."),
                ("GPU temperature says Unavailable", "GPU temperature works on NVIDIA cards. AMD and Intel GPUs aren't supported yet."),
                ("A tweak hasn't taken effect", "Check **Takes effect** under **Show details**. Some need a sign-out or restart."),
                ("Something feels wrong", "Roll the change back in the Restore Center, or use **Roll back everything**."),
                ("Windows warned me about the installer", "VYTRIX isn't code-signed yet. Choose **More info**, then **Run anyway**."),
                ("DNS changed but IPv6 didn't", "DNS presets apply to IPv4 only. IPv6 DNS is left unchanged."),
                ("Does VYTRIX collect my data?", "No. VYTRIX sends no information about you or your PC. It contacts GitHub to check for updates, and the Network tests ping public servers."),
                ("Where are my reports?", "In **Documents**, in the **VYTRIX Reports** folder."),
                ("How do I uninstall?", "Optionally **Roll back everything** first, then uninstall VYTRIX from Windows Settings, Apps."))),
    ];

    public static HelpTopic? ById(string id) => Topics.FirstOrDefault(t => t.Id == id);
}
