# Porting plan: Dynamic Notch → WinUI 3 on the native compositor (60 fps)

Written for a fresh session. You don't need the previous conversation, just this file, the README and the code.

## Why

The current app is WPF. A see-through WPF window (`AllowsTransparency`) is copied from the GPU to the CPU on every frame, which caps animations at about **20–25 fps** on this laptop (2256×1504 @150%, 60 Hz). The app's own per-frame code takes only about 0.2 ms, so the window type is the bottleneck.

`prototypes/CompositionNotch` showed that a window drawn by `Windows.UI.Composition` (Win32 popup with `WS_EX_NOREDIRECTIONBITMAP`) works:

| Measured with `prototypes/FrameMeter` | WPF (current) | Composition prototype |
|---|---|---|
| Open morph | ~20–24 fps | ~57–59 fps |
| Close morph | ~23–26 fps | ~57–58 fps |
| At rest | 0 | 0 |

It's see-through and topmost, never takes focus, lets clicks through outside the notch (window region = notch bounds), and clips its content to the animated shape. Springs run in the compositor (`SpringVector2NaturalMotionAnimation`), so frames keep coming even when the app is busy.

## Target

- **WinUI 3 (Windows App SDK)**, unpackaged-capable and MSIX-packaged, .NET 8, min Windows 10 1809.
- **The notch window:** a WinUI `Window` restyled as a borderless popup. Its HWND gets `WS_EX_NOREDIRECTIONBITMAP | TOOLWINDOW | NOACTIVATE | TOPMOST`, the backdrop is transparent, and the window region follows the notch bounds for click-through, as in the prototype. If WinUI's own window can't be made fully transparent, **fallback:** host the WinUI content in a `DesktopWindowXamlSource` inside the prototype's Win32 window.
- **The notch shape and the morph:** a composition `ShapeVisual` (`CompositionPathGeometry` with the concave shoulders, or a rounded rect pushed up by R as in the prototype). Size and offset springs run in the compositor, and the content is clipped with `CompositionGeometricClip` sharing the same geometry.
- **Content:** WinUI XAML on top of the shape. Per-frame motion uses composition implicit / natural-motion animations on `ElementCompositionPreview.GetElementVisual(...)` (Offset, Scale, Opacity), never layout.

## What carries over unchanged (copy as-is)

- `Services/*`: Media, Clipboard, Shelf, Network, VPN, Bluetooth, Power, Battery, Clock, Notification, Startup, Settings, SecretStore (DPAPI), ClaudeService (streaming), ScreenCapture, AlbumColor, PageRegistry, KeyboardHook.
- `ViewModels/*` (they use `INotifyPropertyChanged` and `ICommand`, which WinUI supports). Replace WPF `Dispatcher` with `DispatcherQueue`, and `System.Windows.Media.Imaging.BitmapSource` with WinUI `BitmapImage` / `SoftwareBitmapSource`.
- `AppConfig.cs`, `Theme/Theme.cs` values, `Theme/Motion.cs` values, `Theme/Icons.cs` path data, and the Inter fonts.
- The behaviour rules, all documented in the README: keyboard hook rules, focus rules, page registry, radius rules, the one network call, key storage.

## What gets rewritten

| WPF piece | WinUI replacement |
|---|---|
| `NotchWindow.xaml(.cs)` state machine | Same logic; the morph becomes compositor springs and clips become `GeometricClip` |
| `Animation/Spring*`, `MotionBehaviors`, `Stagger`, `BlurFx` | Composition `NaturalMotion` / `ImplicitAnimation`; blur via `GaussianBlurEffect` (Win2D) on a `CompositionEffectBrush` / `CompositionBackdropBrush` |
| `Theme/Styles.xaml` | WinUI ResourceDictionary (same tokens); pill radius = height / 2 via a converter or `CornerRadius` binding |
| Custom controls: `NotchShape`, `BatteryRing`, `VectorIcon`, `PageNavigator`, `PillSwitch`, `SeekBar`, `Visualizer`, `ReflowPanel`, `ImageThumb`, `ClaudeLogo`, `CrossfadeText` | WinUI controls / Win2D `CanvasControl` drawing, or `PathIcon` for icons |
| `MarkdownView` (RichTextBox) | `RichTextBlock` (selectable, `IsTextSelectionEnabled`) with the same subset |
| `MusicPage`, `ClipboardShelfPage`, `AskPage`, `ClipboardPreviewCard` | WinUI XAML, same layouts and tokens |
| `SettingsWindow` | WinUI window with a `ListView` tab list and a `ToggleSwitch` restyled as the pill switch |
| `RegionSelectorWindow` | Win32 popup per monitor (keep as WPF/Win32 if simpler; it isn't animated) |
| `TrayService` (WinForms NotifyIcon) | Keep WinForms NotifyIcon or use H.NotifyIcon.WinUI |

## Stages (each one ends with a working app; stop after any stage)

1. **Shell + collapsed notch + pills.** WinUI project `src/DynamicNotch.WinUI`, transparent click-through window, compositor-driven notch shape with shoulders, collapsed content (battery ring, centred time, mini art + visualiser), notification pills, tray, single instance, hotkey and keyboard hook wiring.
   *Done when:* FrameMeter shows ≥55 fps on open and close, clicks pass through outside the notch, focus is never taken, and idle CPU is about 0%.
2. **Expanded frame + Music page.** Header (ring, time, date, gear), page navigator (dots ⇄ icons), music page with album glow (compositor radial brush + scale/opacity pulse), seek bar, transport, details centred on the cover (log the midpoint check as now).
   *Done when:* parity screenshots match the WPF app and page slides measure ≥55 fps.
3. **Clipboard + Shelf.** Tiles (RadiusSmall), hover lift, preview card following the cursor with a flip, drag-out, shelf drop target with a dashed zone.
4. **Ask.** Idle box, Select area (reuse the region selector + capture service), thumbnails and their hover preview, turn-based conversation (question card / answer block), streaming with fade-in, thinking pulse, errors, New chat, focus take / restore rules.
5. **Settings.** Tabs with the sliding highlight, API key tab (save / remove / paste / test), Pages tab with pill switches and the keep-one-on rule.
6. **Packaging + cleanup.** MSIX manifest (runFullTrust, userNotificationListener, bluetooth, startup task), `register-dev.ps1` pointing at the new project, README update, then retire the WPF project.

## Rules to keep (from the specs)

- One global radius (22); `RadiusSmall` (11) only for the Ask thumbnail, clipboard tiles and their overlay buttons, and the preview card; pills = height / 2.
- Springs on everything, interruptible, honour Windows "Animation effects".
- Animate only transforms, opacity, clips and brush colours, never layout, per frame.
- The only network call is the Claude API on Send / Test connection. The key is DPAPI-encrypted, never logged, never sent elsewhere.
- Never take focus except via the Ask hotkey or a click into an Ask text box; restore focus on close. Arrow keys pass through untouched unless the notch is open or hovered, and always while typing.
- Disabled pages leave navigation, dots and background work (PageRegistry).

## Testing notes

- **Frame rate:** `prototypes/FrameMeter` (pixel-change count through the notch centre; only moves the cursor). Pause the other notch while measuring so they don't overlap.
- **Before clicking anything during a test,** check that the window under the cursor is one of the app's own windows. Never type unless the notch has focus; the user may be working.

## Estimate

About the size of everything built so far: 2–4 long sessions, done one stage per session. Stage 1 is the riskiest (transparent click-through WinUI window); if the WinUI window can't do it, use the prototype's Win32 window with `DesktopWindowXamlSource` and continue.

## Stage 1 status (done, 2026-11-11)

Project: `src/DynamicNotch.WinUI` (in `DynamicNotch.sln`). Run it with `.\scripts\run-winui.ps1` (`-Demo` for sample pills). It builds as `DynamicNotch.WinUI.exe` with its own single-instance lock, so it can sit beside the WPF app. Quit one of them before comparing. Windows App SDK 1.8, unpackaged and self-contained (no runtime install). MSIX is stage 6.

**Measured** with `prototypes/FrameMeter` (WPF notch stopped), 150% scale, 60 Hz:

| | WPF | WinUI stage 1 |
|---|---|---|
| Open morph | ~20–24 fps | 52–58 fps (mostly 54–58) |
| Close morph | ~23–26 fps | 53–59 fps with `FrameMeter 4 500` (the default 460 ms wait starts counting before the collapse begins) |
| At rest | 0 | 0 |
| Idle CPU | | 0.000 s CPU over 10 s |
| Private memory | ~180 MB | ~99 MB |

Clicks pass through everywhere outside the notch (checked with `WindowFromPoint` around and below it). Clicking the notch left the foreground window unchanged.

**How it works (read before stage 2):**
- **See-through window:** a WinUI `Window` with a transparent `SystemBackdrop`, `DwmExtendFrameIntoClientArea(-1)` and `Root.Background="Transparent"`. The backdrop brush comes from the *system* compositor, which needs a `Windows.System` dispatcher queue. Without the transparent root background, the island paints the dark theme fill. The style is set to `WS_POPUP` (keep `WS_VISIBLE`) and re-applied after `AppWindow.Show`, because showing restores the presenter frame. Click-through comes from `SetWindowRgn`, and the glass is re-extended after every region change.
- **Never subclass the WinUI window** (`SetWindowLongPtr(GWLP_WNDPROC)`): all XAML pointer input stops. The hotkey and display/settings broadcasts go to `Interop/HiddenWindow`. `WS_EX_NOACTIVATE` alone keeps clicks from activating the notch.
- **x:Bind in a Window** doesn't start by itself: `ShowNotch` calls `Bindings.Update()`.
- **Springs:** `Animation/SpringModel` is the WPF closed-form spring as a pure function of time. `Animation/CompSpring` bakes each retarget into a compositor keyframe animation (60 keys/s) on a property set. The notch geometry, clips and content transforms follow it through expressions, so the motion and `Motion.cs` presets match the WPF app, with no per-frame app code. The app still knows every value (`Value`, `TimeWhen`, `MaxFrom`). It uses that to start content at the same morph thresholds (one timer, not a frame loop) and to size the window region to the largest the shape will get, including overshoot.
- **Shape:** `Views/NotchShape`. The body is a rounded rect pushed up by R. The shoulders are ring strokes clipped to a box and scaled by min(R, H/2). The inner highlight is a stroke with an absolute-mapped gradient, clipped to the body. The shadow is a `DropShadow` masked by a `CompositionVisualSurface` of the body (a `LayerVisual` shadow renders unscaled inside XAML). Content is clipped with a `CompositionGeometricClip` on the same geometry.
- **Hover:** WinUI doesn't raise `PointerExited` when the cursor jumps out of the window, and then won't raise `PointerEntered` again. While the pointer is over the notch (only then), a 50 ms check of the real cursor raises the exit, and `PointerMoved` counts as entering. FrameMeter now sends a real `SendInput` move after each `SetCursorPos`, because WinUI ignores `SetCursorPos` alone.
- **Visualiser:** the WPF bar springs are pre-simulated into a looping compositor animation, so there's no CPU while playing.
- **Tray:** native `Shell_NotifyIcon` (`Services/TrayService`), so WinForms isn't loaded. Settings… is greyed out until stage 5.
- Services and view models are copies of the WPF ones. `Ui.Post` replaces the WPF dispatcher, `Microsoft.UI.Xaml.DispatcherTimer` replaces the WPF one, and images are `SoftwareBitmap` (`Services/ImageLoader`). `MainViewModel` doesn't have the Clipboard, Shelf or Ask view models yet; they come back in stages 3–4.

**Not done or different in stage 1 (pick up later):**
- Bottom corners are circular. The WPF notch's continuous ("squircle") corners need a path geometry, so they aren't animated by the compositor yet.
- No blur on the collapsed-content and pill fades yet (Win2D blur is planned).
- Expanded notch: the shape morphs to the per-page sizes, but its content (header, pages, footer, matched-element flights) is stage 2. The hotkey opens and pins the notch on the Ask page index; the page itself and focus take/restore are stage 4.
- Not checked on screen: the tray icon (the taskbar was auto-hidden), the mini art and visualiser with music playing, and click-to-pin. Late in the session the machine ignored injected mouse input, so the pin was only confirmed not to take focus.

## Stage 2 status (done, 2026-11-11)

The open notch and the Music page are in: header (battery ring with %, bold time, date, settings gear), the pager, the footer (notification hint, page navigator, connected devices with reflow), the music page (art with pop and paused squash, click to cycle players, title / artist / source, compositor seek bar with hover thickening, handle and album-accent fill, transport), the album glow (radial gradient, cross-fade between tracks, playing / paused level, breathing pulse), the staggered entrance, and the shared-element flights (battery ring, clock, mini art ⇄ album art).

**Measured** (WPF notch stopped, 150%, 60 Hz):

| | WinUI stage 2 |
|---|---|
| Open / close morph | 52–57 / 57–59 fps (`FrameMeter 4 500`) |
| Page slides (`--cycle` + `FrameMeter slides 9`) | 56–58 fps for slides with a shape morph. Music → clipboard reads ~48 because the meter's 1 px centre column only sees the still-empty clipboard page in the second half of the slide (identical frames), not because frames are dropped |
| Idle CPU / memory | 0.016 s CPU over 10 s / ~105 MB |
| Music midpoint check (`DYNAMICNOTCH_PROFILE=1`, frames.log) | cover 105.33, details 105.00 (diff −0.33 DIP; WPF logged +0.67) |

Parity screenshot: the open music page matches the WPF one (same layout, sizes, fonts, glow). The only difference is the footer's "Notifications need the MSIX build" hint, which the unpackaged debug build shows by design.

**How it works:**
- `Views/ExpandedFrame` builds the open notch in code; `NotchWindow.Expanded.cs` wires it to the state machine. The pager is a `Canvas` (a Grid would layout-clip pages past the first) whose strip follows `Page` on the notch property set; the footer follows the body height.
- **Shared-element flights:** each pair has its own property set holding its measured layout rectangles (re-measured on `SizeChanged`), flags (Active, page index, near/except) and **chained expressions** for the intermediate values (progress, edge shift, centres, size). One long inline expression per property is rejected by the compositor ("The parameter is incorrect"). Composition property names are **case-insensitive**: don't give an input and a computed value names that differ only by case (an expression would reference itself).
- **Stagger / press / hover / pop / leave:** `Animation/Motions.cs`. A stagger is a start delay baked into each block's spring (`CompSpring.SetTarget(…, delay)`), so the whole entrance is scheduled in one call.
- **Navigator, seek bar, glow, devices row:** compositor shapes and expressions over springs (`Views/PageNavigator`, `SeekBar`, `AlbumGlow`, `DevicesRow`). The accent tint on navigator icons is a second, accent-coloured copy whose opacity follows the page spring (XAML brush colours aren't compositor-animatable).
- `AlbumColorService` is ported to `SoftwareBitmap` (same rules).
- **Timers:** a `DispatcherQueueTimer` that nothing references is garbage-collected and silently stops. Keep a field.
- **Dev flags** (for measuring without driving the mouse): `--open [page] [delayMs]` pins the notch open, `--cycle` steps pages every 1.6 s, `--nav-icons` shows the navigator's icon state. FrameMeter gained `watch [seconds] [windowMs]` and `slides [count]` (no cursor moves).

**Not done or different in stage 2:**
- No blur on the staggered entrance or page slides yet (Win2D).
- The Clipboard and Ask pages are empty placeholders until stages 3 and 4; the header and footer stay visible on the Ask page until it has its own.
- The navigator shows the sparkle icon for Ask (as WPF does without a logo file); the logo loader comes with stage 4.
- The Settings gear does nothing until stage 5.
- Not checked with music playing (I didn't start playback on your machine): the visualiser, the mini art ⇄ album art flight, the glow pulse and the seek bar moving. Hover-driven pieces (navigator morph on hover, seek-bar hover and drag, button hover fills) were checked only through the dev flags or not at all, because injected mouse input was unreliable again.

## Stage 3 status (done, 2026-11-11)

The Clipboard + Shelf page is in, with the same layout and rules as WPF:
- **Clipboard tiles:** RadiusSmall; image letterboxed on black, text snippet, or link icon; pin mark; green "copied" flash.
- **Tile interactions:** hover zoom and lift, pin / delete overlay buttons on hover; click to copy back, drag out to another app; pop in and leave animations.
- **Preview card:** after a short hover it springs out next to the cursor, follows it, flips left near the notch's right edge and stays inside the notch.
- **Shelf:** shell icons, click to open, drag out (as storage items), × and Clear all; missing files greyed out.
- **Drop target:** files dragged over the notch spring it open on the shelf. The notch outline glows, and the dashed drop zone brightens, thickens, scales up a touch and marches.

**Measured:** idle CPU 0.016 s over 10 s once start-up work has settled (the first ~10 s after launch are busier: media sessions, Bluetooth enumeration); ~100 MB. Page slides: 52–58 fps (mostly 56–58) measured on a column 250 px left of centre (`FrameMeter slides 9 -250`). The centre column reads 48–56 because it crosses blank areas (gap between the cards, the still-empty Ask page), where consecutive frames are identical.

**How it works:**
- **Clipboard:** the same Win32 listener (`AddClipboardFormatListener`, now on its own `HiddenWindow`). Reading and writing go through the WinRT clipboard (`Clipboard.GetContent` / `SetContent` + `Flush`). Images are `SoftwareBitmap`, written back as PNG. The private-content checks (`ExcludeClipboardContentFromMonitorProcessing`, `Clipboard Viewer Ignore`, `CanIncludeInClipboardHistory` = 0) and the self-copy guard are unchanged. History is still memory-only.
- **Shell icons:** `Interop/ShellIcon` draws the HICON into a 32-bit DIB and copies it into a `SoftwareBitmap`.
- **Drag out:** `CanDrag` + `DragStarting` (with a deferral to fill the data). **Drop in:** `AllowDrop` on the notch root, `DragEnter/Over/Leave/Drop` with the same 180 ms leave confirmation as WPF. `OutgoingDrag` keeps the notch open during a drag out.
- **Motion:** tile hover, pop / leave, preview position and pop, the drop zone (stroke thickness, opacity, scale, dash offset loop) and the notch's drop glow are all compositor springs and expressions. The preview card's shadow is a drop shadow masked by a visual surface of the card.
- **Overlay buttons** are `IconButton.Overlay` (solid backing, small radius). Icon buttons now swallow `Tapped`, so pin / delete don't also copy the tile.
- **Dev:** `--demo` also adds four sample clipboard items (memory only, never the real clipboard or `shelf.json`); `--preview` opens the preview card for the first tile.

**Not checked on screen (synthetic mouse input is unreliable here, and I didn't touch your real clipboard or shelf):** copying something for real, clicking a tile to copy back, the hover zoom, dragging a tile or shelf file out, dropping files onto the notch (glow, marching ants, shelf add), the preview's follow and flip, the image preview, and real shelf tiles (your shelf is empty, so only the empty drop zone was seen).

## Stage 4 status (done, 2026-11-11)

The Ask page is in, with the same layout and rules as WPF:
- **Own header** (Claude logo or sparkle, "Ask Claude", clock, settings). It slides in with the pager while the shared header and footer fade out.
- **Idle box:** attached-screenshot chip with ×, text box, Send (dimmed when there's nothing to send), Select area, hotkey caption, page dots.
- **Conversation:** question card ("You", thumbnail, question) and answer block ("Claude", thinking pulse, streamed Markdown, or the error with Retry / Open Settings). Older turns dimmed, the scroller sticks to the newest turn, follow-up row with Stop / Send / Select area / New chat.
- **Notch sizing:** the notch springs to the page's size per state and as content grows (`Motion.AskResize`).
- **Screenshots:** Select area (hint pill, region selector on every monitor, capture with the notch excluded) and the thumbnail hover preview in the preview card.
- **Focus:** taken only by the hotkey or a click into a text box / the answer, and handed back on Esc, hotkey or collapse. While the notch owns focus the keyboard hook is completely out of the way.

**Measured** (WPF notch stopped, 150%, 60 Hz):

| | WinUI stage 4 |
|---|---|
| Page slides through Ask (`--open 0 --cycle` + `FrameMeter slides 9 -250`) | 56–59 fps |
| Idle CPU, Ask pinned open after an answer | 0.047 s over 10 s, ~98 MB |
| Focus (`--ask-demo focus` + a script) | The notch became foreground; typed keys landed in the box; Esc returned the foreground to the previous window (same HWND) |
| Select area (`--ask-demo select` + Enter) | Overlay focused and dimmed, whole-screen capture attached as a thumbnail, focus back in the box |

**How it works:**
- `Views/AskPage` (a `UserControl`, so it can hold focus when no box has it) and `TurnView` are built in code. `ExpandedFrame` keeps an empty slot for Ask in the strip so indices line up. The real page is a full-width layer whose translation is `(AskIdx - Page) * Slot`. Header and footer opacity is `Clamp(|Page - AskIdx|, 0, 1)` on the compositor. While Ask is current, its layer takes every click (`SetAskActive`), so the invisible header and footer underneath can't.
- **Text box:** a WinUI `TextBox` restyled through lightweight resources (`TextControlBackground*`, `TextControlBorderBrush*`, `TextControlBorderThemeThicknessFocused`), so there's no light focus fill and no accent underline. Radius is min(R, h/2). Enter sends (`PreviewKeyDown`), Shift+Enter adds a new line.
- **Markdown:** a selectable `RichTextBlock`, same subset as WPF.
  - Lists use a hanging indent with the marker in the gutter.
  - Code blocks are an `InlineUIContainer`.
  - Bold uses the Inter Bold file (one family per weight).
  - Streamed text arrives in a fresh brush whose alpha follows the `StreamFade` spring, sampled into colour keyframes.
  - Inline code gets a `TextHighlighter` background. Its ranges are plain character indices that **don't count block breaks** (measured). `TextPointer.Offset` counts element boundaries, so it can't be used.
- **Thinking pulse:** a looping compositor opacity animation, started only while "thinking…" shows.
- **Focus:** `TakeFocus` removes `WS_EX_NOACTIVATE` and calls `SetForegroundWindow`; the page focuses the box. `ReleaseFocus` parks focus on the page, restores `NOACTIVATE` and hands the foreground back. The window's `Activated` (Deactivated) event replaces WPF's `Deactivated`. Esc is a `KeyDown` handler on the root with handled-events-too.
- **Region selector:** one see-through WinUI window per monitor (same transparent-backdrop recipe as the notch), placed with `AppWindow.MoveAndResize` in physical pixels. Cross cursor via `ProtectedCursor`. The dim goes on the Canvas, because a `UserControl` doesn't draw its own Background.
- **Capture:** GDI `BitBlt` into a DIB with the notch excluded (`WDA_EXCLUDEFROMCAPTURE`). JPEG q85 (long edge ≤ 1568) and the preview-sized copy come from the WinRT `BitmapEncoder`, so no System.Drawing.
- `ClaudeService` and `SecretStore` are unchanged copies. The key file is shared with the WPF app (same DPAPI scope and entropy).
- **Dev:** `--ask-demo [error|thinking] [focus|select]` plays a canned, local conversation. No request is made and the saved key is never read. Outside clicks don't unpin it, so screenshots aren't interrupted.

**Not checked on screen:**
- A real request to the Claude API (I didn't send one with your key).
- Dragging out a region (Enter for the whole screen was tested).
- The thumbnail hover preview, Stop mid-stream, New chat, Copy.
- Clicking into the text box to take focus (the hotkey path was tested).
- Scrolling up during a long answer.

## Stage 5 status (done, 2026-11-11)

The Settings window is in, with the same layout and rules as WPF. It opens from the notch's gear, from Ask's "Open Settings", and from the tray (Settings… is no longer greyed out). Only one exists at a time.
- **Window:** a black card with the global radius, a hairline highlight and a soft shadow, on a see-through, topmost window that shows in the taskbar. It opens and closes with a scale + fade spring, closes on Esc or ×, and drags by any empty part of the card.
- **Tabs:** a data-driven tab list (`SettingsViewModel.Tabs`) with a highlight that springs between tabs.
- **Claude API key tab:**
  - Masked key box (mono, the Ask field look), eye to show / hide, Paste from the clipboard, link to platform.claude.com (opens only when clicked).
  - Save (the box is cleared and a check pops in), Remove key (dimmed when there's no key), Test connection with the result in green / red.
  - The status shows the masked key only.
- **Pages tab:** one card row per page (icon tile, name, description) with a pill switch. At least one page stays on: the switch shakes and "Keep at least one page on" shows. Changes apply instantly.
- While Settings is in front, the keyboard hook passes every key through (`PassThroughWindow`).

**Checked:** both tabs open with `--settings [pages]`, take the foreground, and render like WPF (screenshots). Esc closes the window and the foreground goes back to the previous app.

**How it works:**
- `Views/SettingsWindow`: the same see-through recipe as the notch (transparent backdrop, `MakeSeeThroughPopup`), but with `WS_EX_APPWINDOW` so it's in the taskbar. It's placed with `AppWindow.MoveAndResize`, centred on the primary monitor in physical pixels, and dragged with `WM_NCLBUTTONDOWN` / `HTCAPTION`. The card's shadow is a drop shadow masked by a visual surface of the card. Open / close scale + fade and the tab highlight are compositor springs.
- `Views/PillSwitch`: a focusable `UserControl` (Space / Enter toggle; a focus ring for keyboard users). The "on" track fades over the "off" one, the thumb slides, and the thumb's two colours swap at halfway. The "no" shake is an impulse on a spring. All on the compositor (XAML brush colours can't be animated there).
- `Views/InputStyle`: the shared text-field look for the TextBox (Ask) and the PasswordBox (key), via lightweight resources. Show / hide uses `PasswordRevealMode`, not a second box.
- `ViewModels/SettingsViewModel`: the WPF one, with a `DispatcherQueueTimer`. Remove re-queries `CanExecute` when the key changes.
- **Dev:** `--settings [pages]` opens the window at start.

**Not checked** (to avoid changing your saved settings and key, and making the API call):
- Saving / removing a key, Paste, Test connection.
- Flipping page switches, including the keep-one-on shake.
- Dragging the window, the tab highlight moving, the open / close spring on screen.
- Opening from the tray or the gear by click.

## Stage 6 status (done, 2026-11-11)

The port is finished. The WinUI app is now *the* app, packaged as MSIX, and the WPF project is retired.

- **One app:** `src/DynamicNotch.WinUI` builds `DynamicNotch.exe` (AssemblyName changed from `DynamicNotch.WinUI`, version 2.0.0). It uses the WPF app's single-instance lock (`Local\DynamicNotch.SingleInstance`), so an old WPF copy and the new app never run at once.
- **Manifest:** `Package/AppxManifest.xml` is unchanged in shape (runFullTrust, userNotificationListener, bluetooth, the startup task) but now describes the WinUI build at **Version 2.0.0.0**. The self-contained Windows App SDK needs nothing extra in the manifest. One packaging detail: a packaged process reads XAML resources from `resources.pri` at the package root, so the scripts copy `DynamicNotch.pri` to `resources.pri`.
- **`scripts/register-dev.ps1`** publishes the WinUI build (Debug, self-contained) into `artifacts/package-layout` and registers it. **It no longer unregisters first:** removing a package deletes its private data, and the packaged app keeps its settings and DPAPI-encrypted Claude key in `%LocalAppData%\Packages\DynamicNotch_…\LocalCache\Local\DynamicNotch`. A normal run re-registers over the existing package (same version from the same folder, or a higher Version). `-Unregister` backs that data up to `artifacts/package-data-backup` before removing.
- **`scripts/make-msix.ps1`** publishes the WinUI build (Release, ReadyToRun) and names the .msix after the manifest version. **`scripts/run-debug.ps1`** runs the WinUI build unpackaged. `run-winui.ps1` is gone.
- **WPF retired:**
  - removed from `DynamicNotch.sln`
  - its source moved to `archive/DynamicNotch.Wpf` (kept for reference, not built; delete it whenever you like)
  - its stale publish folder `artifacts/dev-layout` deleted
  - the README describes the WinUI app throughout

**Checked:**
- `register-dev.ps1` installed 2.0.0.0 as an update over the registered WPF dev package (1.0.0.0). The package's `claude.key` and `settings.json` were kept (also backed up by hand before the update).
- Launched through the AUMID, `DynamicNotch.exe` runs from the package layout.
- Started inside the package (`Invoke-CommandInDesktopPackage … --open 0 --settings`):
  - Settings shows the saved key (masked), so the key from the WPF package is readable by the WinUI app (same DPAPI scope and entropy)
  - the footer's "Notifications need the MSIX build" hint is gone, so package identity is active
  - the music page showed the real playing track
- `DynamicNotch.sln` builds, and the Release self-contained publish used by `make-msix.ps1` succeeds.

**Not checked:**
- `make-msix.ps1` end to end (the Windows SDK isn't installed here, and it would add a signing certificate to your store).
- A real app-notification pill arriving.
- "Start with Windows" through the startup task.
- ARM64.

## After the port: squircle corners in, blur tried and removed (2026-11-11)

**Squircle corners (in).** The notch body is now a `CompositionPathGeometry` with the WPF app's continuous bottom corners. Each corner's curve starts (1 + `CornerSmoothing`) × r from the corner, with cubic control points at 0.22 of that length. The compositor can't compute a path from an expression, so `NotchShape.Follow(width, height)` re-bakes on every retarget: it samples the outline along both springs' exact trajectories into a `PathKeyFrameAnimation` (60 keys a second, linear in between; the command sequence never changes, so keys interpolate cleanly). Win2D (`Microsoft.Graphics.Win2D` 1.3.2) builds the paths with `CanvasPathBuilder`. The fill, inner highlight, drop glow, content clip and shadow mask all use that one geometry.
- **Measured:** page slides 51–58 fps (`--open 0 --cycle` + `FrameMeter slides 9 -250`, first slide after start-up excluded), open / close about 55 fps through the whole morph (`--toggle` + `FrameMeter watch 10 200`), idle 0.016 s CPU per 10 s.
- **Memory:** ~140 MB, up from ~100 MB. The extra is the shared Win2D / D3D device.

**Blur (removed at your request).** It was built as a compositor backdrop blur (`GaussianBlurEffect` over a `CompositionBackdropBrush`) in sprites over each fading region. A backdrop can't be read from inside a visual whose opacity is animating, so the sprites sat in non-fading hosts. It looked right but cost too much on this laptop:
- page slides fell from 56–58 to 36–53 fps, sometimes far lower
- the opening morph dipped for its first ~100 ms

It's gone, along with the blur constants in `Motion.cs`.

**Dev:** `--toggle` opens and closes the notch every 1.8 s for measuring the morph without moving the mouse. A notch opened by a dev flag (`--open`, `--ask-demo`, `--toggle`) ignores outside clicks, so measurements aren't cut short when you click elsewhere.
