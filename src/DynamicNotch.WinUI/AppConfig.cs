namespace DynamicNotch;

/// <summary>App-level constants that aren't design tokens: hotkeys, the Claude API and screenshot limits.</summary>
public static class AppConfig
{
    // ---- Ask: global hotkey (Ctrl+Shift+Space) ------------------------------------------------
    public const uint AskHotkeyModifiers = 0x0002 /* MOD_CONTROL */ | 0x0004 /* MOD_SHIFT */ | 0x4000 /* MOD_NOREPEAT */;
    public const uint AskHotkeyVirtualKey = 0x20; // VK_SPACE
    public const string AskHotkeyLabel = "Ctrl+Shift+Space";

    // ---- Claude API (the ONLY network call the app makes, and only on Send / Test connection) ----
    public const string ClaudeApiUrl = "https://api.anthropic.com/v1/messages";
    public const string ClaudeApiVersion = "2023-06-01";
    public const string ClaudeModel = "claude-haiku-5-5";
    public const int ClaudeMaxTokens = 1024;
    /// <summary>Haiku 5.5 thinks by default and thinking counts toward max_tokens; low effort keeps answers fast and complete.</summary>
    public const string ClaudeEffort = "low";
    /// <summary>Only the most recent messages are sent with each request.</summary>
    public const int ClaudeHistoryMessages = 6;
    public const string ClaudeSystemPrompt =
        "You are a concise assistant inside a desktop notch. The user may attach a screenshot of part of their screen. " +
        "Answer briefly and clearly. Use short paragraphs or bullets.";
    public const string DefaultScreenQuestion = "What's on my screen?";
    public const string ApiKeyPageUrl = "https://platform.claude.com";
    public const string ApiKeyPageLabel = "Get a key at platform.claude.com";
    public const int ClaudeTimeoutSeconds = 90;

    // ---- Claude Usage page (UNOFFICIAL endpoint; see Services/ClaudeUsageService.cs) ---------------
    /// <summary>Polling interval while the usage page is visible (no polling while it's hidden).</summary>
    public const int UsagePollSeconds = 60;
    /// <summary>Longest wait between retries after errors / 429s.</summary>
    public const int UsageMaxBackoffSeconds = 15 * 60;

    // ---- Screenshots ---------------------------------------------------------------------------
    public const int ScreenshotMaxEdge = 1568;
    public const int ScreenshotJpegQuality = 85;
    /// <summary>Pixel height of the thumbnail kept for the UI (the full image is only sent, never stored).</summary>
    public const int ThumbnailPixelHeight = 120;
}
