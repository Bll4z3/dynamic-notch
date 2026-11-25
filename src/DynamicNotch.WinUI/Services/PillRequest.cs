
namespace DynamicNotch.Services;

public enum PillSource { App, Device, Network, Vpn, Power, System }

/// <summary>
/// The small animation (and colour) a system pill's icon plays as it appears (see Views.PillIconView): Wi-Fi bars filling
/// in, the VPN shield drawing in and clicking shut, a Bluetooth device sending out waves, and so on. None = the plain
/// static glyph (app notifications and anything else).
/// </summary>
public enum PillAnim { None, WifiOn, WifiOff, VpnOn, VpnOff, DeviceOn, DeviceOff, AudioSwitch, UsbIn, UsbOut, DisplayIn, DisplayOut }

/// <summary>A transient notification pill. System events use <see cref="Glyph"/>; app toasts use <see cref="Logo"/>.</summary>
public sealed record PillRequest(
    PillSource Source,
    string Title,
    string Subtitle,
    string? Glyph = null,
    Windows.Graphics.Imaging.SoftwareBitmap? Logo = null,
    string? AppName = null,
    string? Badge = null,
    Action? OnClick = null,
    PillAnim Anim = PillAnim.None);
