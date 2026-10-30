# AnovaDesk

A Windows desktop app for controlling Anova Wi-Fi Precision Cookers.

Anova ships iOS and Android apps but no Windows client, so this project drives the
cooker through Anova's own documented developer WebSocket API. Nothing here
reverse-engineers the device firmware: the protocol is implemented from Anova's
public developer documentation.

## Features

- Device discovery for every cooker paired to your Anova account
- Live water, heater and triac temperatures with current job state
- Start a cook with target temperature and timer, and stop it again
- Celsius / Fahrenheit toggle, with preset temperatures that follow the unit
- Windows toast notifications for: target temperature reached, cook complete,
  cook stopped early, and cook failed to start
- Safety flags surfaced from the device (water leak, level, over-temperature, fault)
- Tray icon with close-to-tray option, so a cook keeps being monitored
- Technical panels (raw device state and websocket traffic) behind an
  "Advanced view" toggle that is off by default

The access token is stored in Windows Credential Manager and is never written to
a file in this repository.

## System requirements

| Requirement | Detail |
| --- | --- |
| Operating system | Windows 10 version 2004 (build 19041) or newer, or Windows 11 |
| Architecture | x64 |
| .NET runtime | **Not required** - the release build is self-contained |
| Network | Outbound HTTPS/WSS to `devices.anovaculinary.io` (port 443) |
| Account | An Anova account with the cooker paired and online |
| Notifications | Windows notifications enabled for the app (Settings > System > Notifications) |

Windows 10 version 2004 is the minimum because the project targets
`net10.0-windows10.0.19041.0` for the Windows Runtime APIs used by toast
notifications.

## Getting started

1. Install the **Anova Oven** app on your phone and sign in. It is the only app
   that can create tokens, and it works for sous vide cookers too.
2. In that app open **More > Developer > Personal Access Tokens**.
3. Tap **Create**, give the token a name, and copy it. It starts with `anova-`.
4. Run `AnovaDesk.exe`, paste the token, and press **Connect**.

The app shows these same instructions the first time it starts, and again
whenever no token is registered.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

`AnovaDesk.sln` includes the app and all four tools, so a single build covers everything:

```powershell
dotnet build AnovaDesk.sln -c Release
```

To build just the application:

```powershell
dotnet build AnovaDesk\AnovaDesk.csproj -c Release
```

To produce the self-contained single-file executable:

```powershell
dotnet publish AnovaDesk\AnovaDesk.csproj -c Release -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none `
  -o release\AnovaDesk-1.0.0-win-x64
```

## Repository layout

```
AnovaDesk.sln       solution covering the app and all four tools
AnovaDesk\          the WPF application
  AnovaClient.cs      websocket transport and command payloads
  MainWindow.*        UI and state rendering
  CookNotificationRules.cs  edge-triggered notification policy (UI-free)
  ToastChannel.cs     toast notifications and AppUserModelID registration
  Notifier.cs         tray icon and fallback notification path
  TokenStore.cs       Windows Credential Manager storage
tools\              diagnostic and test harnesses (see below)
docs\               engineering notes, protocol findings, open issues
reference\          third-party reference material, see reference/NOTICE.md
```

## Diagnostic tools

| Tool | Purpose |
| --- | --- |
| `tools\AnovaProbe` | Dumps raw websocket frames. `--cycle` sends a start/stop pair and prints every response. |
| `tools\NotifyCheck` | Posts a toast and reports whether it reached Action Center. |
| `tools\CookRuleCheck` | 35 assertions over the notification rules and formatting helpers. |
| `tools\UiSmoke` | UI Automation smoke test: six scenarios driving the real window. |

Run them from the repository root:

```powershell
dotnet run --project tools\CookRuleCheck
dotnet run --project tools\UiSmoke
dotnet run --project tools\UiSmoke -- --scenario advanced
```

`UiSmoke` launches the app with `--profile smoke --offline`, which redirects the
credential to an isolated entry and disables the network connection, so your real
token is never touched. It also aborts rather than running if it detects that the
target build ignores `--profile` - otherwise the "forget token" scenario could
delete a real token.

## Known limitations

- **Precision Ovens are not implemented.** Oven devices are listed but their
  commands are not sent.
- **Device faults still block cooking.** The app reports aborts and surfaces the safety
  flags, but it cannot work around a hardware fault such as a low water level.
- **Token scope is limited.** Personal access tokens expose a subset of commands
  and are rate limited by Anova; websocket connections also time out after
  30 minutes of inactivity, which the app works around by refreshing the session.
- **The release binary is unsigned**, so Windows SmartScreen warns on first run.
- **Debug symbols are not shipped** with the release build.

## AI usage disclosure

This project was built with substantial AI assistance, and it seems worth saying so plainly.

**What was AI-assisted.** The application source, the four test harnesses, the packaging and
release steps, and this README were written by an AI coding agent (OpenWork, running
DeepSeek V4.1 Flash) working in a single session.

**What a human did.** The repository owner directed the work, supplied the physical cooker
and its network address, created the Anova personal access token, ran the app against real
hardware, visually confirmed the window and notifications (the agent has no screen access),
and reported the defects that drove several rounds of fixes, including two bugs in the close
handler and a text-encoding regression.

**What was verified automatically.** The harnesses in `tools\` were run during development:
35 assertions over notification rules and formatting (`CookRuleCheck`), six UI Automation
scenarios driving the real window (`UiSmoke`), toast delivery confirmed through Action
Center (`NotifyCheck`), and live protocol capture against a real cooker (`AnovaProbe`).

**What was verified on real hardware.** Once the cooker's water-level fault was fixed by the
owner, a full cook was driven through the app and observed end to end: `PREHEATING` ->
`COOKING` with the timer counting down -> `MAINTAINING` after the timer expired, with both the
"target reached" and "cook complete" notifications delivered to Action Center, and a
deliberate stop confirmed to raise no notification.

**What is still unverified.** Oven support, unusual failure modes, and behaviour on cooker
generations other than the A5 used here are untested.

**Ownership.** The repository owner has dedicated this code to the public domain under the
Unlicense (see `LICENSE`). That also sidesteps the question of whether AI-generated output is
copyrightable, which varies by jurisdiction.

## Attribution and trademarks

This project is built on Anova's public documentation:

- [Anova Developer Documentation](https://developer.anovaculinary.com/docs/intro)
- [Wi-Fi device overview](https://developer.anovaculinary.com/docs/devices/wifi/overview)
- [API authentication](https://developer.anovaculinary.com/docs/devices/wifi/authentication)
- [Precision Cooker commands](https://developer.anovaculinary.com/docs/devices/wifi/sous-vide-commands)
- [Device discovery](https://developer.anovaculinary.com/docs/devices/wifi/device-discovery)

`reference\anova_interactive_reference.py` is Anova's own reference
implementation, retained for protocol reference. See
[reference/NOTICE.md](reference/NOTICE.md) for its origin and licensing status.

Anova, Anova Culinary, Anova Precision Cooker, Anova Precision Oven and the Anova
logo are trademarks or registered trademarks of Anova Applied Electronics, Inc.

This project is **not affiliated with, endorsed by, sponsored by, or supported by
Anova Applied Electronics, Inc.** Device names and trademarks are used only to
describe interoperability.

## License

The source code in this repository is released into the public domain under the
[Unlicense](LICENSE). The contents of `reference\` are third-party material and
are excluded from that dedication; see [reference/NOTICE.md](reference/NOTICE.md).
