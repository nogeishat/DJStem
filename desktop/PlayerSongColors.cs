using System;
using System.Drawing;

internal static class PlayerSongColors
{
    internal static Color ButtonText(Color background)
    {
        return ColorContrast(background, Color.White) >= ColorContrast(background, Color.Black) ? Color.White : Color.Black;
    }
    internal static Color Select(Image image, Color original)
    {
        if (image == null) return PlayerTheme.Purple;
        // Group nearby RGB shades instead of selecting one exact photographic pixel.
        int[] counts = new int[512], red = new int[512], green = new int[512], blue = new int[512];
        int winner = 0;
        using (Bitmap sample = new Bitmap(48, 48))
        {
            using (Graphics graphics = Graphics.FromImage(sample)) graphics.DrawImage(image, 0, 0, 48, 48);
            for (int y = 0; y < sample.Height; y++) for (int x = 0; x < sample.Width; x++)
            {
                Color pixel = sample.GetPixel(x, y); if (pixel.A < 128) continue;
                int bucket = (pixel.R >> 5) * 64 + (pixel.G >> 5) * 8 + (pixel.B >> 5);
                counts[bucket]++; red[bucket] += pixel.R; green[bucket] += pixel.G; blue[bucket] += pixel.B;
            }
        }
        for (int bucket = 1; bucket < counts.Length; bucket++) if (counts[bucket] > counts[winner]) winner = bucket;
        int colorful = -1, distinctColorful = -1;
        int alternative = -1, distinct = -1, distinctExtreme = -1, oppositeNeutral = -1;
        int originalTone = original.IsEmpty ? 0 : NeutralTone(original);
        bool neutralCover = true;
        for (int bucket = 0; bucket < counts.Length; bucket++)
        {
            if (counts[bucket] == 0) continue;
            Color candidate = Color.FromArgb(red[bucket] / counts[bucket], green[bucket] / counts[bucket], blue[bucket] / counts[bucket]);
            Color readable = ReadableColor(candidate);
            int tone = NeutralTone(readable);
            if (tone == 0) neutralCover = false;
            else if (originalTone != 0 && tone == -originalTone && (oppositeNeutral < 0 || counts[bucket] > counts[oppositeNeutral])) oppositeNeutral = bucket;
            bool similar = original.IsEmpty || SimilarColors(readable, original);
            // Judge the displayed shade: brightening can wash a dark cover color
            // toward gray, and pale tints should not outrank a colorful accent.
            if (ColorChroma(readable) >= 25)
            {
                if (colorful < 0 || counts[bucket] > counts[colorful]) colorful = bucket;
                if (!similar && (distinctColorful < 0 || counts[bucket] > counts[distinctColorful])) distinctColorful = bucket;
            }
            if (NearBlackOrWhite(candidate))
            {
                if (!similar && (distinctExtreme < 0 || counts[bucket] > counts[distinctExtreme])) distinctExtreme = bucket;
                continue;
            }
            if (alternative < 0 || counts[bucket] > counts[alternative]) alternative = bucket;
            if (!similar && (distinct < 0 || counts[bucket] > counts[distinct])) distinct = bucket;
        }
        // Fall back to the overall winner when the cover has no other shade.
        if (alternative >= 0) winner = alternative;
        // Choose by population among visibly distinct shades.
        if (distinct >= 0) winner = distinct;
        else if (distinctExtreme >= 0) winner = distinctExtreme;
        // Prefer the most common colorful shade over every neutral option.
        // If all colorful shades clash with the other song, use white even when
        // the cover has no white pixels instead of keeping a matching accent.
        if (colorful >= 0)
        {
            if (!original.IsEmpty && distinctColorful < 0) return Color.White;
            winner = distinctColorful >= 0 ? distinctColorful : colorful;
        }
        Color color = counts[winner] == 0 ? PlayerTheme.Purple : Color.FromArgb(red[winner] / counts[winner], green[winner] / counts[winner], blue[winner] / counts[winner]);
        if (distinct < 0 && distinctExtreme < 0 && neutralCover && originalTone != 0 && counts[winner] > 0)
        {
            // A monochrome pair uses opposing tones, even if the added cover only
            // contains the original tone. Dark accents still need to be readable.
            color = oppositeNeutral >= 0
                ? Color.FromArgb(red[oppositeNeutral] / counts[oppositeNeutral], green[oppositeNeutral] / counts[oppositeNeutral], blue[oppositeNeutral] / counts[oppositeNeutral])
                : originalTone > 0 ? Color.Black : Color.White;
        }
        // Keep dark cover colors readable against the information cards, using one
        // shared accent for the title, source swatch, and corresponding waveform.
        color = ReadableColor(color);
        return color;
    }

    private static Color ReadableColor(Color dominant)
    {
        Color color = dominant;
        for (float lift = .05f; ColorContrast(color, PlayerTheme.Glass) < 4.5 && lift <= 1; lift += .05f) color = PlayerTheme.Blend(dominant, Color.White, lift);
        return color;
    }

    private static bool SimilarColors(Color first, Color second)
    {
        double[] a = ColorLab(first), b = ColorLab(second);
        // Neutral shades still look alike even when their lightness differs a lot.
        double firstChroma = Math.Sqrt(a[1] * a[1] + a[2] * a[2]);
        double secondChroma = Math.Sqrt(b[1] * b[1] + b[2] * b[2]);
        if (firstChroma < 15 && secondChroma < 15) return true;
        return Math.Sqrt(Math.Pow(a[0] - b[0], 2) + Math.Pow(a[1] - b[1], 2) + Math.Pow(a[2] - b[2], 2)) < 30;
    }
    private static double ColorChroma(Color color)
    {
        double[] lab = ColorLab(color);
        return Math.Sqrt(lab[1] * lab[1] + lab[2] * lab[2]);
    }
    private static int NeutralTone(Color color)
    {
        double[] lab = ColorLab(color);
        if (Math.Sqrt(lab[1] * lab[1] + lab[2] * lab[2]) >= 15) return 0;
        // Readability lifts black into gray; it still belongs to the dark family.
        return lab[0] >= 75 ? 1 : -1;
    }
    private static double[] ColorLab(Color color)
    {
        double r = LinearColor(color.R), g = LinearColor(color.G), b = LinearColor(color.B);
        double x = LabComponent((.4124564 * r + .3575761 * g + .1804375 * b) / .95047);
        double y = LabComponent(.2126729 * r + .7151522 * g + .0721750 * b);
        double z = LabComponent((.0193339 * r + .1191920 * g + .9503041 * b) / 1.08883);
        return new[] { 116 * y - 16, 500 * (x - y), 200 * (y - z) };
    }
    private static double LabComponent(double value)
    {
        return value > .008856451679 ? Math.Pow(value, 1.0 / 3) : 7.787037037 * value + 16.0 / 116;
    }

    private static bool NearBlackOrWhite(Color color)
    {
        return Math.Max(color.R, Math.Max(color.G, color.B)) <= 48 || Math.Min(color.R, Math.Min(color.G, color.B)) >= 208;
    }

    private static double ColorContrast(Color a, Color b)
    {
        double first = ColorLuminance(a), second = ColorLuminance(b);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }
    private static double ColorLuminance(Color color)
    {
        return .2126 * LinearColor(color.R) + .7152 * LinearColor(color.G) + .0722 * LinearColor(color.B);
    }
    private static double LinearColor(byte channel)
    {
        double value = channel / 255.0;
        return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
    }

}
