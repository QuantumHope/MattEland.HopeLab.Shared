using System.Globalization;
using System.Text.RegularExpressions;

namespace MattEland.HopeLab.Themes.Tokens;

/// <summary>
/// An sRGB colour with alpha, as written in a token file: <c>#RGB</c>, <c>#RRGGBB</c>, <c>#RRGGBBAA</c>,
/// <c>rgb()</c>/<c>rgba()</c>, <c>transparent</c>, <c>white</c> or <c>black</c>.
/// </summary>
public readonly partial record struct TokenColor(byte A, byte R, byte G, byte B)
{
    public static TokenColor White { get; } = new(255, 255, 255, 255);
    public static TokenColor Black { get; } = new(255, 0, 0, 0);

    /// <summary>The colour in a CSS value, or null when the value is anything else (a gradient, a reference, a mix).</summary>
    public static TokenColor? Parse(string? value)
    {
        if (value is null) return null;
        var s = value.Trim();
        switch (s.ToLowerInvariant())
        {
            case "transparent": return new TokenColor(0, 0, 0, 0);
            case "white": return White;
            case "black": return Black;
        }

        if (s.StartsWith('#'))
        {
            var h = s[1..];
            if (!h.All(Uri.IsHexDigit)) return null;
            if (h.Length is 3 or 4) h = string.Concat(h.Select(c => $"{c}{c}"));
            if (h.Length is not (6 or 8)) return null;
            byte P(int i) => byte.Parse(h.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new TokenColor(h.Length == 8 ? P(6) : (byte)255, P(0), P(2), P(4));
        }

        var m = RgbPattern().Match(s);
        if (!m.Success) return null;
        byte Channel(int g) => (byte)Math.Clamp(Math.Round(double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture)), 0, 255);
        byte alpha = 255;
        if (m.Groups[4].Success)
        {
            var a = m.Groups[4].Value;
            var f = a.EndsWith('%')
                ? double.Parse(a[..^1], CultureInfo.InvariantCulture) / 100
                : double.Parse(a, CultureInfo.InvariantCulture);
            alpha = (byte)Math.Clamp(Math.Round(f * 255), 0, 255);
        }
        return new TokenColor(alpha, Channel(1), Channel(2), Channel(3));
    }

    [GeneratedRegex(@"^rgba?\(\s*([\d.]+)[\s,]+([\d.]+)[\s,]+([\d.]+)(?:\s*[,/]\s*([\d.]+%?))?\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex RgbPattern();

    /// <summary>Avalonia's form, <c>#AARRGGBB</c>.</summary>
    public override string ToString() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";

    public TokenColor WithAlpha(byte alpha) => this with { A = alpha };

    /// <summary>This colour moved <paramref name="amount"/> (0 to 1) of the way towards <paramref name="other"/>.</summary>
    public TokenColor Mix(TokenColor other, double amount)
    {
        byte L(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return new TokenColor(A, L(R, other.R), L(G, other.G), L(B, other.B));
    }

    /// <summary>WCAG 2 relative luminance (alpha ignored).</summary>
    public double Luminance
    {
        get
        {
            static double Lin(byte v)
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Lin(R) + 0.7152 * Lin(G) + 0.0722 * Lin(B);
        }
    }

    /// <summary>WCAG 2 contrast ratio against another colour, 1 to 21.</summary>
    public double Contrast(TokenColor other)
    {
        var (hi, lo) = Luminance >= other.Luminance ? (Luminance, other.Luminance) : (other.Luminance, Luminance);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>CIE76 ΔE in Lab (D65): coarse, and enough to tell two accents apart.</summary>
    public double DeltaE(TokenColor other)
    {
        var (l1, a1, b1) = Lab();
        var (l2, a2, b2) = other.Lab();
        return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
    }

    private (double L, double A, double B) Lab()
    {
        static double Lin(byte v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        double r = Lin(R), g = Lin(G), b = Lin(B);
        var x = (r * 0.4124 + g * 0.3576 + b * 0.1805) / 0.95047;
        var y = r * 0.2126 + g * 0.7152 + b * 0.0722;
        var z = (r * 0.0193 + g * 0.1192 + b * 0.9505) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
        return (116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
    }

    /// <summary>
    /// This colour, darkened (on a light ground) or lightened (on a dark one) in small steps until it reaches
    /// <paramref name="ratio"/> against <paramref name="ground"/>.
    /// </summary>
    public TokenColor AtLeast(double ratio, TokenColor ground)
    {
        var c = this;
        var towards = ground.Luminance > 0.5 ? Black : White;
        for (var i = 0; i < 40 && c.Contrast(ground) < ratio; i++) c = c.Mix(towards, 0.05);
        return c;
    }
}
