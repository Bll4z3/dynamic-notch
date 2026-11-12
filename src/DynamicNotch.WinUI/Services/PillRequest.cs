
namespace DynamicNotch.Services;

public enum PillSource { App, Device, Network, Vpn, Power, System }

/// <summary>A transient notification pill. System events use <see cref="Glyph"/>; app toasts use <see cref="Logo"/>.</summary>
public sealed record PillRequest(
    PillSource Source,
    string Title,
    string Subtitle,
    string? Glyph = null,
    Windows.Graphics.Imaging.SoftwareBitmap? Logo = null,
    string? AppName = null,
    string? Badge = null,
    Action? OnClick = null);
