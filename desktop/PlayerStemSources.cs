using System;
using System.IO;

internal sealed class StemSourceBank
{
    internal SongInfo Song;
    internal MusicTraits Traits;
    internal StemMatchPlan Plan;
    internal string[] Files;
    internal float[][] Peaks;
    internal float LoudnessGain = 1;
    // Files are immutable disk sources; Tuning describes the audible timeline.
    internal PlaybackTransform[] Tuning;
}

// Staging affects every lane's next choice, but only the dropped lane switches.
// Older active sources remain audible until their own toggle is pressed.
internal sealed class StemSourceState
{
    internal readonly string OriginalFolder;
    internal readonly string[] OriginalFiles;
    internal StemSourceBank Available;
    internal readonly StemSourceBank[] Active = new StemSourceBank[4];
    internal readonly float[][] OriginalPeaks = new float[4][];
    internal readonly float[][] NativePeaks = new float[4][];
    internal PlaybackTransform[] OriginalTuning;
    internal StemSourceState(string originalFolder, string[] originalFiles = null) { OriginalFolder = originalFolder; OriginalFiles = originalFiles; PlayerMatchedCache.Keep(this, originalFiles); }
    internal string Original(int lane) { return OriginalFiles == null ? Path.Combine(OriginalFolder, StemMixer.StemNames[lane] + ".wav") : OriginalFiles[lane]; }
    internal string File(int lane) { return Active[lane] == null ? Original(lane) : Active[lane].Files[lane]; }
    internal PlaybackTransform Transform(int lane) { return Active[lane] == null ? OriginalTuning == null ? null : OriginalTuning[lane] : Active[lane].Tuning == null ? null : Active[lane].Tuning[lane]; }
    internal float[] Peaks(int lane) { return Active[lane] == null ? OriginalPeaks[lane] : Active[lane].Peaks[lane]; }
    internal string ToggleFile(int lane) { return Active[lane] != null || Available == null ? Original(lane) : Available.Files[lane]; }
    internal void Stage(StemSourceBank bank, int lane)
    {
        if (lane < 0 || lane >= 4 || bank == null || bank.Song == null || bank.Files == null || bank.Files.Length != 4 || bank.Peaks == null || bank.Peaks.Length != 4 || Array.Exists(bank.Files, String.IsNullOrEmpty)) throw new ArgumentException("A replacement song needs all four prepared stems.");
        Available = bank; Active[lane] = bank;
    }
    internal void Toggle(int lane) { Active[lane] = Active[lane] == null ? Available : null; }
    internal void Reset() { Available = null; Array.Clear(Active, 0, Active.Length); }
}
