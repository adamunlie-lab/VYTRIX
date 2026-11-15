# VYTRIX
**Precision Performance. Zero Compromise.**

VYTRIX is a Windows 10/11 optimization suite built on safety. Every change it makes is a documented Windows setting, is shown to you before it is applied, has its previous value recorded, and can be rolled back with one click. VYTRIX has no placebo tweaks, no fake RAM cleaners, no "ping booster", and it never touches Defender, Firewall, Windows Update or core services.

---

## Quick start

| What you want | How |
|---|---|
| Run it now | `dist\VYTRIX\VYTRIX.exe` (portable, self-contained, no install needed) |
| Portable zip | `dist\VYTRIX-1.0.0-win-x64.zip` |
| Rebuild everything + run tests | `powershell -ExecutionPolicy Bypass -File build.ps1` |
| Install it | `dist\VYTRIX-Setup-1.0.0.exe` (Start menu shortcut, optional desktop shortcut, uninstall from Apps & features) |
| Rebuild the installer | Needs [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`), then `build.ps1 -Installer` |
| Installer QA | `tools\installer-qa.ps1`: silent install → verify → launch → silent uninstall → verify removal (one UAC prompt) |

VYTRIX starts in **standard mode**. Changes that affect the whole machine (HKLM settings, services, DNS, restore points, WinSAT) need **Relaunch as administrator** (bottom of the sidebar), which goes through the normal UAC prompt.

### Requirements to build
- Windows 10 2004+ / Windows 11, x64
- Visual Studio 2022/2026 with **Desktop development with C++** (MSVC + Windows SDK)
- .NET 10 SDK

---

## Technology choices

| Layer | Choice | Why |
|---|---|---|
| System core | **C++20 DLL** (`native/VytrixCore`) | Direct, low-overhead access to PDH counters, DXGI/D3DKMT, PowrProf, ICMP, Toolhelp/PSAPI and NVML. Sampling all live metrics takes well under 1 ms. |
| UI | **WPF on .NET 10** | Native Windows desktop UI with full styling control, GPU-composited rendering, mature async model and small idle footprint (≈0.2% CPU while live monitoring). Compiles with the toolchain already on the machine. |
| Interop | P/Invoke with blittable/sequential structs | No COM registration, no C++/CLI; the DLL can be tested independently. |
| Persistence | JSON (settings, journal, game profiles) + JSON-lines logs | Human-readable, diff-able, atomic writes (`.tmp` + replace). |

Electron/Tauri were rejected for footprint and because the native work is in C++ anyway; WinUI 3 was rejected because WPF gives the same visual result with fewer deployment requirements.

---

## Architecture

```
native/VytrixCore/          C++ core (VytrixCore.dll)
  VytrixCore.h/.cpp         telemetry, GPU/HAGS, processes, power plans, ping, recycle bin
src/Vytrix/
  Native/NativeMethods.cs   P/Invoke surface (VxNative) + SystemParametersInfo helpers
  Core/AppCore.cs           paths, structured logger, elevation, formatting, command runner
  Core/Safety.cs            registry snapshot/restore, .reg backups, change journal,
                            System Restore (WMI), settings
  Services/Tweaks.cs        tweak model, TweakEngine (apply/revert/rollback), TweakCatalog
  Services/SystemInfo.cs    static hardware info + live Telemetry pump
  Services/SystemServices.cs processes, startup manager, power plans, network/DNS
  Services/StorageService.cs cleanup categories (preview → delete), folder sizing
  Services/ExtraServices.cs services viewer, installed apps, WinSAT benchmark,
                            profiles, game launch profiles, fullscreen-opt flag,
                            reports, updater
  Controls/                 Sparkline, Meter, RingGauge, Spinner, ResponsiveGrid,
                            ThemeManager, UI kit
  Views/                    one class per page
  Themes/Theme.xaml         palette, typography, custom icon set, control styles
tests/Vytrix.Tests/         xUnit tests (109, incl. 13 admin)
legal/TERMS.txt             Terms of Agreement (embedded in the app and shown by the installer)
tools/                      icon generator, screenshot + UI-automation QA scripts
installer/vytrix.iss        Inno Setup script
```

Design rules applied throughout:
- **No fabricated readings.** The native core returns `-1` for any sensor it cannot read. The UI shows "Unavailable" with an explanation, never a number.
- **No false success.** Every operation returns `(bool ok, string message)`, and the UI shows the operation's own result. Windows' silent 24-hour skip on restore points is detected and reported.
- **UI never blocks.** System calls run on background tasks. Live telemetry is sampled on a timer thread and published to the UI only while a page that shows it is visible.

---

## Safety and restore system

1. **Change journal** (`%LocalAppData%\VYTRIX\journal.json`). Before any registry write, VYTRIX captures each value exactly: whether it existed, its type and its data, and which keys it is about to create. DNS changes record the previous servers and mode. Power-plan changes record the previous plan. Startup and service changes record the previous registry values.
2. **Registry backups.** Every affected key is exported with `reg export` to `%LocalAppData%\VYTRIX\backups\*.reg` before modification. You can toggle this in Settings.
3. **System Restore points** via the documented WMI `SystemRestore` class, created automatically before profiles in administrator mode.
4. **One-click rollback.** Restore Center → *Roll back* on any change, or *Roll back everything* (newest first). Turning a tweak off restores the exact recorded value. If no record exists (the setting was changed outside VYTRIX), it restores the documented Windows default instead.
5. **Protected components.** Defender, Firewall, Windows Update, BITS, cryptographic, RPC, audio, networking and other core services are locked in the Services viewer. Core and security processes can never be ended. Cleanup only targets system-managed temp and cache folders, never personal folders, and re-checks each file's location at delete time.
6. **Action log.** Structured JSON-lines log per day in `%LocalAppData%\VYTRIX\logs`, viewable in the Restore Center.

> **Before uninstalling**, use Restore Center → *Roll back everything* if you want Windows returned to its previous state. The journal and backups are deliberately kept on uninstall.

## Terms of Agreement

The VYTRIX Terms of Agreement are in [legal/TERMS.txt](legal/TERMS.txt). They cover:
- what VYTRIX changes;
- the license;
- your responsibilities;
- the limits of rollback;
- privacy (no data is collected or sent);
- third-party components;
- the warranty disclaimer and limitation of liability.

The installer shows them and won't continue unless you accept. The app asks on first launch: **Accept** stays disabled until the "I have read and agree" box is ticked, and **Decline** exits. Acceptance (version and time) is stored in settings. Changing `Terms.Version` in `Core/Safety.cs` makes everyone accept again. You can re-read the terms in Settings → Terms of Agreement.

These terms were drafted for this project and are not legal advice. Have a lawyer review them before distributing VYTRIX commercially.

---

## Features

- **Overview:** live CPU / GPU / RAM / network and disk graphs, CPU and GPU models, temperatures where exposed, Windows version, uptime, power plan, background process count, startup app count, health checks, optimization status and quick actions.
- **Optimization:** the Balanced, Gaming, Competitive, Creator and Custom profiles. Each one opens a full review showing every change and its state before anything is applied, and has a one-click revert. Also recommendations generated from your actual configuration, background apps (excluding Windows components), background permissions and visual effects.
- **Gaming:** Game Mode, hardware-accelerated GPU scheduling (support read from the driver), background recording, windowed-game optimizations and mouse acceleration. Also power-plan management (it can add the hidden High/Ultimate plans), game launch profiles (power plan, priority, closing chosen apps, restoring the plan on exit), per-game fullscreen-optimization flags, and a launcher/overlay view.
- **Network:** adapter details (link speed, IPs, gateway, DNS mode), latency, jitter and packet-loss diagnostics with plain-language interpretation, DNS presets (Cloudflare, Google, Quad9) with rollback, and flush DNS / renew IP / reset Winsock with explanations.
- **Memory:** in use, available, committed, commit limit, cache and kernel memory; the top 20 apps by private bytes or working set; installed modules; Windows Memory Diagnostic and Resource Monitor.
- **Storage:** volumes and disk health, a cleanup scan that previews every file, permanent-delete confirmation, and a drill-down large-folder analyzer.
- **Tweaks:** 27 tweaks organised by component: CPU, GPU, Memory, Storage, Network, Input, Display & visuals, Windows & startup, and Privacy. Each section shows its count and how many are applied, and the library is searchable and filterable by state. The Memory section explains why it has no tweaks. See [docs/OPTIMIZATIONS.md](docs/OPTIMIZATIONS.md).
- **Startup:** the Run keys and Startup folders, with publisher and missing-file detection. Entries are enabled or disabled the same way Task Manager does it (StartupApproved), never deleted.
- **Restore Center:** restore points, the change history with per-item and full rollback, backup and log folders, and today's log.
- **Hardware:** CPU (CPUID and topology), GPUs (DXGI), memory modules, motherboard, BIOS, disks (MSFT_PhysicalDisk) and the sensors available on this PC.
- **Installed Apps:** search and sort, and opens each app's own uninstaller (never silent).
- **Services:** every service, classified Protected / Optional / Caution. Startup-type changes are journaled.
- **Benchmark:** the Windows System Assessment Tool (`winsat`) for disk sequential/random, memory bandwidth, CPU encryption and a full assessment with scores.
- **Christmas edition (default theme):** a midnight-pine palette with crimson and gold.
  - The Overview hero shows a decorated tree drawn in code (`Controls/Christmas.cs`): tiered boughs with snow, a gold garland, ornaments, three banks of fairy lights that twinkle out of phase, a glowing star and presents.
  - Snow drifts across the window, and the hero shows a days-until-Christmas greeting.
- **Carols:** music-box arrangements of five public-domain carols, *synthesized by VYTRIX on your PC* (`Services/Carols.cs`). No downloads, no recordings, no licensing questions.
  - The carols are Jingle Bells (1857), We Wish You a Merry Christmas (traditional), Deck the Halls (traditional), Silent Night (1818) and Joy to the World (1839).
  - The first press of **Play carols** renders them to small WAV files in about 3 seconds.
  - Music never starts on its own, pauses while VYTRIX is minimised and stops when it closes. There's a volume control in Settings.
- **Polish:**
  - a branded splash that waits for the first live reading;
  - a title-bar theme switcher and music controls;
  - glossy primary buttons, cards that respond on hover, sliding toasts with a coloured edge, and a deep scrim behind dialogs.
- **Settings already on are shown as on.** VYTRIX reads the real state of every tweak. If you (or Windows) already turned a setting on, the tweak shows **Already on**, separate from **Applied by VYTRIX**, with a tooltip saying where it's set. This covers:
  - **Windows defaults** that are already on (Game Mode, background recording off on Windows 11);
  - **driver defaults** (HAGS on newer GPU drivers);
  - **settings stored in a second place** by Windows Settings (Bing web search, diagnostic data, Delivery Optimization);
  - **Group Policies**;
  - **settings that are off because of another setting** (Fast Startup is off when hibernation is disabled).

  Turning such a tweak off really turns it off. If it's controlled somewhere VYTRIX shouldn't change, such as a policy, VYTRIX says exactly where instead of claiming success.
- **Themes:** **Christmas** (default), **Cherry Blossom** or **Graphite**, switchable live from the title bar or Settings.
  - Cherry Blossom is a night-sakura palette.
  - The Overview has a hero banner with a procedurally generated cherry tree whose limbs sway in the wind, with smaller branches riding on bigger ones (`Controls/Sakura.cs`). The same tree stands behind every other page.
  - Petals drift across the window. You can turn them off.
  - The scene animates only while VYTRIX is the active window. It pauses when minimised, in the background or with Reduce motion on, and costs about 0.3–0.5% CPU while animating.
- **Settings:** theme, accent colour (Violet / Cyan / Emerald / Amber / Rose / Sakura), falling petals, reduced motion, monitoring interval, safety switches, update feed, report export and Explorer restart.
- **Help:** a built-in guide (Views/HelpPage.cs, content in Services/Help.cs) with a quick start, topics with jump-to-page buttons, a badge legend, common questions and a filter. Help topics also appear in global search.
- **Global search** (Ctrl+K) across pages, tweaks, profiles and help topics.
- **Reports:** HTML and JSON system reports in `Documents\VYTRIX Reports`.
- **Auto-update from GitHub releases.** VYTRIX reads `releases/latest/download/latest.json` from [adamunlie-lab/VYTRIX](https://github.com/adamunlie-lab/VYTRIX/releases) at startup and every 6 hours.
  - If there's a newer version, it downloads the installer in the background (only from GitHub release hosting, including after redirects) and verifies its SHA-256.
  - It then offers **Install now / Later** with the release notes. Install now runs setup in update mode: a progress window and one administrator prompt. VYTRIX closes, updates in place (settings, journal and backups are kept) and reopens as the normal user.
  - Nothing ever installs without your confirmation. Automatic checks and background downloads can be turned off in Settings → Updates.
  - Verified end to end: 1.0.0 updated itself to 1.0.1 from the live releases (`tools\update-e2e.ps1`).

### Publishing a release
```powershell
powershell -File tools\publish-release.ps1 -Version 1.0.2 -Notes "What changed"
```
This builds at that version, runs the tests, compiles the installer, computes the SHA-256, writes `latest.json`, and creates GitHub release `v1.0.2` with the installer, the portable zip and `latest.json`. Every installed copy is offered it automatically. Version numbers must increase. Releases aren't code-signed yet, so Windows SmartScreen may warn on a first manual download until the publisher has a code-signing certificate.

---

## Testing

```powershell
dotnet test tests\Vytrix.Tests\Vytrix.Tests.csproj     # 109 automated tests (admin ones skip unless elevated)
powershell -File tools\qa-music.ps1                     # carols: render, play (15% volume), pause, CPU
powershell -File tools\qa-motion.ps1                    # proves the tree animates; CPU active vs minimised
powershell -File tools\qa-theme-switch.ps1              # live theme switching via Settings
powershell -File tools\admin-qa.ps1                     # elevated QA: full suite as admin + admin UI checks (UAC prompt)
powershell -File tools\ui-smoke.ps1                     # UI automation smoke test (read-only actions)
powershell -File tools\qa-screens.ps1                   # screenshot of every page
```

The automated tests cover:
- the registry snapshot/restore round trip for every value type, including key creation and removal;
- tweak apply → revert restoring exact prior values, the fallback to Windows defaults, and generic journal rollback;
- the admin-refusal path, and unsupported tweaks never reporting success;
- catalog integrity: complete documentation, every category covered, HKLM tweaks requiring admin, no security or update paths, no placebo network values;
- profile references;
- protected services and processes;
- cleanup never targeting personal folders, and skipping recent files;
- parsing and formatting;
- and against the real C++ core: CPU, memory, range-checked samples, process list, power plans, ping, GPU enumeration, sampling cost, and handle-leak checks.

Tests run in an isolated data folder (`VYTRIX_DATA_DIR`) and a scratch registry key that is deleted afterwards.

The UI smoke test checks:
- startup time, and navigation to all 15 pages;
- the storage scan, and that the delete confirmation dialog appears and Cancel aborts it;
- the profile review dialog;
- idle CPU under 2% and working set under 400 MB;
- that the app shuts down cleanly.

## Known limitations
- **CPU temperature:** Windows exposes no CPU package sensor without a kernel driver, which VYTRIX deliberately does not install. VYTRIX shows the ACPI thermal zone when the firmware provides one; otherwise it shows "Unavailable".
- **GPU temperature:** read through NVIDIA NVML. AMD and Intel GPUs show "Unavailable".
- **DNS:** changes apply to IPv4 only; IPv6 DNS is left unchanged and the UI says so.
- **Administrator QA** (`tools\admin-qa.ps1`) makes real, temporary changes and verifies each one is restored exactly: an HKLM policy tweak, an optional service's startup type, Ethernet DNS and the power plan. It also creates a real System Restore point and runs a WinSAT memory test. Results go to `qa-results\admin-report.txt`.
