using System;
using System.Collections.Generic;
using System.Linq;

// These are editing instructions, never an audio export or a reference to a
// temporary rendered cache. Prepared stems can be rebuilt from the source songs.
internal sealed class SavedStemTiming
{
    public double Shift, LoopStart, LoopEnd, LoopAnchor, LoopShift;
    internal static SavedStemTiming From(StemTiming value) { return new SavedStemTiming { Shift = value.Shift, LoopStart = value.LoopStart, LoopEnd = value.LoopEnd, LoopAnchor = value.LoopAnchor, LoopShift = value.LoopShift }; }
    internal StemTiming Live() { return new StemTiming { Shift = Shift, LoopStart = LoopStart, LoopEnd = LoopEnd, LoopAnchor = LoopAnchor, LoopShift = LoopShift }; }
}

internal sealed class SavedStemBank
{
    public string Audio;
    public double Tempo, Offset, Pitch, MatchedBpm;
    internal StemMatchPlan Plan() { return new StemMatchPlan { Tempo = Tempo, Offset = Offset, Pitch = Pitch, MatchedBpm = MatchedBpm }; }
}

internal sealed class SavedMix
{
    public int Version = 1;
    public string Id, Name, OriginalAudio, OriginalTitle, OriginalArtist, AddedAudio, AddedTitle;
    public string[] Sources;
    public List<SavedStemBank> Banks;
    public bool[] Muted, Solo;
    public float[] Levels;
    public SavedStemTiming[] Timing;
    public int Volume;
    public bool OriginalMode;
    public double OriginalTempo = 1;
    public double? Bpm;
    public Dictionary<string, int> KeyShifts;

    private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
    internal bool Valid()
    {
        if (Version != 1 || String.IsNullOrWhiteSpace(Id) || String.IsNullOrWhiteSpace(Name) || String.IsNullOrWhiteSpace(OriginalAudio) || Sources == null || Sources.Length != 4 || Muted == null || Muted.Length != 4 || Solo == null || Solo.Length != 4 || Levels == null || Levels.Length != 4 || Timing == null || Timing.Length != 4 || Banks == null || KeyShifts == null || Volume < 0 || Volume > 100 || !Finite(OriginalTempo) || OriginalTempo <= 0) return false;
        if (Bpm.HasValue && (!Finite(Bpm.Value) || Bpm < 30 || Bpm > 300)) return false;
        if (Banks.Any(bank => bank == null || String.IsNullOrEmpty(bank.Audio) || !Finite(bank.Tempo) || bank.Tempo <= 0 || !Finite(bank.Pitch) || bank.Pitch <= 0 || !Finite(bank.Offset) || !Finite(bank.MatchedBpm))) return false;
        if (Banks.Select(bank => bank.Audio).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Banks.Count) return false;
        if (!String.IsNullOrEmpty(AddedAudio) && !Banks.Any(bank => String.Equals(bank.Audio, AddedAudio, StringComparison.OrdinalIgnoreCase))) return false;
        for (int lane = 0; lane < 4; lane++)
        {
            if (!String.IsNullOrEmpty(Sources[lane]) && !Banks.Any(bank => String.Equals(bank.Audio, Sources[lane], StringComparison.OrdinalIgnoreCase))) return false;
            SavedStemTiming timing = Timing[lane];
            if (!Finite(Levels[lane]) || Levels[lane] < 0 || Levels[lane] > 1 || timing == null || !new[] { timing.Shift, timing.LoopStart, timing.LoopEnd, timing.LoopAnchor, timing.LoopShift }.All(Finite) || timing.LoopStart < 0 || timing.LoopEnd < timing.LoopStart || timing.LoopAnchor < 0) return false;
        }
        return KeyShifts.All(pair => !String.IsNullOrEmpty(pair.Key) && pair.Value >= -12 && pair.Value <= 12);
    }
}
