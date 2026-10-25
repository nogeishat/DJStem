using System;

// Background readers/renders report work here; only the UI timer touches controls.
internal sealed class StemLoadingProgress
{
    private readonly object gate = new object();
    private readonly double[] lanes = new double[4];
    internal double Read(int lane) { lock (gate) return lanes[lane]; }
    internal void Report(int lane, double value)
    {
        if (Double.IsNaN(value) || Double.IsInfinity(value)) return;
        lock (gate) lanes[lane] = Math.Max(lanes[lane], Math.Max(0, Math.Min(1, value)));
    }
    internal void All(double value) { for (int lane = 0; lane < 4; lane++) Report(lane, value); }
    internal Action<double> Range(int lane, double start, double end) { return value => Report(lane, start + Math.Max(0, Math.Min(1, value)) * (end - start)); }
    internal Action<double> AllRange(double start, double end) { return value => All(start + Math.Max(0, Math.Min(1, value)) * (end - start)); }
}

internal sealed partial class PlayerForm
{
    private StemLoadingProgress replacementLoadingProgress, autoLoadingProgress;

    private void UpdateStemLoadingButtons()
    {
        if (IsDisposed) return;
        StemLoadingProgress progress = preparingReplacement ? replacementLoadingProgress : preparingAutoPlay ? autoLoadingProgress : null;
        var color = progress == null ? System.Drawing.Color.Empty : SongColor(current);
        for (int lane = 0; lane < 4; lane++)
        {
            GlassButton button = sourceButtons[lane];
            if (button == null) continue;
            double value = progress == null ? 1 : progress.Read(lane);
            if (progress != null) button.LoadingColor = color;
            button.LoadingProgress = value >= 1 ? -1 : value;
        }
    }
}
