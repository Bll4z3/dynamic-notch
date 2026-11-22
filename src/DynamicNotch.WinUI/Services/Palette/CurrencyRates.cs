using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicNotch.Services.Palette;

/// <summary>
/// Currency conversion with free, key-less exchange rates (open.er-api.com, base USD). Rates are cached in
/// currency.json and refreshed in the background at most every 12 hours; offline, the last saved rates are
/// used (the result says how old they are). Only the currency codes are sent, nothing about the user.
/// </summary>
public sealed class CurrencyRates
{
    private const string Url = "https://open.er-api.com/v6/latest/USD";
    private const string File = "currency.json";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly Regex Pattern = new(@"^\s*([$£€¥₹]?)\s*([\d.,]+)\s*([a-z$£€¥₹]{1,8})?\s+(?:to|in|into|=)\s+([a-z$£€¥₹]{1,8})\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["$"] = "USD", ["dollar"] = "USD", ["dollars"] = "USD", ["usd"] = "USD", ["bucks"] = "USD",
        ["£"] = "GBP", ["pound"] = "GBP", ["pounds"] = "GBP", ["quid"] = "GBP", ["sterling"] = "GBP",
        ["€"] = "EUR", ["euro"] = "EUR", ["euros"] = "EUR",
        ["¥"] = "JPY", ["yen"] = "JPY", ["₹"] = "INR", ["rupee"] = "INR", ["rupees"] = "INR",
        ["yuan"] = "CNY", ["rmb"] = "CNY", ["franc"] = "CHF", ["francs"] = "CHF", ["won"] = "KRW",
        ["peso"] = "MXN", ["pesos"] = "MXN", ["dirham"] = "AED", ["riyal"] = "SAR", ["lira"] = "TRY",
    };

    private sealed class Store { public Dictionary<string, double> Rates { get; set; } = new(); public DateTimeOffset Updated { get; set; } }

    private Store _store = SettingsService.ReadJson<Store>(File) ?? new Store();
    private Task? _refreshing;

    public bool HasRates => _store.Rates.Count > 0;

    /// <summary>Fetches fresh rates when the saved ones are older than 12 hours (or missing). Never throws.</summary>
    public Task RefreshIfStaleAsync()
    {
        if (HasRates && DateTimeOffset.Now - _store.Updated < TimeSpan.FromHours(12)) return Task.CompletedTask;
        return _refreshing ??= Task.Run(async () =>
        {
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(Url));
                if (doc.RootElement.TryGetProperty("rates", out var rates))
                {
                    var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in rates.EnumerateObject()) if (p.Value.TryGetDouble(out var v)) map[p.Name] = v;
                    if (map.Count > 0)
                    {
                        _store = new Store { Rates = map, Updated = DateTimeOffset.Now };
                        SettingsService.WriteJson(File, _store);
                    }
                }
            }
            catch { /* offline: keep the saved rates */ }
            finally { _refreshing = null; }
        });
    }

    /// <summary>Whether the query looks like a currency conversion (so the palette can wait for rates).</summary>
    public bool Matches(string query, out (double amount, string from, string to) parsed)
    {
        parsed = default;
        var m = Pattern.Match(query);
        if (!m.Success) return false;
        string fromRaw = m.Groups[3].Success && m.Groups[3].Value.Length > 0 ? m.Groups[3].Value : m.Groups[1].Value;
        string? from = Code(fromRaw), to = Code(m.Groups[4].Value);
        if (from == null || to == null || from == to) return false;
        if (!double.TryParse(m.Groups[2].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double amount)) return false;
        parsed = (amount, from, to);
        return true;
    }

    private string? Code(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (Aliases.TryGetValue(raw, out var a)) return a;
        string up = raw.ToUpperInvariant();
        if (up.Length == 3 && (_store.Rates.ContainsKey(up) || !HasRates && up.All(char.IsLetter))) return up;
        return null;
    }

    /// <summary>The converted amount, or null without rates for those currencies.</summary>
    public (double value, string age)? Convert(double amount, string from, string to)
    {
        if (!_store.Rates.TryGetValue(from, out var f) || !_store.Rates.TryGetValue(to, out var t) || f <= 0) return null;
        var age = DateTimeOffset.Now - _store.Updated;
        string ageText = age.TotalHours < 1 ? "rates just updated" : age.TotalHours < 24 ? $"rates from {(int)age.TotalHours} h ago" : $"rates from {_store.Updated.LocalDateTime:d MMM}";
        return (amount / f * t, ageText);
    }
}
