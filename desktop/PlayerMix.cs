using System;

// A single snapshot keeps presets, mute, solo and faders consistent in the audio callback.
internal sealed class PlayerMix
{
    internal readonly bool[] Muted = new bool[4], Solo = new bool[4];
    internal readonly float[] Levels = { 1, 1, 1, 1 };
    internal string Preset = "all";
    internal bool Audible(int index)
    {
        bool anySolo = Array.Exists(Solo, value => value);
        return !Muted[index] && (!anySolo || Solo[index]);
    }
    internal void ToggleMute(int index) { Muted[index] = !Muted[index]; if (Muted[index]) Solo[index] = false; Preset = "custom"; }
    internal void ToggleSolo(int index) { Solo[index] = !Solo[index]; if (Solo[index]) Muted[index] = false; Preset = "custom"; }
    internal void ApplyPreset(string preset)
    {
        if (preset != "all" && preset != "karaoke" && preset != "acapella" && preset != "drumnbass") throw new ArgumentException("Unknown mix preset.");
        for (int i = 0; i < 4; i++)
        {
            Muted[i] = false;
            Solo[i] = preset == "karaoke" ? i != 0 : preset == "acapella" ? i == 0 : preset == "drumnbass" && (i == 1 || i == 2);
        }
        Preset = preset;
    }
}
