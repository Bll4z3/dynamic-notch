using System.Globalization;
using System.Text.RegularExpressions;

namespace DynamicNotch.Services.Palette;

/// <summary>
/// A small, safe maths evaluator for the palette (no code is ever run): + − × ÷ ^ %, brackets, implicit
/// multiplication (2(3+4), 2pi), factorial, mod, constants (pi, e) and functions (sqrt, cbrt, abs, round,
/// floor, ceil, sin, cos, tan, asin, acos, atan in degrees, ln, log, exp). "15% of 80" and "80 + 15%" work too.
/// </summary>
public static class MathEval
{
    private static readonly Regex PercentOf = new(@"^\s*([\d.,]+)\s*%\s*of\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AddPercent = new(@"^\s*(.+?)\s*([+\-])\s*([\d.,]+)\s*%\s*$", RegexOptions.Compiled);
    private static readonly Regex LooksLikeMaths = new(@"[\d)]\s*[-+*/×÷^%!x]|\b(sqrt|cbrt|abs|round|floor|ceil|sin|cos|tan|asin|acos|atan|ln|log|exp|pi|mod)\b|^\s*-?\d", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The answer when <paramref name="text"/> is a sum (and not just a number); otherwise null.</summary>
    public static double? TryEvaluate(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || !LooksLikeMaths.IsMatch(text)) return null;
        try
        {
            var m = PercentOf.Match(text);
            if (m.Success && Parse(m.Groups[2].Value) is double whole) return Number(m.Groups[1].Value) / 100 * whole;
            m = AddPercent.Match(text);
            if (m.Success && Parse(m.Groups[1].Value) is double baseValue)
            {
                double pct = Number(m.Groups[3].Value) / 100;
                return m.Groups[2].Value == "+" ? baseValue * (1 + pct) : baseValue * (1 - pct);
            }
            // A bare number is not an answer worth showing.
            if (double.TryParse(text.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return null;
            return Parse(text);
        }
        catch { return null; }
    }

    private static double Number(string s) => double.Parse(s.Replace(",", ""), CultureInfo.InvariantCulture);

    /// <summary>Formats an answer: up to 12 significant digits, no exponent for everyday sizes.</summary>
    public static string Format(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return v.ToString(CultureInfo.InvariantCulture);
        if (Math.Abs(v) >= 1e15 || (Math.Abs(v) < 1e-6 && v != 0)) return v.ToString("G10", CultureInfo.InvariantCulture);
        return Math.Round(v, 10).ToString("#,0.##########", CultureInfo.InvariantCulture);
    }

    private static double? Parse(string text)
    {
        var p = new Parser(text);
        double v = p.Expr();
        p.SkipSpace();
        return p.AtEnd ? v : null;
    }

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;
        public Parser(string s) => _s = s.ToLowerInvariant().Replace("×", "*").Replace("÷", "/").Replace("−", "-").Replace("**", "^");
        public bool AtEnd => _i >= _s.Length;
        public void SkipSpace() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }
        private char Peek() { SkipSpace(); return _i < _s.Length ? _s[_i] : '\0'; }
        private bool Eat(char c) { if (Peek() == c) { _i++; return true; } return false; }
        private bool EatWord(string w)
        {
            SkipSpace();
            if (string.CompareOrdinal(_s, _i, w, 0, w.Length) == 0 && (_i + w.Length >= _s.Length || !char.IsLetter(_s[_i + w.Length]))) { _i += w.Length; return true; }
            return false;
        }

        public double Expr()
        {
            double v = Term();
            while (true)
            {
                if (Eat('+')) v += Term();
                else if (Eat('-')) v -= Term();
                else return v;
            }
        }

        private double Term()
        {
            double v = Unary();
            while (true)
            {
                if (Eat('*') || EatX()) v *= Unary();
                else if (Eat('/')) v /= Unary();
                else if (EatWord("mod")) v %= Unary();
                else if (StartsPrimary()) v *= Unary(); // implicit: 2(3+4), 2pi, (1+2)(3+4)
                else return v;
            }
        }

        /// <summary>"x" between numbers means times (3x4); a lone x is not a variable.</summary>
        private bool EatX()
        {
            SkipSpace();
            if (_i < _s.Length && _s[_i] == 'x' && _i + 1 < _s.Length && (char.IsDigit(_s[_i + 1]) || _s[_i + 1] == ' ' || _s[_i + 1] == '(')) { _i++; return true; }
            return false;
        }

        private bool StartsPrimary()
        {
            char c = Peek();
            return c == '(' || (char.IsLetter(c) && c != 'x' && !EatPeekWord("mod"));
        }

        private bool EatPeekWord(string w) => string.CompareOrdinal(_s, _i, w, 0, w.Length) == 0;

        private double Unary()
        {
            if (Eat('-')) return -Unary();
            if (Eat('+')) return Unary();
            return Power();
        }

        private double Power()
        {
            double b = Postfix();
            if (Eat('^')) return Math.Pow(b, Unary()); // right-associative
            return b;
        }

        private double Postfix()
        {
            double v = Primary();
            while (true)
            {
                if (Eat('%')) v /= 100;
                else if (Eat('!')) v = Factorial(v);
                else return v;
            }
        }

        private double Primary()
        {
            if (Eat('('))
            {
                double v = Expr();
                Eat(')');
                return v;
            }
            SkipSpace();
            int start = _i;
            if (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.'))
            {
                while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.' || _s[_i] == ',')) _i++;
                if (_i < _s.Length && _s[_i] == 'e' && _i + 1 < _s.Length && (char.IsDigit(_s[_i + 1]) || _s[_i + 1] is '-' or '+'))
                {
                    _i += 2;
                    while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                }
                return double.Parse(_s[start.._i].Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            while (_i < _s.Length && char.IsLetter(_s[_i])) _i++;
            string name = _s[start.._i];
            switch (name)
            {
                case "pi": case "π": return Math.PI;
                case "e": return Math.E;
                case "tau": return Math.Tau;
            }
            if (name.Length == 0) throw new FormatException();
            double arg = Peek() == '(' ? Primary() : Unary();
            const double D = Math.PI / 180;
            return name switch
            {
                "sqrt" => Math.Sqrt(arg),
                "cbrt" => Math.Cbrt(arg),
                "abs" => Math.Abs(arg),
                "round" => Math.Round(arg, MidpointRounding.AwayFromZero),
                "floor" => Math.Floor(arg),
                "ceil" => Math.Ceiling(arg),
                "sin" => Math.Sin(arg * D),
                "cos" => Math.Cos(arg * D),
                "tan" => Math.Tan(arg * D),
                "asin" => Math.Asin(arg) / D,
                "acos" => Math.Acos(arg) / D,
                "atan" => Math.Atan(arg) / D,
                "ln" => Math.Log(arg),
                "log" => Math.Log10(arg),
                "exp" => Math.Exp(arg),
                _ => throw new FormatException(),
            };
        }

        private static double Factorial(double n)
        {
            if (n < 0 || n > 170 || n != Math.Floor(n)) throw new FormatException();
            double r = 1;
            for (int k = 2; k <= n; k++) r *= k;
            return r;
        }
    }
}
