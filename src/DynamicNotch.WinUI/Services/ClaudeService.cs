using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DynamicNotch.Services;

/// <summary>One turn of the Ask conversation (memory only, never written to disk).</summary>
public sealed record ChatMessage(string Role, string Text, string? ImageBase64 = null);

public enum ClaudeErrorKind { NoKey, InvalidKey, RateLimited, Offline, Overloaded, NoCredit, Refused, Other }

public sealed class ClaudeException : Exception
{
    public ClaudeException(ClaudeErrorKind kind, string message) : base(message) => Kind = kind;
    public ClaudeErrorKind Kind { get; }
}

/// <summary>
/// Minimal client for the Claude Messages API with streaming. This is the app's ONLY network call,
/// and it runs only when the user presses Send (or Test connection in Settings). The key comes from
/// <see cref="SecretStore"/> and goes nowhere except the x-api-key header to api.anthropic.com.
/// Nothing here logs the key, the screenshot or the conversation.
/// </summary>
public sealed class ClaudeService
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.None,
        UseProxy = true,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan, // streaming: the per-request token below handles timeouts
    };

    private readonly SecretStore _secrets;

    public ClaudeService(SecretStore secrets) => _secrets = secrets;

    /// <summary>
    /// Streams a reply. <paramref name="onText"/> is called on a background thread for every text
    /// delta; the caller marshals to the UI thread. Throws <see cref="ClaudeException"/> on failure
    /// and <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task StreamAsync(IReadOnlyList<ChatMessage> history, Action<string> onText, CancellationToken ct)
    {
        string key = _secrets.Get() ?? throw new ClaudeException(ClaudeErrorKind.NoKey, "Add your Claude API key in Settings to use Ask");

        var body = new JsonObject
        {
            ["model"] = AppConfig.ClaudeModel,
            ["max_tokens"] = AppConfig.ClaudeMaxTokens,
            ["stream"] = true,
            ["system"] = AppConfig.ClaudeSystemPrompt,
            ["output_config"] = new JsonObject { ["effort"] = AppConfig.ClaudeEffort },
            ["messages"] = BuildMessages(history),
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(AppConfig.ClaudeTimeoutSeconds));

        using var request = NewRequest(key, body);
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ClaudeException(ClaudeErrorKind.Offline, "Claude took too long to respond. Try again.");
        }
        catch (HttpRequestException)
        {
            throw new ClaudeException(ClaudeErrorKind.Offline, "Can't reach Claude. Check your internet connection.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw await ErrorFromResponse(response).ConfigureAwait(false);

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                await ReadEvents(reader, onText, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ClaudeException(ClaudeErrorKind.Offline, "The connection stalled. Try again.");
            }
            catch (IOException)
            {
                throw new ClaudeException(ClaudeErrorKind.Offline, "The connection dropped. Check your internet and try again.");
            }
        }
    }

    /// <summary>Tiny request (max_tokens 1) to check a key. Returns null when it works, otherwise the failure.</summary>
    public async Task<ClaudeException?> TestKeyAsync(string key, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = AppConfig.ClaudeModel,
            ["max_tokens"] = 1,
            ["thinking"] = new JsonObject { ["type"] = "disabled" },
            ["output_config"] = new JsonObject { ["effort"] = "low" },
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "Hi" } },
        };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var request = NewRequest(key.Trim(), body);
            using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return null;
            return await ErrorFromResponse(response).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new ClaudeException(ClaudeErrorKind.Offline, "No response from Claude. Check your internet connection."); }
        catch (HttpRequestException) { return new ClaudeException(ClaudeErrorKind.Offline, "Can't reach Claude. Check your internet connection."); }
    }

    // ---- Request building --------------------------------------------------------------------
    private static HttpRequestMessage NewRequest(string key, JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, AppConfig.ClaudeApiUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("x-api-key", key);
        request.Headers.TryAddWithoutValidation("anthropic-version", AppConfig.ClaudeApiVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    /// <summary>Last N messages; an image travels only with the message it was captured for.</summary>
    private static JsonArray BuildMessages(IReadOnlyList<ChatMessage> history)
    {
        var recent = history.Skip(Math.Max(0, history.Count - AppConfig.ClaudeHistoryMessages)).ToList();
        while (recent.Count > 0 && recent[0].Role != "user") recent.RemoveAt(0); // must start with a user turn

        var array = new JsonArray();
        foreach (var m in recent)
        {
            if (m.ImageBase64 is { Length: > 0 } img && m.Role == "user")
            {
                array.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/jpeg", ["data"] = img },
                        },
                        new JsonObject { ["type"] = "text", ["text"] = m.Text },
                    },
                });
            }
            else
            {
                array.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Text });
            }
        }
        return array;
    }

    // ---- SSE parsing -------------------------------------------------------------------------
    private static async Task ReadEvents(StreamReader reader, Action<string> onText, CancellationToken ct)
    {
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue; // "event:" lines and keep-alives
            var json = line.AsSpan(5).Trim().ToString();
            if (json.Length == 0) continue;

            JsonNode? node;
            try { node = JsonNode.Parse(json); }
            catch (JsonException) { continue; }
            if (node == null) continue;

            switch ((string?)node["type"])
            {
                case "content_block_delta":
                    if ((string?)node["delta"]?["type"] == "text_delta" && (string?)node["delta"]?["text"] is { Length: > 0 } text)
                        onText(text);
                    break;
                case "message_delta":
                    if ((string?)node["delta"]?["stop_reason"] == "refusal")
                        throw new ClaudeException(ClaudeErrorKind.Refused, "Claude can't help with that one. Try asking another way.");
                    break;
                case "error":
                    throw FromErrorType((string?)node["error"]?["type"], 0);
                case "message_stop":
                    return;
            }
        }
    }

    // ---- Errors ------------------------------------------------------------------------------
    private static async Task<ClaudeException> ErrorFromResponse(HttpResponseMessage response)
    {
        string? type = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            type = (string?)JsonNode.Parse(text)?["error"]?["type"];
        }
        catch { /* body isn't JSON (proxy, captive portal) */ }
        return FromErrorType(type, (int)response.StatusCode);
    }

    private static ClaudeException FromErrorType(string? type, int status) => (type, status) switch
    {
        ("authentication_error", _) or (_, 401) =>
            new(ClaudeErrorKind.InvalidKey, "Your API key was rejected. Check it in Settings."),
        ("permission_error", _) or (_, 403) =>
            new(ClaudeErrorKind.InvalidKey, "This API key isn't allowed to use Claude. Check it in Settings."),
        ("billing_error", _) or (_, 402) =>
            new(ClaudeErrorKind.NoCredit, "Your API account is out of credit. Add credit at platform.claude.com."),
        ("rate_limit_error", _) or (_, 429) =>
            new(ClaudeErrorKind.RateLimited, "Rate limited. Try again in a moment."),
        ("overloaded_error", _) or (_, 529) or (_, 503) =>
            new(ClaudeErrorKind.Overloaded, "Claude is busy right now. Try again in a moment."),
        ("request_too_large", _) or (_, 413) =>
            new(ClaudeErrorKind.Other, "That request was too large. Try a smaller area."),
        ("invalid_request_error", _) or (_, 400) =>
            new(ClaudeErrorKind.Other, "Claude couldn't process that request. Try rephrasing it."),
        _ => new(ClaudeErrorKind.Other, "Something went wrong talking to Claude. Try again."),
    };
}
