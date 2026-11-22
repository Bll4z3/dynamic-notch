using System.Globalization;
using System.Text.RegularExpressions;

namespace DynamicNotch.Services.Palette;

/// <summary>
/// Unit conversions for the palette: "10 km to miles", "180 lbs in kg", "100 f to c", "5 gb in mib".
/// Each unit is a factor to its category's base unit (temperatures are converted through Celsius).
/// </summary>
public static class UnitConvert
{
    private sealed record Unit(string Category, string Symbol, double Factor);

    private static readonly Regex Pattern = new(@"^\s*(-?[\d.,]+)\s*([a-z°/²³0-9 .]+?)\s+(?:to|in|into|as|=)\s+([a-z°/²³0-9 .]+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, Unit> Units = Build();

    private static Dictionary<string, Unit> Build()
    {
        var d = new Dictionary<string, Unit>(StringComparer.OrdinalIgnoreCase);
        void Add(string cat, string symbol, double f, params string[] names)
        {
            var u = new Unit(cat, symbol, f);
            d[symbol] = u;
            foreach (var n in names) d[n] = u;
        }
        // Length (metres)
        Add("length", "mm", 0.001, "millimetre", "millimetres", "millimeter", "millimeters");
        Add("length", "cm", 0.01, "centimetre", "centimetres", "centimeter", "centimeters");
        Add("length", "m", 1, "metre", "metres", "meter", "meters");
        Add("length", "km", 1000, "kilometre", "kilometres", "kilometer", "kilometers", "kms");
        Add("length", "in", 0.0254, "inch", "inches", "\"");
        Add("length", "ft", 0.3048, "foot", "feet", "'");
        Add("length", "yd", 0.9144, "yard", "yards");
        Add("length", "mi", 1609.344, "mile", "miles");
        Add("length", "nmi", 1852, "nautical mile", "nautical miles");
        // Mass (kilograms)
        Add("mass", "mg", 1e-6, "milligram", "milligrams");
        Add("mass", "g", 0.001, "gram", "grams", "gr");
        Add("mass", "kg", 1, "kilogram", "kilograms", "kilo", "kilos", "kgs");
        Add("mass", "t", 1000, "tonne", "tonnes", "ton", "tons");
        Add("mass", "lb", 0.45359237, "lbs", "pound", "pounds");
        Add("mass", "oz", 0.028349523125, "ounce", "ounces");
        Add("mass", "st", 6.35029318, "stone", "stones");
        // Temperature (special)
        Add("temp", "°C", 1, "c", "celsius", "centigrade", "degc", "°c");
        Add("temp", "°F", 1, "f", "fahrenheit", "degf", "°f");
        Add("temp", "K", 1, "k", "kelvin");
        // Speed (m/s)
        Add("speed", "m/s", 1, "mps", "metres per second", "meters per second");
        Add("speed", "km/h", 1 / 3.6, "kph", "kmh", "kmph", "kilometres per hour", "kilometers per hour");
        Add("speed", "mph", 0.44704, "miles per hour");
        Add("speed", "kn", 0.514444444, "knot", "knots", "kt");
        Add("speed", "ft/s", 0.3048, "fps");
        // Data (bytes)
        Add("data", "bit", 0.125, "bits");
        Add("data", "B", 1, "byte", "bytes");
        Add("data", "KB", 1e3, "kilobyte", "kilobytes");
        Add("data", "MB", 1e6, "megabyte", "megabytes");
        Add("data", "GB", 1e9, "gigabyte", "gigabytes", "gig", "gigs");
        Add("data", "TB", 1e12, "terabyte", "terabytes");
        Add("data", "KiB", 1024, "kibibyte");
        Add("data", "MiB", 1048576, "mebibyte");
        Add("data", "GiB", 1073741824, "gibibyte");
        Add("data", "TiB", 1099511627776, "tebibyte");
        Add("data", "Mbit", 125000, "mb/s", "mbps", "megabit", "megabits");
        Add("data", "Gbit", 125000000, "gbps", "gigabit", "gigabits");
        // Time (seconds)
        Add("time", "ms", 0.001, "millisecond", "milliseconds");
        Add("time", "s", 1, "sec", "secs", "second", "seconds");
        Add("time", "min", 60, "mins", "minute", "minutes");
        Add("time", "h", 3600, "hr", "hrs", "hour", "hours");
        Add("time", "day", 86400, "days", "d");
        Add("time", "week", 604800, "weeks", "wk", "wks");
        Add("time", "month", 2629746, "months");
        Add("time", "year", 31556952, "years", "yr", "yrs");
        // Volume (litres)
        Add("volume", "ml", 0.001, "millilitre", "millilitres", "milliliter", "milliliters");
        Add("volume", "cl", 0.01, "centilitre", "centilitres");
        Add("volume", "l", 1, "litre", "litres", "liter", "liters");
        Add("volume", "m³", 1000, "m3", "cubic metre", "cubic metres");
        Add("volume", "gal", 3.785411784, "gallon", "gallons", "us gal");
        Add("volume", "uk gal", 4.54609, "imperial gallon", "imperial gallons");
        Add("volume", "qt", 0.946352946, "quart", "quarts");
        Add("volume", "pt", 0.473176473, "pint", "pints");
        Add("volume", "cup", 0.2365882365, "cups");
        Add("volume", "fl oz", 0.0295735295625, "floz", "fluid ounce", "fluid ounces");
        Add("volume", "tbsp", 0.01478676478125, "tablespoon", "tablespoons");
        Add("volume", "tsp", 0.00492892159375, "teaspoon", "teaspoons");
        // Area (square metres)
        Add("area", "m²", 1, "m2", "sqm", "square metre", "square metres", "square meter", "square meters");
        Add("area", "km²", 1e6, "km2", "sqkm", "square kilometre", "square kilometres");
        Add("area", "ft²", 0.09290304, "ft2", "sqft", "square foot", "square feet");
        Add("area", "in²", 0.00064516, "in2", "sqin", "square inch", "square inches");
        Add("area", "mi²", 2589988.110336, "mi2", "sqmi", "square mile", "square miles");
        Add("area", "acre", 4046.8564224, "acres", "ac");
        Add("area", "ha", 10000, "hectare", "hectares");
        return d;
    }

    /// <summary>(answer text, the value, a one-line description) or null when it isn't a unit conversion.</summary>
    public static (double value, string text, string detail)? TryConvert(string query)
    {
        var m = Pattern.Match(query);
        if (!m.Success) return null;
        if (!double.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double amount)) return null;
        if (!Units.TryGetValue(m.Groups[2].Value.Trim(), out var from) || !Units.TryGetValue(m.Groups[3].Value.Trim(), out var to)) return null;
        if (from.Category != to.Category) return null;
        double result = from.Category == "temp" ? Temperature(amount, from.Symbol, to.Symbol) : amount * from.Factor / to.Factor;
        string text = $"{MathEval.Format(Math.Round(result, 6))} {to.Symbol}";
        return (result, text, $"{MathEval.Format(amount)} {from.Symbol} = {text}");
    }

    private static double Temperature(double v, string from, string to)
    {
        double c = from switch { "°F" => (v - 32) * 5 / 9, "K" => v - 273.15, _ => v };
        return to switch { "°F" => c * 9 / 5 + 32, "K" => c + 273.15, _ => c };
    }
}
