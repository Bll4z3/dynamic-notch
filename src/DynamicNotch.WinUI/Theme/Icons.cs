using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace DynamicNotch.Design;

/// <summary>
/// The app's one icon set: Lucide (https://lucide.dev, ISC licence) path data on a 24×24 grid.
/// Icons are drawn as vector strokes with round caps/joins at <see cref="Theme.IconStroke"/>, so
/// every icon has the same stroke weight at any size. Data starting with "F1" is a filled variant
/// (used for active states such as play/pause); everything else is outline.
/// </summary>
public static class Icons
{
    // Transport (filled = active controls)
    public const string Play = "F1 M7 4.6 v14.8 a1 1 0 0 0 1.5 .87 l12.4 -7.4 a1 1 0 0 0 0 -1.74 L8.5 3.73 A1 1 0 0 0 7 4.6 z";
    public const string Pause = "F1 M6.5 4 h2.5 a1 1 0 0 1 1 1 v14 a1 1 0 0 1 -1 1 h-2.5 a1 1 0 0 1 -1 -1 v-14 a1 1 0 0 1 1 -1 z M15 4 h2.5 a1 1 0 0 1 1 1 v14 a1 1 0 0 1 -1 1 h-2.5 a1 1 0 0 1 -1 -1 v-14 a1 1 0 0 1 1 -1 z";
    public const string Prev = "M19 20 L9 12 L19 4 Z M5 19 V5";
    public const string Next = "M5 4 L15 12 L5 20 Z M19 5 V19";
    public const string Music = "M9 18 V5 l12 -2 v13 M9 18 a3 3 0 1 1 -6 0 a3 3 0 1 1 6 0 z M21 16 a3 3 0 1 1 -6 0 a3 3 0 1 1 6 0 z";
    /// <summary>Lyrics (Lucide message-square-quote): the media player's lyrics button.</summary>
    public const string Lyrics = "M21 15 a2 2 0 0 1 -2 2 H7 l-4 4 V5 a2 2 0 0 1 2 -2 h14 a2 2 0 0 1 2 2 z M8 12 a2 2 0 0 0 2 -2 V8 H8 M14 12 a2 2 0 0 0 2 -2 V8 h-2";
    /// <summary>Quotation marks (Lucide quote): the "no lyrics" state.</summary>
    public const string Quote = "M16 3 a2 2 0 0 0 -2 2 v6 a2 2 0 0 0 2 2 a1 1 0 0 1 1 1 v1 a2 2 0 0 1 -2 2 a1 1 0 0 0 -1 1 v2 a1 1 0 0 0 1 1 a6 6 0 0 0 6 -6 V5 a2 2 0 0 0 -2 -2 z M5 3 a2 2 0 0 0 -2 2 v6 a2 2 0 0 0 2 2 a1 1 0 0 1 1 1 v1 a2 2 0 0 1 -2 2 a1 1 0 0 0 -1 1 v2 a1 1 0 0 0 1 1 a6 6 0 0 0 6 -6 V5 a2 2 0 0 0 -2 -2 z";

    // Status / pills
    public const string Headphone = "M3 14 h3 a2 2 0 0 1 2 2 v3 a2 2 0 0 1 -2 2 H5 a2 2 0 0 1 -2 -2 v-7 a9 9 0 0 1 18 0 v7 a2 2 0 0 1 -2 2 h-1 a2 2 0 0 1 -2 -2 v-3 a2 2 0 0 1 2 -2 h3";
    public const string Bluetooth = "M7 7 L17 17 L12 22 V2 L17 7 L7 17";
    /// <summary>Lucide "usb" (circles as arcs).</summary>
    public const string Usb = "M9 7 a1 1 0 1 0 2 0 a1 1 0 1 0 -2 0 z M3 20 a1 1 0 1 0 2 0 a1 1 0 1 0 -2 0 z M4.7 19.3 L19 5 M21 3 l-3 1 l2 2 z M9.26 7.68 L5 12 l2 5 M10 14 l5 2 l3.5 -3.5 M18 12 l1 -1 l1 1 l-1 1 z";
    /// <summary>Lucide "monitor".</summary>
    public const string Monitor = "M4 3 h16 a2 2 0 0 1 2 2 v10 a2 2 0 0 1 -2 2 H4 a2 2 0 0 1 -2 -2 V5 a2 2 0 0 1 2 -2 z M8 21 h8 M12 17 v4";
    /// <summary>The Wi-Fi icon's parts (dot, then the arcs from small to large), for the pill animation.</summary>
    public const string WifiDot = "M12 20 h.01", WifiArc1 = "M8.5 16.43 a5 5 0 0 1 7 0", WifiArc2 = "M5 12.86 a10 10 0 0 1 14 0", WifiArc3 = "M2 8.82 a15 15 0 0 1 20 0";
    /// <summary>The VPN shield without its check, and the check alone (the pill draws them separately).</summary>
    public const string VpnShield = "M20 13 c0 5 -3.5 7.5 -7.66 8.95 a1 1 0 0 1 -.67 -.01 C7.5 20.5 4 18 4 13 V6 a1 1 0 0 1 1 -1 c2 0 4.5 -1.2 6.24 -2.72 a1.17 1.17 0 0 1 1.52 0 C14.51 3.81 17 5 19 5 a1 1 0 0 1 1 1 z", VpnCheck = "M9 12 l2 2 l4 -4";
    public const string Wifi = "M12 20 h.01 M2 8.82 a15 15 0 0 1 20 0 M5 12.86 a10 10 0 0 1 14 0 M8.5 16.43 a5 5 0 0 1 7 0";
    public const string Vpn = "M20 13 c0 5 -3.5 7.5 -7.66 8.95 a1 1 0 0 1 -.67 -.01 C7.5 20.5 4 18 4 13 V6 a1 1 0 0 1 1 -1 c2 0 4.5 -1.2 6.24 -2.72 a1.17 1.17 0 0 1 1.52 0 C14.51 3.81 17 5 19 5 a1 1 0 0 1 1 1 z M9 12 l2 2 l4 -4";
    public const string Bolt = "F1 M13 2 L4 14 h7 l-1 8 l9 -12 h-7 z";
    public const string Battery = "M4 6 h12 a2 2 0 0 1 2 2 v8 a2 2 0 0 1 -2 2 H4 a2 2 0 0 1 -2 -2 V8 a2 2 0 0 1 2 -2 z M22 11 v2 M6 10 v4 M10 10 v4";
    public const string BatteryLow = "M4 6 h12 a2 2 0 0 1 2 2 v8 a2 2 0 0 1 -2 2 H4 a2 2 0 0 1 -2 -2 V8 a2 2 0 0 1 2 -2 z M22 11 v2 M6 10 v4";
    public const string Bell = "M10.27 21 a2 2 0 0 0 3.46 0 M3.26 15.33 A1 1 0 0 0 4 17 h16 a1 1 0 0 0 .74 -1.67 C19.41 13.96 18 12.5 18 8 A6 6 0 0 0 6 8 c0 4.5 -1.41 5.96 -2.74 7.33";

    // Clipboard / shelf
    public const string Pin = "M12 17 v5 M9 10.76 a2 2 0 0 1 -1.11 1.79 l-1.78 .9 A2 2 0 0 0 5 15.24 V16 a1 1 0 0 0 1 1 h12 a1 1 0 0 0 1 -1 v-.76 a2 2 0 0 0 -1.11 -1.79 l-1.78 -.9 A2 2 0 0 1 15 10.76 V7 a1 1 0 0 1 1 -1 a2 2 0 0 0 0 -4 H8 a2 2 0 0 0 0 4 a1 1 0 0 1 1 1 z";
    /// <summary>Filled pin = pinned (active state); clicking it unpins.</summary>
    public const string Unpin = "F1 " + Pin;
    public const string Delete = "M3 6 h18 M19 6 v14 c0 1 -1 2 -2 2 H7 c-1 0 -2 -1 -2 -2 V6 M8 6 V4 c0 -1 1 -2 2 -2 h4 c1 0 2 1 2 2 v2";
    public const string Close = "M18 6 L6 18 M6 6 L18 18";
    public const string Link = "M10 13 a5 5 0 0 0 7.54 .54 l3 -3 a5 5 0 0 0 -7.07 -7.07 l-1.72 1.71 M14 11 a5 5 0 0 0 -7.54 -.54 l-3 3 a5 5 0 0 0 7.07 7.07 l1.71 -1.71";
    public const string Text = "M17 6.1 H3 M21 12.1 H3 M15.1 18 H3";
    public const string Image = "M5 3 h14 a2 2 0 0 1 2 2 v14 a2 2 0 0 1 -2 2 H5 a2 2 0 0 1 -2 -2 V5 a2 2 0 0 1 2 -2 z M11 9 a2 2 0 1 1 -4 0 a2 2 0 1 1 4 0 z M21 15 l-3.09 -3.09 a2 2 0 0 0 -2.83 0 L6 21";
    public const string File = "M15 2 H6 a2 2 0 0 0 -2 2 v16 a2 2 0 0 0 2 2 h12 a2 2 0 0 0 2 -2 V7 z M14 2 v4 a2 2 0 0 0 2 2 h4";
    public const string Clipboard = "M9 2 h6 a1 1 0 0 1 1 1 v2 a1 1 0 0 1 -1 1 H9 a1 1 0 0 1 -1 -1 V3 a1 1 0 0 1 1 -1 z M16 4 h2 a2 2 0 0 1 2 2 v14 a2 2 0 0 1 -2 2 H6 a2 2 0 0 1 -2 -2 V6 a2 2 0 0 1 2 -2 h2";
    public const string Shelf = "M22 12 h-6 l-2 3 h-4 l-2 -3 H2 M5.45 5.11 L2 12 v6 a2 2 0 0 0 2 2 h16 a2 2 0 0 0 2 -2 v-6 l-3.45 -6.89 A2 2 0 0 0 16.76 4 H7.24 a2 2 0 0 0 -1.79 1.11 z";
    public const string Upload = "M12 3 v12 M7 8 l5 -5 l5 5 M21 15 v4 a2 2 0 0 1 -2 2 H5 a2 2 0 0 1 -2 -2 v-4";

    // Device types
    public const string Speaker = "M6 2 h12 a2 2 0 0 1 2 2 v16 a2 2 0 0 1 -2 2 H6 a2 2 0 0 1 -2 -2 V4 a2 2 0 0 1 2 -2 z M12 6 h.01 M16 14 a4 4 0 1 1 -8 0 a4 4 0 1 1 8 0 z M12 14 h.01";
    public const string Mouse = "M12 2 a7 7 0 0 1 7 7 v6 a7 7 0 0 1 -14 0 V9 a7 7 0 0 1 7 -7 z M12 6 v4";
    public const string Keyboard = "M4 4 h16 a2 2 0 0 1 2 2 v12 a2 2 0 0 1 -2 2 H4 a2 2 0 0 1 -2 -2 V6 a2 2 0 0 1 2 -2 z M6 8 h.01 M10 8 h.01 M14 8 h.01 M18 8 h.01 M8 12 h.01 M12 12 h.01 M16 12 h.01 M7 16 h10";
    public const string Controller = "M6 11 h4 M8 9 v4 M15 12 h.01 M18 10 h.01 M17.32 5 H6.68 a4 4 0 0 0 -3.98 3.59 C2.6 9.42 2 14.46 2 16 a3 3 0 0 0 3 3 c1 0 1.5 -.5 2 -1 l1.41 -1.41 A2 2 0 0 1 9.83 16 h4.34 a2 2 0 0 1 1.41 .59 L17 18 c.5 .5 1 1 2 1 a3 3 0 0 0 3 -3 c0 -1.55 -.6 -6.58 -.69 -7.26 A4 4 0 0 0 17.32 5 z";
    public const string Phone = "M7 2 h10 a2 2 0 0 1 2 2 v16 a2 2 0 0 1 -2 2 H7 a2 2 0 0 1 -2 -2 V4 a2 2 0 0 1 2 -2 z M12 18 h.01";
    public const string Watch = "M12 10 v2.2 l1.6 1 M16.13 7.66 l-.81 -4.05 a2 2 0 0 0 -2 -1.61 h-2.68 a2 2 0 0 0 -2 1.61 l-.78 4.05 M7.88 16.36 l.8 4 a2 2 0 0 0 2 1.61 h2.72 a2 2 0 0 0 2 -1.61 l.81 -4.05 M18 12 a6 6 0 1 1 -12 0 a6 6 0 1 1 12 0 z";

    // Ask / settings
    public const string Sparkle = "M9.94 15.5 A2 2 0 0 0 8.5 14.06 l-6.14 -1.58 a.5 .5 0 0 1 0 -.96 L8.5 9.94 A2 2 0 0 0 9.94 8.5 l1.58 -6.14 a.5 .5 0 0 1 .96 0 L14.06 8.5 A2 2 0 0 0 15.5 9.94 l6.14 1.58 a.5 .5 0 0 1 0 .96 L15.5 14.06 a2 2 0 0 0 -1.44 1.44 l-1.58 6.14 a.5 .5 0 0 1 -.96 0 z M20 3 v4 M22 5 h-4 M4 17 v2 M5 18 H3";
    public const string SparkleFilled = "F1 " + Sparkle;
    public const string Crop = "M6 2 v14 a2 2 0 0 0 2 2 h14 M18 22 V8 a2 2 0 0 0 -2 -2 H2";
    public const string ArrowUp = "M5 12 L12 5 L19 12 M12 19 V5";
    public const string Stop = "F1 M7 6 h10 a1 1 0 0 1 1 1 v10 a1 1 0 0 1 -1 1 H7 a1 1 0 0 1 -1 -1 V7 a1 1 0 0 1 1 -1 z";
    public const string Copy = "M10 8 h10 a2 2 0 0 1 2 2 v10 a2 2 0 0 1 -2 2 H10 a2 2 0 0 1 -2 -2 V10 a2 2 0 0 1 2 -2 z M4 16 c-1.1 0 -2 -.9 -2 -2 V4 c0 -1.1 .9 -2 2 -2 h10 c1.1 0 2 .9 2 2";
    public const string NewChat = "M12 3 H5 a2 2 0 0 0 -2 2 v14 a2 2 0 0 0 2 2 h14 a2 2 0 0 0 2 -2 v-7 M18.38 2.63 a1 1 0 0 1 3 3 l-9.01 9.01 a2 2 0 0 1 -.85 .51 l-2.87 .84 a.5 .5 0 0 1 -.62 -.62 l.84 -2.87 a2 2 0 0 1 .51 -.85 z";
    public const string Settings = "M12.22 2 h-.44 a2 2 0 0 0 -2 2 v.18 a2 2 0 0 1 -1 1.73 l-.43 .25 a2 2 0 0 1 -2 0 l-.15 -.08 a2 2 0 0 0 -2.73 .73 l-.22 .38 a2 2 0 0 0 .73 2.73 l.15 .1 a2 2 0 0 1 1 1.72 v.51 a2 2 0 0 1 -1 1.74 l-.15 .09 a2 2 0 0 0 -.73 2.73 l.22 .38 a2 2 0 0 0 2.73 .73 l.15 -.08 a2 2 0 0 1 2 0 l.43 .25 a2 2 0 0 1 1 1.73 V20 a2 2 0 0 0 2 2 h.44 a2 2 0 0 0 2 -2 v-.18 a2 2 0 0 1 1 -1.73 l.43 -.25 a2 2 0 0 1 2 0 l.15 .08 a2 2 0 0 0 2.73 -.73 l.22 -.39 a2 2 0 0 0 -.73 -2.73 l-.15 -.08 a2 2 0 0 1 -1 -1.74 v-.5 a2 2 0 0 1 1 -1.74 l.15 -.09 a2 2 0 0 0 .73 -2.73 l-.22 -.38 a2 2 0 0 0 -2.73 -.73 l-.15 .08 a2 2 0 0 1 -2 0 l-.43 -.25 a2 2 0 0 1 -1 -1.73 V4 a2 2 0 0 0 -2 -2 z M15 12 a3 3 0 1 1 -6 0 a3 3 0 1 1 6 0 z";
    public const string Eye = "M2.06 12.35 a1 1 0 0 1 0 -.7 a10.75 10.75 0 0 1 19.88 0 a1 1 0 0 1 0 .7 a10.75 10.75 0 0 1 -19.88 0 z M15 12 a3 3 0 1 1 -6 0 a3 3 0 1 1 6 0 z";
    public const string EyeOff = Eye + " M3 3 L21 21";
    public const string Check = "M20 6 L9 17 L4 12";
    public const string Key = "M12 16 a4 4 0 1 1 -8 0 a4 4 0 1 1 8 0 z M10.8 13.2 L21 3 M17 7 l3 3 M14.5 9.5 l2 2";
    public const string Alert = "M22 12 a10 10 0 1 1 -20 0 a10 10 0 1 1 20 0 z M12 8 v4 M12 16 h.01";
    /// <summary>Lucide "palette" (Settings ▸ Customisation).</summary>
    public const string Palette = "M12 2 C6.5 2 2 6.5 2 12 s4.5 10 10 10 c.93 0 1.65 -.75 1.65 -1.69 0 -.44 -.18 -.84 -.44 -1.13 -.29 -.29 -.44 -.65 -.44 -1.13 a1.64 1.64 0 0 1 1.67 -1.67 h2 c3.05 0 5.56 -2.5 5.56 -5.55 C21.97 6.01 17.46 2 12 2 z M13.5 6.5 h.01 M17.5 10.5 h.01 M8.5 7.5 h.01 M6.5 12.5 h.01";
    /// <summary>Lucide "undo-2".</summary>
    public const string Undo = "M9 14 4 9 l5 -5 M4 9 h10.5 a5.5 5.5 0 0 1 5.5 5.5 a5.5 5.5 0 0 1 -5.5 5.5 H11";
    public const string Retry ="M3 12 a9 9 0 1 0 9 -9 a9.75 9.75 0 0 0 -6.74 2.74 L3 8 M3 3 v5 h5";
    public const string Paste = "M9 2 h6 a1 1 0 0 1 1 1 v2 a1 1 0 0 1 -1 1 H9 a1 1 0 0 1 -1 -1 V3 a1 1 0 0 1 1 -1 z M16 4 h2 a2 2 0 0 1 2 2 v14 a2 2 0 0 1 -2 2 H6 a2 2 0 0 1 -2 -2 V6 a2 2 0 0 1 2 -2 h2 M12 11 v6 M9 14 l3 3 l3 -3";

    // Usage / focus timer / routine editor
    public const string Gauge = "M12 14 l4 -4 M3.34 19 a10 10 0 1 1 17.32 0";
    public const string Timer = "M10 2 h4 M12 14 l3 -3 M20 14 a8 8 0 1 1 -16 0 a8 8 0 1 1 16 0 z";
    public const string Plus = "M5 12 h14 M12 5 v14";
    public const string Minus = "M5 12 h14";
    public const string ChevronUp = "M18 15 l-6 -6 l-6 6";
    public const string ChevronDown = "M6 9 l6 6 l6 -6";
    public const string Fullscreen = "M8 3 H5 a2 2 0 0 0 -2 2 v3 M21 8 V5 a2 2 0 0 0 -2 -2 h-3 M3 16 v3 a2 2 0 0 0 2 2 h3 M16 21 h3 a2 2 0 0 0 2 -2 v-3";

    // Activity pills / quick actions
    private const string SpeakerBody = "M11 4.7 a.7 .7 0 0 0 -1.2 -.5 L6.41 7.59 A1.4 1.4 0 0 1 5.42 8 H3 a1 1 0 0 0 -1 1 v6 a1 1 0 0 0 1 1 h2.42 a1.4 1.4 0 0 1 1 .41 l3.38 3.39 A.7 .7 0 0 0 11 19.3 z";
    public const string VolumeMute = SpeakerBody + " M22 9 l-6 6 M16 9 l6 6";
    public const string VolumeLow = SpeakerBody;
    public const string VolumeMid = SpeakerBody + " M16 9 a5 5 0 0 1 0 6";
    public const string VolumeHigh = SpeakerBody + " M16 9 a5 5 0 0 1 0 6 M19.36 18.36 a9 9 0 0 0 0 -12.73";
    public const string SunCore = "M16 12 a4 4 0 1 1 -8 0 a4 4 0 1 1 8 0 z";
    public const string SunRays = "M12 2 v2 M12 20 v2 M4.93 4.93 l1.41 1.41 M17.66 17.66 l1.41 1.41 M2 12 h2 M20 12 h2 M6.34 17.66 l-1.41 1.41 M19.07 4.93 l-1.41 1.41";
    public const string LockBody = "M5 11 h14 a2 2 0 0 1 2 2 v7 a2 2 0 0 1 -2 2 H5 a2 2 0 0 1 -2 -2 v-7 a2 2 0 0 1 2 -2 z";
    public const string LockShackle = "M7 11 V7 a5 5 0 0 1 10 0 v4";
    public const string Download = "M12 15 V3 M21 15 v4 a2 2 0 0 1 -2 2 H5 a2 2 0 0 1 -2 -2 v-4 M7 10 l5 5 l5 -5";
    public const string UpdateArrow = "M22 12 a10 10 0 1 1 -20 0 a10 10 0 1 1 20 0 z M16 12 l-4 -4 l-4 4 M12 16 V8";
    public const string CheckCircle = "M22 12 a10 10 0 1 1 -20 0 a10 10 0 1 1 20 0 z M9 12 l2 2 l4 -4";
    public const string Folder = "M20 20 a2 2 0 0 0 2 -2 V8 a2 2 0 0 0 -2 -2 h-7.9 a2 2 0 0 1 -1.69 -.9 L9.6 3.9 A2 2 0 0 0 7.93 3 H4 a2 2 0 0 0 -2 2 v13 a2 2 0 0 0 2 2 z";
    public const string Plane = "M17.8 19.2 L16 11 l3.5 -3.5 C21 6 21.5 4 21 3 c-1 -.5 -3 0 -4.5 1.5 L13 8 L4.8 6.2 c-.5 -.1 -.9 .1 -1.1 .5 l-.3 .5 c-.2 .5 -.1 1 .3 1.3 L9 12 l-2 3 H4 l-1 1 l3 2 l2 3 l1 -1 v-3 l3 -2 l3.5 5.3 c.3 .4 .8 .5 1.3 .3 l.5 -.2 c.4 -.3 .6 -.7 .5 -1.2 z";
    public const string Moon = "M12 3 a6 6 0 0 0 9 9 a9 9 0 1 1 -9 -9 z";
    public const string Mic = "M12 2 a3 3 0 0 0 -3 3 v7 a3 3 0 0 0 6 0 V5 a3 3 0 0 0 -3 -3 z M19 10 v2 a7 7 0 0 1 -14 0 v-2 M12 19 v3";
    public const string MicOff = "M2 2 L22 22 M18.89 13.23 A7 7 0 0 0 19 12 v-2 M5 10 v2 a7 7 0 0 0 12 5 M15 9.34 V5 a3 3 0 0 0 -5.68 -1.33 M9 9 v3 a3 3 0 0 0 5.12 2.12 M12 19 v3";
    public const string Leaf = "M11 20 A7 7 0 0 1 9.8 6.1 C15.5 5 17 4.48 19 2 c1 2 2 4.18 2 8 c0 5.5 -4.78 10 -10 10 z M2 21 c0 -3 1.85 -5.36 5.08 -6 C9.5 14.52 12 13 13 12";
    public const string NightLight = "M12 10 V2 M4.93 10.93 l1.41 1.41 M2 18 h2 M20 18 h2 M19.07 10.93 l-1.41 1.41 M22 22 H2 M16 6 l-4 4 l-4 -4 M16 18 a4 4 0 0 0 -8 0";
    public const string Cast = "M2 8 V6 a2 2 0 0 1 2 -2 h16 a2 2 0 0 1 2 2 v12 a2 2 0 0 1 -2 2 h-6 M2 12 a9 9 0 0 1 8 8 M2 16 a5 5 0 0 1 4 4 M2 20 h.01";
    public const string Dashboard = "M4 3 h5 a1 1 0 0 1 1 1 v7 a1 1 0 0 1 -1 1 H4 a1 1 0 0 1 -1 -1 V4 a1 1 0 0 1 1 -1 z M15 3 h5 a1 1 0 0 1 1 1 v3 a1 1 0 0 1 -1 1 h-5 a1 1 0 0 1 -1 -1 V4 a1 1 0 0 1 1 -1 z M15 12 h5 a1 1 0 0 1 1 1 v7 a1 1 0 0 1 -1 1 h-5 a1 1 0 0 1 -1 -1 v-7 a1 1 0 0 1 1 -1 z M4 16 h5 a1 1 0 0 1 1 1 v3 a1 1 0 0 1 -1 1 H4 a1 1 0 0 1 -1 -1 v-3 a1 1 0 0 1 1 -1 z";
    public const string ChevronRight = "M9 18 l6 -6 l-6 -6";
    /// <summary>Wi-Fi signal levels (Wifi is full strength).</summary>
    public const string WifiMid = "M12 20 h.01 M5 12.86 a10 10 0 0 1 14 0 M8.5 16.43 a5 5 0 0 1 7 0";
    public const string WifiLow = "M12 20 h.01 M8.5 16.43 a5 5 0 0 1 7 0";
    public const string Lock = LockBody + " " + LockShackle;
    /// <summary>Six-dot drag handle.</summary>
    public const string Grip = "M9 5 h.01 M9 12 h.01 M9 19 h.01 M15 5 h.01 M15 12 h.01 M15 19 h.01";
    // ---- Header pop-ups and the screenshot shelf (Lucide-style strokes) ----
    public const string Search = "M21 21 l-4.34 -4.34 M19 11 a8 8 0 1 1 -16 0 a8 8 0 1 1 16 0 z";
    public const string Command = "M15 6 v12 a3 3 0 1 0 3 -3 H6 a3 3 0 1 0 3 3 V6 a3 3 0 1 0 -3 3 h12 a3 3 0 1 0 -3 -3";
    public const string Calculator = "M6 2 h12 a2 2 0 0 1 2 2 v16 a2 2 0 0 1 -2 2 H6 a2 2 0 0 1 -2 -2 V4 a2 2 0 0 1 2 -2 z M8 6 h8 M16 14 v4 M16 10 h.01 M12 10 h.01 M8 10 h.01 M12 14 h.01 M8 14 h.01 M12 18 h.01 M8 18 h.01";
    public const string Globe = "M22 12 a10 10 0 1 1 -20 0 a10 10 0 1 1 20 0 z M12 2 a14.5 14.5 0 0 0 0 20 a14.5 14.5 0 0 0 0 -20 M2 12 h20";
    public const string Ruler = "M21.3 15.3 a2.4 2.4 0 0 1 0 3.4 l-2.6 2.6 a2.4 2.4 0 0 1 -3.4 0 L2.7 8.7 a2.41 2.41 0 0 1 0 -3.4 l2.6 -2.6 a2.41 2.41 0 0 1 3.4 0 Z M14.5 12.5 l2 -2 M11.5 9.5 l2 -2 M8.5 6.5 l2 -2 M17.5 15.5 l2 -2";
    public const string Coins = "M14 8 a6 6 0 1 1 -12 0 a6 6 0 1 1 12 0 z M18.09 10.37 A6 6 0 1 1 10.34 18 M7 6 h1 v4 M16.71 13.88 l.7 .71 -2.82 2.82";
    public const string Power = "M12 2 v10 M18.4 6.6 a9 9 0 1 1 -12.77 .04";
    public const string AppWindow = "M4 3 h16 a2 2 0 0 1 2 2 v14 a2 2 0 0 1 -2 2 H4 a2 2 0 0 1 -2 -2 V5 a2 2 0 0 1 2 -2 z M2 8 h20 M6 5.5 h.01 M9 5.5 h.01";
    public const string Clock = "M22 12 a10 10 0 1 1 -20 0 a10 10 0 1 1 20 0 z M12 6 v6 l4 2";
    public const string Book = "M4 19.5 v-15 A2.5 2.5 0 0 1 6.5 2 H19 a1 1 0 0 1 1 1 v18 a1 1 0 0 1 -1 1 H6.5 a1 1 0 0 1 0 -5 H20";
    public const string Hash = "M4 9 h16 M4 15 h16 M10 3 L8 21 M16 3 l-2 18";
    public const string Calendar = "M8 2 v4 M16 2 v4 M5 4 h14 a2 2 0 0 1 2 2 v14 a2 2 0 0 1 -2 2 H5 a2 2 0 0 1 -2 -2 V6 a2 2 0 0 1 2 -2 z M3 10 h18";
    public const string Terminal = "M4 17 l6 -6 -6 -6 M12 19 h8";
    public const string Star = "M11.53 2.3 a.53 .53 0 0 1 .95 0 l2.31 4.68 a2.12 2.12 0 0 0 1.6 1.16 l5.16 .76 a.53 .53 0 0 1 .3 .9 l-3.74 3.64 a2.12 2.12 0 0 0 -.61 1.88 l.88 5.14 a.53 .53 0 0 1 -.77 .56 l-4.62 -2.43 a2.12 2.12 0 0 0 -1.97 0 L6.4 21.01 a.53 .53 0 0 1 -.77 -.56 l.88 -5.14 a2.12 2.12 0 0 0 -.61 -1.88 L2.16 9.79 a.53 .53 0 0 1 .3 -.9 l5.16 -.76 a2.12 2.12 0 0 0 1.6 -1.16 z";
    public const string Dice = "M5 3 h14 a2 2 0 0 1 2 2 v14 a2 2 0 0 1 -2 2 H5 a2 2 0 0 1 -2 -2 V5 a2 2 0 0 1 2 -2 z M16 8 h.01 M8 8 h.01 M8 16 h.01 M16 16 h.01 M12 12 h.01";
    public const string Sliders = "M21 4 H14 M10 4 H3 M21 12 H12 M8 12 H3 M21 20 H16 M12 20 H3 M14 2 v4 M8 10 v4 M16 18 v4";
    public const string Screenshot = "M3 7 V5 a2 2 0 0 1 2 -2 h2 M17 3 h2 a2 2 0 0 1 2 2 v2 M21 17 v2 a2 2 0 0 1 -2 2 h-2 M7 21 H5 a2 2 0 0 1 -2 -2 v-2 M8 9 h8 v6 H8 z";
    public const string Pen = "M21.17 6.81 a1 1 0 0 0 -3.99 -3.99 L3.84 16.17 a2 2 0 0 0 -.5 .83 l-1.32 4.35 a.5 .5 0 0 0 .62 .62 l4.35 -1.32 a2 2 0 0 0 .83 -.5 z M15 5 l4 4";
    public const string Highlighter = "M9 11 l-6 6 v3 h9 l3 -3 M22 12 l-4.6 4.6 a2 2 0 0 1 -2.8 0 l-5.2 -5.2 a2 2 0 0 1 0 -2.8 L14 4";
    public const string ArrowTool = "M7 17 L17 7 M7 7 h10 v10";
    public const string TextTool = "M4 7 V4 h16 v3 M9 20 h6 M12 4 v16";
    public const string Save = "M15.2 3 a2 2 0 0 1 1.4 .6 l3.8 3.8 a2 2 0 0 1 .6 1.4 V19 a2 2 0 0 1 -2 2 H5 a2 2 0 0 1 -2 -2 V5 a2 2 0 0 1 2 -2 z M17 21 v-7 a1 1 0 0 0 -1 -1 H8 a1 1 0 0 0 -1 1 v7 M7 3 v4 a1 1 0 0 0 1 1 h7";
    public const string ScanText = "M3 7 V5 a2 2 0 0 1 2 -2 h2 M17 3 h2 a2 2 0 0 1 2 2 v2 M21 17 v2 a2 2 0 0 1 -2 2 h-2 M7 21 H5 a2 2 0 0 1 -2 -2 v-2 M7 8 h8 M7 12 h10 M7 16 h6";

    // ---- Rendering ---------------------------------------------------------------------------
    public static bool IsFilled(string data) => data.StartsWith("F1", StringComparison.Ordinal);

    /// <summary>
    /// A XAML Path for the icon on its 24×24 grid, scaled to <paramref name="size"/> DIPs with the stroke
    /// kept at <see cref="Theme.IconStroke"/>. A XAML geometry can only belong to one Path, so every
    /// call parses a fresh one (icons are few and built once).
    /// </summary>
    public static Path? CreatePath(string? data, double size, Brush brush)
    {
        if (string.IsNullOrEmpty(data)) return null;
        Path path;
        // The geometry type converter parses only the path data (cheaper than XamlReader parsing a whole
        // <Path> element, which made every icon, and so every popup row, costly to build).
        try { path = new Path { Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data) }; }
        catch
        {
            try
            {
                path = (Path)XamlReader.Load(
                    "<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Data=\"" + data + "\" />");
            }
            catch { return null; }
        }
        double s = size / 24.0;
        path.Width = path.Height = 24;
        path.Stroke = brush;
        path.Fill = IsFilled(data) ? brush : null;
        path.StrokeThickness = Theme.IconStroke / s;
        path.StrokeStartLineCap = path.StrokeEndLineCap = Microsoft.UI.Xaml.Media.PenLineCap.Round;
        path.StrokeLineJoin = Microsoft.UI.Xaml.Media.PenLineJoin.Round;
        path.RenderTransform = new ScaleTransform { ScaleX = s, ScaleY = s };
        path.IsHitTestVisible = false;
        return path;
    }
}
