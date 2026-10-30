# AnovaDesk engineering notes

A durable record of how this project was built, what was learned about the device, and what
remains unresolved. Written so a future session can pick up without replaying the original
chat, which lives in a different OpenWork workspace record.

Last updated: 2026-10-30, at v1.0.0.

Device identifiers (cooker IDs, MAC address) are deliberately omitted here. They are visible
in the app's device list and in the original session.

## Current state

| Item | Value |
| --- | --- |
| Repository | https://github.com/shadowtheimpure/AnovaDesk (private at time of writing) |
| Licence | Unlicense (public domain) for project code; `reference/` is third-party |
| Released | `v1.0.1`, assets: `AnovaDesk-1.0.1-win-x64.zip`, `SHA256SUMS.txt` |
| Workspace folder | `G:\Coding Projects\AnovaDesk` (renamed from `ReverseEngineerAnything`) |
| Binary | Unsigned self-contained single file, ~72 MB |

## Hardware

- **Anova Precision Cooker, generation A5** (class `A5`, controller `RA2L1-128`).
- Firmware **1.6.0**. An OTA to 2.2.0 was offered by the device and deliberately **not**
  applied, because its effect on protocol behaviour is unknown.
- OUI `f8:64:65` is Anova Applied Electronics, confirming the vendor.
- A dead **A3** cooker is still paired to the Anova account. It cannot be removed from the
  app; AnovaDesk greys it out and disables controls for it.

### No local control path

Port scanning the cooker found **no inbound TCP services** - no legacy websocket on 8080, no
SSDP, no mDNS, and a `PTR` lookup returns nothing. Control is therefore only possible through
Anova's cloud relay, and the device's LAN IP is irrelevant to the application.

## Protocol

All of this comes from Anova's public developer documentation plus direct observation. No
firmware was reverse-engineered.

```
wss://devices.anovaculinary.io?token=<PAT>&supportedAccessories=APC,APO
```

Authentication uses a **Personal Access Token**, created in the Anova Oven app under
More > Developer > Personal Access Tokens. Tokens start with `anova-`, are limited to 10 per
account, and connections time out after 30 minutes of inactivity.

### Messages observed

```
EVENT_APC_WIFI_LIST   [{ cookerId, name, pairedAt, type }]
EVENT_APO_WIFI_LIST   []            (oven list; empty for this account)
EVENT_USER_STATE      subscription flags
EVENT_APC_STATE       { cookerId, type, state: { ... } }
EVENT_APC_WIFI_VERSION firmware + OTA availability
```

`state` shape, as actually reported (note the hyphens and nesting):

```
state.job             mode, target-temperature, temperature-unit,
                      cook-time-seconds, id, ota-url
state.job-status      cook-time-remaining, state, job-start-systick,
                      state-change-systick
state.temperature-info water-temperature, heater-temperature, triac-temperature
state.pin-info        water-leak, water-level-critical, water-temp-too-high, device-safe
state.system-info     class, firmware-version, type
```

### Commands

```
CMD_APC_START                { cookerId, type, targetTemperature, unit: "C", timer }
CMD_APC_STOP                 { cookerId, type }
CMD_APC_SET_TEMPERATURE_UNIT { cookerId, type, unit }
```

Success replies are `RESPONSE { requestId, payload: { status: "ok" } }`.

### Units: an important detail

**All numeric temperatures in device state are Celsius.** The `temperature-unit` field is a
display preference only. This was proven empirically: `water-temperature` read 22.4 while the
unit said `F`, and 22.4 F is physically impossible for water, whereas 22.4 C is exactly room
temperature. A stored target of 57.8 therefore means 57.8 C (136 F), not 57.8 F.

Commands are sent in Celsius with `unit: "C"`, matching Anova's own reference implementation.

## Cook lifecycle (resolved)

Earlier in the project a cook would not sustain: the device acknowledged the command, briefly
reported `COOK / PREHEATING`, and fell back to `IDLE` with the timer frozen. `pin-info`
reported zero for every flag throughout, so the cause was invisible from the cloud. The owner
fixed a water-level fault on the unit, after which cooks run normally.

Verified end to end on 2026-10-30, driven through the app's own UI:

| Phase | Device state | Timer | Notification |
| --- | --- | --- | --- |
| Heating | `mode=COOK`, `jstate=PREHEATING` | full | - |
| Target reached | `jstate=COOKING`, water within 0.5 of target | counting down | "Target temperature reached" |
| Timer expired | `jstate=MAINTAINING` | `rem=0` | "Cook complete" |
| Deliberate stop | `mode=IDLE`, `rem` reset to full | - | none (by design) |

**The important protocol detail:** an Anova cooker does **not** stop when the timer expires.
It switches to `MAINTAINING` and keeps holding temperature indefinitely, so `mode` stays
`COOK`. Completion must therefore be detected from `cook-time-remaining` reaching zero while
the cook is still running, not from the cook ending. An earlier version of the app waited for
cooking to stop and so never reported completion.

**Second defect found in the same test:** pressing Stop produced a spurious "Cook stopped"
warning, because the notification rule saw the cook end with time remaining. Clearing rule
state when the button is pressed was not enough - state frames still arriving before the
device winds down re-armed it. The fix records the requested stop and consumes it on the
transition, with a two-minute expiry in case the stop never lands.

**Unit gotcha worth remembering:** `CMD_APC_START` with `unit: "C"` also switches the
device's stored display unit, and the app then adopts it. Targets must be entered in whatever
unit the app is showing; setting `90` while displaying Fahrenheit means 90 F, and while
displaying Celsius means 90 C.

## Application design

WPF on .NET 10, target `net10.0-windows10.0.19041.0`, published self-contained single-file.

| File | Responsibility |
| --- | --- |
| `AnovaClient.cs` | WebSocket transport, reconnect with backoff, idle refresh before the 30 minute timeout |
| `MainWindow.*` | UI, state rendering, alert composition |
| `CookNotificationRules.cs` | Pure, UI-free, edge-triggered notification policy |
| `ToastChannel.cs` | Toast notifications and AppUserModelID registration |
| `Notifier.cs` | Tray icon, and balloon fallback if toasts are unavailable |
| `TokenStore.cs` | Windows Credential Manager storage |
| `Settings.cs` | `%APPDATA%\AnovaDesk\settings.json` (last selected cooker only) |
| `AppRuntime.cs` | `--profile` / `--offline` isolation hooks for tests |

## Defects found and fixed (worth remembering)

1. **Balloon notifications silently dropped.** `NotifyIcon.ShowBalloonTip` returns without
   error on Windows 10 when the process has no AppUserModelID, so nothing appears. Replaced
   with real toasts.
2. **Registry-only AUMID is not enough.** Registering an arbitrary AUMID under
   `HKCU\Software\Classes\AppUserModelId` and calling `CreateToastNotifier` throws
   `0x80070490` (ERROR_NOT_FOUND). The AUMID must be the **full executable path**, which the
   Community Toolkit compat layer registers along with the required shortcut.
3. **Mojibake from shell text round-trips.** `Get-Content | Set-Content` in PowerShell 5.1
   reads BOM-less files as ANSI, permanently double-encoding UTF-8 characters. Every C# string
   literal now uses `\u` escapes and the sources are ASCII. Do not rewrite source files
   through the shell.
4. **`ShowDialog`/`Close` inside `OnClosing`** throws "Cannot ... while a Window is closing".
   The close prompt and its follow-up actions are deferred with `Dispatcher.BeginInvoke`.
5. **`ScrollIntoView` on a collapsed list** throws; guarded on panel visibility.
6. **Preset labels did not follow the unit toggle**, and the unit adopted from the device
   bypassed the change handler entirely, so labels could stay in Celsius on a Fahrenheit
   device. Fixed by rendering captions from the stored Celsius value.

## Verification

| Tool | Covers |
| --- | --- |
| `tools/UiSmoke` | Six UI Automation scenarios: no-token, forget token, advanced toggle, close cancel/minimize/exit |
| `tools/CookRuleCheck` | 35 assertions over notification rules and formatting |
| `tools/NotifyCheck` | Toast delivery, proven by Action Center history count |
| `tools/AnovaProbe` | Raw frame dump; `--cycle` sends start/stop and prints responses |

`UiSmoke` runs against `--profile smoke --offline`, so it never touches the real token and
never calls Anova's servers. It also aborts if the target build ignores `--profile`, because
the "forget token" scenario would otherwise delete a real credential.

The notification cycle was proven end to end: Action Center history went 0 -> 1 for the
published executable's AUMID.

## Build and release

```powershell
dotnet build AnovaDesk.sln -c Release

dotnet publish AnovaDesk\AnovaDesk.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none `
  -o release\AnovaDesk-1.0.0-win-x64
```

Release assets are attached with `gh release create`; `gh` is installed per-user at
`%LOCALAPPDATA%\Programs\gh\bin`. Known caveat: the cached GitHub credential is a classic PAT
with `gist, repo, workflow` scopes, which `gh auth login` rejects for missing `read:org`, so
`GH_TOKEN` is used instead of a gh login.

## Open items

- Cooks run correctly since the water-level fault was fixed. The completion and stop
  notification defects found during that test are fixed in v1.0.1.
- Precision Ovens are listed but their commands are not implemented.
- The binary is unsigned, so SmartScreen warns on first run.
- The `v1.0.0` tag points one commit behind `main`; the difference is cosmetic (README only).
- The empty `G:\Coding Projects\ReverseEngineerAnything` folder remains and can be deleted
  once OpenWork is restarted and releases it.

## Licensing notes

`reference/anova_interactive_reference.py` is Anova's own reference implementation. Anova's
documentation calls it MIT, but the repository contains **no LICENSE file and no copyright
line**, so `reference/NOTICE.md` records that gap rather than inventing one. Deleting that
directory removes all third-party material; nothing depends on it at build or run time.
