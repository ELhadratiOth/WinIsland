namespace WinIsland.Core.Media;

/// <summary>
/// Picks a vivid accent colour from cover art (like the iOS island tinting its waveform),
/// tuned to read well on the island's black background.
/// </summary>
public static class AccentPicker
{
    /// <summary>Used when the art is monochrome or missing.</summary>
    public const uint Neutral = 0xFFE5E5EA;

    private const int HueBins = 12;

    /// <param name="bgra">Pixels in BGRA order (4 bytes each), any size; a 24×24 thumbnail is plenty.</param>
    public static uint Pick(ReadOnlySpan<byte> bgra)
    {
        Span<double> weight = stackalloc double[HueBins];
        Span<double> r = stackalloc double[HueBins];
        Span<double> g = stackalloc double[HueBins];
        Span<double> b = stackalloc double[HueBins];

        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            double pb = bgra[i] / 255.0, pg = bgra[i + 1] / 255.0, pr = bgra[i + 2] / 255.0, alpha = bgra[i + 3] / 255.0;
            (double h, double s, double v) = ToHsv(pr, pg, pb);

            // Vivid, reasonably bright pixels say the most about a cover's colour.
            if (alpha < 0.5 || s < 0.25 || v < 0.2)
            {
                continue;
            }

            int bin = Math.Min(HueBins - 1, (int)(h / 360.0 * HueBins));
            double w = s * v;
            weight[bin] += w;
            r[bin] += pr * w;
            g[bin] += pg * w;
            b[bin] += pb * w;
        }

        int best = -1;
        for (int i = 0; i < HueBins; i++)
        {
            if (weight[i] > 0 && (best < 0 || weight[i] > weight[best]))
            {
                best = i;
            }
        }

        if (best < 0)
        {
            return Neutral;
        }

        (double hue, double sat, double val) = ToHsv(r[best] / weight[best], g[best] / weight[best], b[best] / weight[best]);

        // Lift it so it glows on black without turning neon.
        sat = Math.Clamp(sat, 0.45, 0.85);
        val = Math.Clamp(val, 0.85, 1.0);
        (double fr, double fg, double fb) = FromHsv(hue, sat, val);
        return 0xFF000000u | (Byte(fr) << 16) | (Byte(fg) << 8) | Byte(fb);
    }

    private static uint Byte(double c) => (uint)Math.Round(Math.Clamp(c, 0, 1) * 255);

    private static (double H, double S, double V) ToHsv(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double h = 0;
        if (delta > 0)
        {
            if (max == r)
            {
                h = 60 * (((g - b) / delta) % 6);
            }
            else if (max == g)
            {
                h = 60 * (((b - r) / delta) + 2);
            }
            else
            {
                h = 60 * (((r - g) / delta) + 4);
            }
        }

        if (h < 0)
        {
            h += 360;
        }

        return (h, max == 0 ? 0 : delta / max, max);
    }

    private static (double R, double G, double B) FromHsv(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60 % 2) - 1));
        double m = v - c;
        (double r, double g, double b) = (h % 360) switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return (r + m, g + m, b + m);
    }
}
