# Dynamic Notch

A notch overlay for Windows 10 (1809+) and Windows 11 with a music player, a clipboard and shelf page, an "Ask Claude" page that can look at part of your screen, and notification pills.
Built with C# / .NET 8 / WinUI 3 (Windows App SDK 1.8, self-contained). The notch shape, the morph and every animation run in the system compositor, so motion holds the display's refresh rate (about 56–59 fps on a 60 Hz screen) and the app does no per-frame work. The first version was WPF; its see-through windows capped animations at about 20–25 fps, so it was ported (see `docs/PORTING-PLAN.md`). The WPF source is kept in `archive/DynamicNotch.Wpf` for reference and isn't built.

## Download

1. Open the [Releases](../../releases) page and download the zip for your PC: `win-x64` (most PCs) or `win-ARM64` (Snapdragon / ARM laptops).
2. Unzip it anywhere and run `DynamicNotch.exe`. Nothing else needs installing.
3. Windows SmartScreen may warn that the app is unrecognised (it isn't code-signed). Click **More info ▸ Run anyway**.
4. For **Ask Claude**, paste your own Claude API key in Settings. It's encrypted and stored only on your PC.

## Run it from source

| Goal | Command | Notes |
|---|---|---|
| Quick debug run | `.\scripts\run-debug.ps1` | Everything except app-notification pills and Start with Windows; doesn't see the packaged app's settings or key |
| Debug run + samples | `.\scripts\run-debug.ps1 -Demo` | Queues one pill of each kind, sample devices and sample clipboard items |
| Packaged debug run (with app notifications) | `.\scripts\register-dev.ps1` | Needs **Developer Mode**. Re-registers over the existing package, so settings and the key are kept |
| Remove the packaged dev build | `.\scripts\register-dev.ps1 -Unregister` | Deletes the package's data; it's backed up to `artifacts\package-data-backup` first |
| Installable signed .msix | `.\scripts\make-msix.ps1 -TrustCert` | Needs the Windows SDK (see below) |

Or open `DynamicNotch.sln` in Visual Studio / VS Code and run the `DynamicNotch.WinUI` project (platform x64 or ARM64). It builds `DynamicNotch.exe`.

Dev flags (for checking the UI without a mouse): `--open [page] [delayMs]`, `--cycle`, `--nav-icons`, `--preview`, `--ask-demo [error|thinking] [focus|select]` (a canned local conversation, no request is made), `--settings [pages|focus|general]`, `--usage-demo [error]` (canned usage JSON through the real parser; no request, no token read), `--focus-demo [end]` (timer mid-round, or 6 s before a phase ends), `--toggle` (opens and closes every 1.8 s). A notch opened by a dev flag ignores outside clicks.

Quit from the tray icon (right-click ▸ Quit). The tray menu also has **Settings…**, **Minimal notch** and **Start with Windows**.

## Windows settings you may need

1. **Developer Mode** (for `register-dev.ps1`): Settings ▸ System ▸ For developers ▸ Developer Mode **On**.
2. **Notification access** (packaged builds only): Windows asks for permission on first launch. If you said no, use Settings ▸ Privacy & security ▸ Notifications ▸ *Let apps access your notifications*, or the "Enable app notifications" item in the tray or the notch.
3. **Bluetooth** must be on for headphone pills.
4. **Windows SDK** (for `make-msix.ps1` only): `winget install Microsoft.WindowsSDK.10.0.22621`.
5. Optional: Settings ▸ Accessibility ▸ Visual effects ▸ *Animation effects*. When it's off, the notch snaps instead of animating (glow pulse included).
6. **Ask Claude** needs a Claude API key (console at platform.claude.com, billed per use, separate from a Claude.ai subscription). Paste it in the notch's Settings (gear icon, or tray ▸ Settings…).

## How to use it

- **Collapsed notch:** the bold time sits at the true centre. A battery ring (green above 50%, amber 20–50%, red below 20%, with a bolt while charging) hugs the left edge. While music plays, a visualiser and small album art sit on the right. Hovering shows the battery % inside the ring.
- **Expanded header:** the ring, the bold time and a lighter date. The ring, time and album art glide between the collapsed and expanded positions as the notch morphs. Times follow the Windows 12/24-hour setting and update exactly on the minute.
- **Connected devices** (bottom-right when open): one icon per connected Bluetooth device (headphones, speaker, mouse, keyboard, controller, phone, watch or generic), each with a battery ring. Hover an icon to see its %.
- **Hover** the notch to expand it. Move away and it collapses after a short delay.
- **Keyboard, no click needed:** while the notch is open or the mouse is over it, **← / →** switch pages and **Space** plays/pauses (music page, while hovered). Keys with Ctrl/Alt/Shift/Win held always go to your app. When the notch is closed and not hovered, every key goes straight to your app untouched. The notch never takes focus.
- **Click** the notch to pin it open. **Esc** or a click anywhere else closes it.
- **Settings** (gear, top right when open, or tray ▸ Settings…): a small window with a tab list. **Claude API key**: paste, show/hide, Save, Remove key and Test connection. After saving, only the last 4 characters are ever shown. **Pages**: see below.
- **Page 1, Music:** follows the active player (Spotify, Apple Music, YouTube in a browser, etc.). Click the album art to cycle between players when several are running. Drag the progress bar to seek.
- **Page 2, Clipboard + Shelf:** clipboard items are compact tiles: images in full, text as its first characters, links as an icon. Hover a tile to zoom it (its neighbours move aside) and reveal pin/delete. Click to copy it back, or drag it into another app. Up to 10 recent + 5 pinned items. Drag files from Explorer onto the notch: it springs open to the shelf, and the dashed drop zone lights up and starts moving. Click a tile to open the file, drag it out to share it, or use × / Clear all. Hover a clipboard tile for ~0.2 s and a large preview card springs out just right of the cursor (it flips to the left near the notch's right edge) and follows the mouse: the whole image, the text at a readable size (up to 400 characters), or the full link. It never takes the mouse or focus.
- **Music glow:** a soft glow rises from the bottom of the music page in the album art's most vibrant colour, cross-fades between tracks, breathes gently while playing and dims when paused.
- **Page 3, Ask Claude:** press **Ctrl+Shift+Space** from anywhere (or arrow over to the page and click the box). Type a question and press **Enter** (Shift+Enter for a new line) or **Send**, or click **Select area**, drag over part of the screen (Enter = whole monitor, Esc = cancel) and ask about it. With a screenshot attached and an empty box, Send asks "What's on my screen?". The answer streams in with light formatting (bold, code, lists, code blocks); ask follow-ups below it, select any part of the answer and press **Ctrl+C**, or press **New chat** to start over. Stop cancels a request. **Esc** or the hotkey again closes the notch and puts focus back where you were. The conversation lives in memory only.

- **Page 4, Claude Usage:** your Claude plan's 5-hour session and weekly limits as gradient rings (blue/purple under 60%, amber 60–85%, red above 85%), each with the reset as a countdown and a local clock time, plus a slimmer row per model-specific weekly limit when the data has one. Fetched once at startup, then every 60 s only while the page is open, with back-off on errors and 429s. It uses an **unofficial, undocumented** endpoint (`GET https://api.anthropic.com/api/oauth/usage`) with the access token from Claude Code's `%USERPROFILE%\.claude\.credentials.json` (`claudeAiOauth.accessToken`). The token is only read into memory per request, never logged or stored, and only sent to api.anthropic.com (redirects are disabled). Any failure shows "Usage unavailable" with a short reason.
- **Page 5, Focus Timer:** a Pomodoro round (default Focus 25 · Short 5, three times, then Focus 25 · Long 15). Left: the phase, a large rolling countdown, the clock time it ends ("10:27 until done"), a gradient progress bar, Pause/Resume and Reset. Right: "This round", one segment per interval (short breaks are dots) with a glowing dot on the live one. Time is kept with timestamps, so it stays exact while hidden, collapsed or asleep, and the state survives restarts (`focus-timer.json`). While it runs, the collapsed notch shows the remaining time on the right (instead of the music bits). At each phase end: a soft chime (optional), a pill if you aren't looking at the page, and the next phase starts by itself or waits for Start. The round stops after its last interval.
- **Settings ▸ Focus Timer:** edit the routine (type chip: click to cycle Focus / Short break / Long break; minutes 1–180 with − / +; move up/down; delete; add any type), Reset to defaults, the auto-start and sound switches, and the live total. Edits save instantly; while a round is running they apply at the next phase.
- **Settings ▸ General:** *Hide notch when an app is fullscreen* (on by default). The notch fades out and its window hides while the foreground app on the notch's monitor is fullscreen (`SHQueryUserNotificationState` busy / D3D fullscreen / presentation mode, plus the window covering its whole monitor). A foreground hook and a 750 ms poll bring it back as soon as that ends, also if the app crashes.
- **Settings ▸ Pages:** switch the Music Player, Clipboard + Shelf, Ask Claude, Claude Usage and Focus Timer pages on or off. Changes apply instantly and are saved. A switched-off page leaves the arrow-key navigation and the page dots, and its background work stops: Music stops listening to media sessions (no glow, no tiny album art or visualiser on the collapsed notch), Clipboard + Shelf stops the clipboard listener and the file drop target, and Ask releases Ctrl+Shift+Space. At least one page always stays on.
- **Claude logo:** put the official logo file at `src/DynamicNotch.WinUI/Assets/claude-logo.svg` (or `claude-logo.png`) and rebuild (or run `.\scripts\register-dev.ps1` for the packaged build). It shows next to "Ask Claude", in the "Claude is thinking…" line and in Settings ▸ Pages, tinted white if it's a one-colour mark. Without the file, a sparkle icon is shown. The app never draws the logo itself.

- **Page navigator:** the dots at the bottom of the open notch turn into page icons when you hover them (the same icons as Settings ▸ Pages; the current page is pink). Click an icon to jump to that page. Hidden when only one page is on.
- **Ask conversation:** each exchange shows your question in a light "You" card (with its screenshot) and Claude's answer below it. Follow-ups stack underneath, older ones slightly dimmed; scroll up to see them. Hover a screenshot thumbnail to see it larger.

## Project layout

```
src/DynamicNotch.WinUI/
  Theme/Theme.cs        ONE corner radius (22), colours, fonts (Inter), spacing, sizes
  Theme/Motion.cs       spring presets (Notch, Page, Press, Pop, Hover + supporting) and timings
  Theme/Icons.cs        the one icon set (Lucide path data) + vector icon drawing
  Theme/Styles.cs       text styles built from Theme.cs and published as app resources
  Assets/Fonts/         Inter Regular/Medium/SemiBold/Bold + licence
  Animation/            SpringModel (closed-form spring as a function of time), CompSpring (a spring baked
                        into a compositor keyframe animation), Anim (expression helpers), Motions (press,
                        hover fade, pop in/out, staggered entrances)
  Interop/              Native (window styles, see-through popup, region, monitors, capture exclusion),
                        HiddenWindow (hotkey + system broadcasts), ShellIcon
  Services/             Media, Clipboard, Shelf, Network(Wi-Fi), Bluetooth, Vpn, Power, Battery, Clock,
                        KeyboardHook, Notification, Settings, Startup, Tray, Claude (streaming API client),
                        SecretStore (DPAPI key file), ScreenCapture, AlbumColor, ImageLoader, PageRegistry
  ViewModels/           Main (pages, pill queue), Music, Clipboard, Shelf, Status, Devices, Ask, Settings
  Views/                NotchShape, ExpandedFrame, MusicPage, ClipboardShelfPage, PreviewCard, AskPage,
                        MarkdownView, ImageThumb, SettingsWindow, RegionSelectorWindow, SeekBar, Visualizer,
                        AlbumGlow, BatteryRing, DevicesRow, PageNavigator, PillSwitch, Buttons, InputStyle,
                        CrossfadeText, VectorIcon, ClaudeLogo (asset loader)
  AppConfig.cs          hotkey, Claude model / API constants, screenshot limits
  NotchWindow.*.cs      overlay window: state machine (xaml.cs), open notch + flights (Expanded),
                        clipboard preview + drop target (Clipboard), Ask focus / hotkey / Select area (Ask)
Package/                AppxManifest.xml + logos (MSIX identity, startup task, notification capability)
scripts/                run-debug, register-dev, make-msix, generate-assets
prototypes/             CompositionNotch (the compositor proof of concept), FrameMeter (frame-rate meter)
archive/DynamicNotch.Wpf/  the retired WPF app (not built)
```

Settings, the shelf list and other data live in `%LocalAppData%\DynamicNotch` (packaged builds get a per-package virtualised copy). There's no telemetry. The app only talks to `api.anthropic.com`: the Claude API request, made only when you press Send in Ask (or Test connection in Settings), and the Claude Usage request (once at startup, then every 60 s only while that page is open; switch the page off to stop it). Your API key is stored encrypted for your Windows user (DPAPI) in `%LocalAppData%\DynamicNotch\claude.key` and is sent nowhere except the `x-api-key` header of those requests. Screenshots and conversations are never written to disk or logged.

## Known limits / not fully implemented

- **Screenshot hover preview** fits inside the notch, so while the notch is short (the empty Ask box) the image is shown smaller than its 420×280 maximum.
- **Music page height** is 14 px shorter than the other pages (`Theme.MusicExpandedHeight`). The footer follows the notch's bottom edge with a transform as the height changes between pages.
- **Radius rules:** one global radius (22) for shapes, with two deliberate exceptions. Pills (buttons, badges, switches) take a radius of exactly half their height, computed from their actual height. `Theme.RadiusSmall` (11) is used only for the Ask screenshot thumbnail, clipboard tiles (and their pin / delete buttons) and the clipboard preview card.
- **Preview card position** follows the cursor but stays inside the notch (it's drawn inside the notch shape), so near the bottom of the clipboard area it sits higher than "slightly below the cursor".
- **Logo loader:** reads the SVG's `<path d>` elements and its viewBox, tinted with one colour. Other SVG shapes, gradients, masks and transforms aren't supported; use the PNG in that case.
- **Page icons** in Settings use the app's own icon set (Lucide), not Fluent, so they match every other icon in the notch.
- **Ask Claude:**
  - It needs an internet connection and your own API key (billed per use). The model is `claude-haiku-5-5` (one constant in `AppConfig.cs`), with `max_tokens` 1024. Haiku 5.5 thinks by default and that thinking counts toward `max_tokens`, so requests also send `output_config.effort: "low"` to keep answers quick and complete.
  - The header is `anthropic-version: 2023-06-01`, as specified. The newer `2026-10-21` version also works with these requests, since they send no `temperature`/`top_p`/`top_k`.
  - The notch shows the latest question and answer; earlier turns stay in memory as context (the last 6 messages are sent). A screenshot is sent only with the message it was taken for, while that message is among the last 6.
  - After an error, use **Retry**, or the **New chat** icon to ask something else. Stop keeps whatever part of the answer already arrived.
  - Formatting is a light Markdown subset: paragraphs, bold, inline code, bullet and numbered lists, headings and fenced code blocks. No tables, links or italics.
- **Screen capture** uses DPI-aware GDI capture (`BitBlt`), not Windows.Graphics.Capture, and the WinRT encoder for the JPEG. The notch is kept out of the image with `WDA_EXCLUDEFROMCAPTURE` (Windows 10 2004+; on older Windows it may appear in the capture). DRM-protected video captures as black.
- **Region selection:** each monitor gets its own overlay, so a selection can't span two monitors. Enter captures the monitor under the cursor.
- **Ctrl+Shift+Space:** if another app already owns that shortcut, Windows refuses it and only the arrow / click route opens Ask. Change it in `AppConfig.cs`.
- **Focus:** the notch takes keyboard focus only when you invoke Ask (hotkey, or a click into its box) and returns it when it closes. If you click into another window instead, that window keeps focus.
- **Preview card** fits inside the notch at the current sizes, so the notch doesn't need to grow for it. Long text is capped at 400 characters.
- **Glow pulse** is a looping compositor animation, only while music plays and the music page is open.
- **Global keys** use a low-level keyboard hook (`WH_KEYBOARD_LL`) on its own thread. It needs no setting or permission. It only swallows ← / → while the notch is open or hovered, Space while you hover the music page, and Esc while pinned, and never with Ctrl/Alt/Shift/Win held. While an elevated (admin) window has focus, Windows may not pass keys to an unelevated hook, so use the clickable page dots there. Some anti-cheat games block low-level hooks.
- **Pills wait while the notch is expanded.** They show once it collapses.
- **The visualiser is simulated.** It shows spring-animated bars while playing, not real audio levels. Real levels need WASAPI loopback capture, which costs constant CPU.
- **App-notification events:** `UserNotificationListener.NotificationChanged` isn't delivered to every desktop app. When subscribing fails, the app checks for new toasts every 1.5 s, and only while it has permission.
- **Fullscreen hiding** follows the foreground window: a fullscreen app that isn't in front (e.g. a video on the primary monitor while you work on a second one) doesn't hide the notch. The notch only lives on the primary monitor, so fullscreen apps on other monitors are ignored.
- **Claude Usage** depends on an unofficial endpoint. The parser was written for the expected shape (`five_hour` / `seven_day` / `seven_day_sonnet` or `_opus` objects with `utilization` in % and `resets_at`) and checks every field. The app never refreshes the token: if it expires, run Claude Code once.
- **Exclusive full-screen games** draw above every overlay. Borderless and windowed full-screen apps are fine, because the notch re-asserts topmost whenever the foreground window changes.
- **Bluetooth battery %** comes from `DEVPKEY_Bluetooth_Battery` (Classic devices using the hands-free profile, most earbuds and headsets). For LE devices without it, the app reads the GATT Battery Service and subscribes to its updates. Devices that expose neither show an icon with no ring. That's common for older speakers, some mice/keyboards behind vendor dongles, and phones. Devices connected through a USB dongle (Logitech Unifying/Bolt, Xbox Wireless) aren't Bluetooth to Windows, so they don't appear at all.
- **Device type** comes from Bluetooth Class-of-Device (Classic) or Appearance (LE), with the device name as a fallback. A device that reports neither shows the generic Bluetooth icon.
- **Collapsed battery %:** the notch starts expanding about 60 ms after you hover, so the % in the collapsed ring is only visible briefly. It stays visible in the expanded header's ring.
- **Frame rate:** measured with `prototypes/FrameMeter` on a 2256×1504 @150% 60 Hz screen: open / close morph 52–59 fps, page slides 56–59 fps, 0 at rest. Every spring is baked into a compositor animation, so frames keep coming even when the app is busy.
- **No blur on transitions** (the WPF app blurred content as it faded in and out and while pages slid). A compositor backdrop blur was built and tried, but on this laptop it cost 5–15 fps on page slides and dipped the opening morph, so it was removed: content fades and moves without blur.
- **Bottom corners** are continuous ("squircle") corners, as in the WPF app. The compositor can't compute a path from an expression, so on every size change the outline is sampled along the width and height springs into a path keyframe animation (Win2D builds the paths; the compositor plays them).
- **CPU:** at rest the app uses about 0.02–0.05 s of CPU per 10 s. The visualiser is a pre-simulated looping compositor animation, so it costs no CPU while music plays. Set `DYNAMICNOTCH_PROFILE=1` to log the music-page centring check to `%LocalAppData%\DynamicNotch\frames.log`.
- **`--demo`** also adds four sample devices to the devices row, for checking the layout without hardware.
- **Wi-Fi SSID:** Windows 11 24H2 hides SSIDs from apps without location consent. The app then shows the network profile name, which is normally the SSID.
- **VPN detection** treats PPP/RAS adapters (built-in Windows VPN) and adapters with known VPN driver names (WireGuard, OpenVPN/TAP, Wintun, AnyConnect, Fortinet, GlobalProtect, Tailscale…) as VPNs. An unusual VPN client may need its name added in `VpnService.Markers`.
- **Clipboard history is memory-only** (pinned items too), so copied passwords and private text never touch disk. Content that password managers mark as private is skipped.
- **Minimal notch:** at 6 px tall, the corner radius is clamped by the shape's height (it can't be larger than half the height).
- **Motion** only ever animates compositor properties (offset / translation, scale, opacity, clips, shape geometry), never XAML layout, per frame.
- Memory use is about 140 MB private (about 40 MB of it is the graphics device Win2D needs to build the corner paths).

## Credits

- [Inter](https://rsms.me/inter/) by Rasmus Andersson, SIL Open Font Licence 1.1 (`src/DynamicNotch.WinUI/Assets/Fonts/Inter-LICENSE.txt`).
- Icons: path data from [Lucide](https://lucide.dev) (ISC licence), embedded in `Theme/Icons.cs`.

