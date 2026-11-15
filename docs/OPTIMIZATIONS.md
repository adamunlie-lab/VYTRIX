# VYTRIX optimization reference

The Tweak Library has 56 tweaks, organised by the part of your PC they act on. Every tweak is a documented Windows setting: a Settings app toggle, a Group Policy, a documented registry value or a power-plan setting. Each entry lists the exact change, its real effect and its risk. All of them can be reversed: turning a tweak off restores the value recorded before VYTRIX changed it.

**Risk levels:**
- **Low:** a user preference; no effect on stability.
- **Moderate:** a system behaviour change with trade-offs (power, heat, compatibility).
- **Advanced:** reserved; no current tweak uses it.

## CPU
| Tweak | Change | Real effect | Risk |
|---|---|---|---|
| Game Mode | `HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled` = 1 (a missing value also means on). Turning it off writes 0. | While a game has focus, Windows gives it scheduling priority and holds back Windows Update driver installs and restart prompts. | Low |
| Disable power throttling | `HKLM\…\Control\Power\PowerThrottling\PowerThrottlingOff` = 1 | Background work isn't forced into EcoQoS (efficiency mode). Uses more power. | Moderate |
| Keep CPU clocks up when plugged in | The active plan's *Minimum processor state (plugged in)* = 100% (`PowerWriteACValueIndex`, SUB_PROCESSOR / PROCTHROTTLEMIN). The previous value is recorded and restored. | Removes clock ramp-up delay between bursts of work. Raises idle power and temperature. Small difference on CPUs with fast hardware P-states (Ryzen CPPC, Intel Speed Shift). High performance and Ultimate plans already use 100%. Battery value is unchanged. | Moderate |

## GPU
| Tweak | Change | Real effect | Risk |
|---|---|---|---|
| Hardware-accelerated GPU scheduling | `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode` = 2 | The GPU schedules its own work. It can lower CPU overhead and latency, and it is required for DLSS Frame Generation. Offered only when the driver reports support (D3DKMT WDDM 2.7 caps). Restart required. | Moderate |
| Optimizations for windowed games | `SwapEffectUpgradeEnable=1` in `DirectXUserGlobalSettings` (other entries kept) | Flip-model presentation for DX10/11 games in windowed or borderless mode: lower latency, VRR and Auto HDR. Windows 11 only. | Low |
| Variable refresh rate for windowed games | `VRROptimizeEnable=1` in `DirectXUserGlobalSettings` | G-SYNC/FreeSync for DX11 games without native VRR support. No effect without a VRR display. | Low |
| Turn off background game recording | `HKCU\System\GameConfigStore\GameDVR_Enabled` = 0, `…\GameDVR\AppCaptureEnabled` = 0 | Stops the rolling capture buffer, freeing the GPU video encoder. | Low |
| Turn off PCI Express link power saving when plugged in | Active plan's *PCI Express → Link State Power Management (plugged in)* = Off. The previous value is recorded and restored. | Keeps GPU and NVMe links at full power on AC. Can remove small wake-up delays on some systems; slightly more idle power. Battery unchanged. | Moderate |

## Memory
**No tweaks, by design.** Windows already uses spare RAM as a cache and frees it instantly when an app needs it. "RAM cleaners", standby-list purges and paging-file tricks make performance worse. The Memory page shows what is really using your RAM; an automated test enforces that this section stays empty.

## Storage
| Tweak | Change | Real effect | Risk |
|---|---|---|---|
| Turn on Storage Sense | `HKCU\…\StorageSense\Parameters\StoragePolicy\01` = 1 | Windows removes temporary files automatically when space runs low, following the rules in Settings → Storage → Storage Sense. Check the Downloads rule after turning it on. | Low |
| Disable hibernation | `powercfg /hibernate off` (revert: `on`) | Deletes `hiberfil.sys`, roughly 40% of RAM in disk space. Also disables Fast Startup. | Moderate |
| Never turn off hard disks when plugged in | Active plan's *Hard disk → Turn off hard disk after (plugged in)* = Never. The previous value is recorded and restored. | No spin-up pause from a sleeping hard drive. No effect on SSDs. | Low |
| Allow long file paths | `HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\LongPathsEnabled` = 1 | Programs that opt in can use paths over 260 characters (mods, Git, Node.js). | Low |
| Show icons instead of thumbnails | `HKCU\…\Explorer\Advanced\IconsOnly` = 1 | Media-heavy folders open faster; no previews. | Low |

## Network
| Tweak | Change | Real effect | Risk |
|---|---|---|---|
| Delivery Optimization: local network only | Policy `DODownloadMode` = 1 | Your upload bandwidth isn't used to send updates to internet peers. **It does not change ping.** | Low |

VYTRIX deliberately does **not** include `TcpAckFrequency`, `TCPNoDelay`, `NetworkThrottlingIndex`, `SystemResponsiveness` or similar "latency" edits. On modern Windows and games they have no measurable benefit, and an automated test enforces their absence. DNS changes and network diagnostics are on the Network page.

## Input
| Tweak | Change | Real effect | Risk |
|---|---|---|---|
| Disable "Enhance pointer precision" | `HKCU\Control Panel\Mouse` MouseSpeed/Threshold1/Threshold2 = 0, applied live with SPI_SETMOUSE | 1:1 cursor movement. Improves consistency, not FPS. | Low |
| Turn off the Sticky Keys shortcut | `HKCU\Control Panel\Accessibility\StickyKeys\Flags` = 506 (default 510) | No pop-up from pressing Shift five times. Sticky Keys itself still works. | Low |
| Turn off the Filter Keys shortcut | `HKCU\Control Panel\Accessibility\Keyboard Response\Flags` = 122 (default 126) | No pop-up from holding right Shift. | Low |
| Turn off the Toggle Keys shortcut | `HKCU\Control Panel\Accessibility\ToggleKeys\Flags` = 58 (default 62) | No prompt from holding Num Lock. | Low |
| Shorter key repeat delay | `HKCU\Control Panel\Keyboard\KeyboardDelay` = 0 (default 1), applied live | Held keys repeat sooner in text and menus. | Low |
| Keep USB devices awake when plugged in | Active plan's *USB selective suspend (plugged in)* = Disabled. The previous value is recorded and restored. | Can fix mice, headsets and controllers cutting out after idling. Slightly more idle power. | Low |
| Controller Xbox button doesn't open Game Bar | `HKCU\Software\Microsoft\GameBar\UseNexusForGameBarEnabled` = 0 | The guide button no longer opens the overlay; Win+G still does. | Low |

## Display & visuals
| Tweak | Change | Real effect | Risk |
|---|---|---|---|
| Turn off transparency effects | `HKCU\…\Themes\Personalize\EnableTransparency` = 0 | Less composition work, most noticeable on integrated GPUs. | Low |
| Turn off minimise/maximise animation | `HKCU\Control Panel\Desktop\WindowMetrics\MinAnimate` = 0, applied live | Windows open and close instantly. | Low |
| Turn off taskbar animations | `HKCU\…\Explorer\Advanced\TaskbarAnimations` = 0 | Snappier taskbar. | Low |
| Faster menu display | `HKCU\Control Panel\Desktop\MenuShowDelay` = 100 (default 400), applied live | Submenus open sooner. | Low |
| Don't show window contents while dragging | `HKCU\Control Panel\Desktop\DragFullWindows` = 0, applied live | Drags an outline instead of redrawing the window. Helps older or integrated GPUs. | Low |
| Turn off desktop icon label shadows | `HKCU\…\Explorer\Advanced\ListviewShadow` = 0 | Crisper labels. Cosmetic. | Low |
| Turn off translucent selection rectangle | `HKCU\…\Explorer\Advanced\ListviewAlphaSelect` = 0 | Plain selection outline. Cosmetic. | Low |
| Turn off Peek | `HKCU\Software\Microsoft\Windows\DWM\EnableAeroPeek` = 0 | No accidental desktop previews from the taskbar corner. | Low |

## Windows & startup
| Tweak | Change | Real effect | Risk |
|---|---|---|---|
| Block background Store apps | `HKCU\…\BackgroundAccessApplications\GlobalUserDisabled` = 1 | UWP apps don't run in the background. On Windows 11 the per-app setting takes priority. | Low |
| Remove web results from Start search | Policy `DisableSearchBoxSuggestions` = 1 | Local-only Start search; no queries sent to Bing. | Low |
| Turn off tips and suggestions | ContentDeliveryManager `SubscribedContent-338389Enabled`, `SystemPaneSuggestionsEnabled`, `SilentInstalledAppsEnabled` = 0 | No promotional tips, and no suggested apps installed automatically. | Low |
| Show file extensions | `HKCU\…\Explorer\Advanced\HideFileExt` = 0 | Disguised files like `x.pdf.exe` become visible. | Low |
| Classic right-click menu | Empty `InprocServer32` under `HKCU\Software\Classes\CLSID\{86ca1aa0-…}`; revert deletes the key | Full context menu without "Show more options". Windows 11 only. | Low |
| Remove startup app delay | `HKCU\…\Explorer\Serialize\StartupDelayInMSec` = 0 | Run-key apps launch immediately after sign-in. | Low |
| Disable Fast Startup | `HKLM\…\Session Manager\Power\HiberbootEnabled` = 0 | Every shutdown is a full shutdown. This fixes driver and dual-boot issues and makes uptime reset. | Low |
| Show hidden files and folders | `HKCU\…\Explorer\Advanced\Hidden` = 1 (default 2) | Game saves and configs in hidden folders such as AppData are visible. System files stay hidden. | Low |
| Open File Explorer to This PC | `HKCU\…\Explorer\Advanced\LaunchTo` = 1 | Explorer opens on your drives instead of Home. | Low |
| Turn off lock screen tips and fun facts | ContentDeliveryManager `RotatingLockScreenOverlayEnabled`, `SubscribedContent-338387Enabled` = 0 | A clean lock screen. | Low |
| Turn off Start menu recommendations | `HKCU\…\Explorer\Advanced\Start_IrisRecommendations` = 0 | No promoted tips and apps in Start. Windows 11 22H2+. | Low |
| Add "End task" to taskbar right-click | `HKCU\…\Explorer\Advanced\TaskbarDeveloperSettings\TaskbarEndTask` = 1 | Close a frozen app from the taskbar. Windows 11 23H2+. | Low |
| Hide the Task View button | `HKCU\…\Explorer\Advanced\ShowTaskViewButton` = 0 | Tidier taskbar; Win+Tab still works. | Low |
| Turn off AutoPlay | `HKCU\…\Explorer\AutoplayHandlers\DisableAutoplay` = 1 | No pop-ups or automatic actions when media is plugged in. | Low |
| Don't reopen apps after restarting | `HKCU\…\Windows NT\CurrentVersion\Winlogon\RestartApps` = 0 (off by default on Windows 11) | Apps don't relaunch on their own after updates and restarts. | Low |

## Privacy
| Tweak | Change | Real effect | Risk |
|---|---|---|---|
| Turn off advertising ID | `HKCU\…\AdvertisingInfo\Enabled` = 0 | Apps can't use a shared ad identifier. | Low |
| Turn off tailored experiences | `HKCU\…\Privacy\TailoredExperiencesWithDiagnosticDataEnabled` = 0 | Diagnostic data isn't used to personalise tips or ads. | Low |
| Disable activity history | Policies `PublishUserActivities` = 0, `UploadUserActivities` = 0 | No local activity timeline and no upload of it. | Low |
| Send only required diagnostic data | Policy `AllowTelemetry` = 1 | Minimum telemetry for your edition. Updates and security are unaffected. Windows shows "managed by your organization" while the policy is set. | Moderate |
| Turn off online speech recognition | `HKCU\…\OnlineSpeechPrivacy\HasAccepted` = 0 | Voice input stays on the device. | Low |
| Turn off suggested content in Settings | ContentDeliveryManager `SubscribedContent-338393Enabled`, `-353694Enabled`, `-353696Enabled` = 0 | No promotions inside Settings. | Low |
| Don't share your language list with websites | `HKCU\Control Panel\International\User Profile\HttpAcceptLanguageOptOut` = 1 | One less detail sites can read. | Low |
| Don't track app launches | `HKCU\…\Explorer\Advanced\Start_TrackProgs` = 0 | No launch tracking; Start loses its "most used" list. | Low |
| Turn off inking and typing personalization | InputPersonalization `RestrictImplicitInkCollection`/`RestrictImplicitTextCollection` = 1, `HarvestContacts` = 0, Personalization `AcceptedPrivacyPolicy` = 0 | No personal dictionary built from your typing and handwriting. | Low |
| Never ask for feedback | `HKCU\Software\Microsoft\Siuf\Rules\NumberOfSIUFInPeriod` = 0 | No feedback prompts. | Low |
| Don't include cloud content in search | SearchSettings `IsMSACloudSearchEnabled`, `IsAADCloudSearchEnabled` = 0 | Search stays on this PC. | Low |
| Turn off search history on this device | SearchSettings `IsDeviceSearchHistoryEnabled` = 0 | Searches aren't remembered. | Low |

## Profiles
| Profile | Tweaks | Power plan |
|---|---|---|
| **Balanced** | Game Mode, tips off, advertising ID off, tailored experiences off, file extensions | Balanced |
| **Gaming** | Game Mode, background recording off, windowed optimizations, HAGS (if supported), Sticky/Filter/Toggle Keys shortcuts off, tips off, advertising ID off | High performance (if present) |
| **Competitive** | Gaming + USB devices kept awake, raw mouse, transparency off, minimise animation off, taskbar animations off, web search off, Delivery Optimization LAN, background Store apps off, tailored experiences off | High performance |
| **Creator** | Power throttling off, background recording off, no startup delay, file extensions, tips off, faster menus. Visual effects are kept for colour and preview work. | High performance |
| **Custom** | Exactly the tweaks you tick on the Optimization page | Your choice: unchanged, Balanced or High performance |

Before a profile is applied, a review dialog lists each tweak as **Will apply**, **Already set** or **Skipped** (with the reason), along with the power plan change. In administrator mode a restore point is created first. *Revert* restores every setting the profile changed, plus the previous power plan.
