using System;
using System.Drawing;
using System.IO;
using System.Threading;
using NAudio.Wave;

internal static class PlayerTests
{
    private static int checks;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    private static Color CoverColor(Color original, Color[] colors, int[] widths)
    {
        using (Bitmap cover = new Bitmap(48, 48))
        {
            int left = 0;
            for (int i = 0; i < colors.Length; i++)
            {
                for (int x = left; x < left + widths[i]; x++) for (int y = 0; y < 48; y++) cover.SetPixel(x, y, colors[i]);
                left += widths[i];
            }
            return PlayerSongColors.Select(cover, original);
        }
    }
    private static void CheckSongColors()
    {
        Color gray = Color.FromArgb(140, 140, 140), warmGray = Color.FromArgb(180, 176, 170);
        Color blue = Color.FromArgb(96, 165, 250), green = PlayerTheme.Green, red = Color.FromArgb(248, 113, 113);
        Color[] neutralCover = { gray, warmGray, green, red };
        int[] populations = { 22, 14, 8, 4 };
        Check(CoverColor(Color.Empty, neutralCover, populations).ToArgb() == green.ToArgb(), "An unpaired song must prefer the most common colorful shade over dominant gray and warm gray");
        Check(CoverColor(Color.White, neutralCover, populations).ToArgb() == green.ToArgb(), "White must skip dominant gray and warm gray for the most common distinct shade");
        Check(CoverColor(blue, neutralCover, populations).ToArgb() == green.ToArgb(), "A paired song must prefer a distinct colorful shade even when dominant gray is already distinct");
        Color palePink = Color.FromArgb(235, 215, 225), paleBlue = Color.FromArgb(215, 225, 240);
        Check(CoverColor(Color.Empty, new[] { palePink, paleBlue, green }, new[] { 24, 18, 6 }).ToArgb() == green.ToArgb(), "Pale tints near white and gray must not displace a smaller colorful area");
        Check(CoverColor(Color.Empty, new[] { Color.White, blue }, new[] { 42, 6 }).ToArgb() == blue.ToArgb(), "A mostly white cover must still use its colorful accent");
        Check(CoverColor(gray, new[] { Color.White, warmGray, blue }, new[] { 24, 18, 6 }).ToArgb() == blue.ToArgb(), "Gray must skip white and another neutral shade");
        Check(CoverColor(blue, new[] { Color.FromArgb(102, 170, 242), green, red }, new[] { 30, 12, 6 }).ToArgb() == green.ToArgb(), "Similar chromatic shades must be skipped in population order");
        Check(CoverColor(red, new[] { blue, green }, new[] { 32, 16 }).ToArgb() == blue.ToArgb(), "A distinct preferred color must remain selected");
        Color darkBlue = Color.FromArgb(16, 32, 100);
        Color readableBlue = CoverColor(Color.Empty, new[] { darkBlue }, new[] { 48 });
        Check(CoverColor(readableBlue, new[] { darkBlue, red }, new[] { 32, 16 }).ToArgb() == red.ToArgb(), "Similarity must use the displayed color after readability brightening");
        Check(CoverColor(blue, new[] { blue, Color.White }, new[] { 32, 16 }).ToArgb() == Color.White.ToArgb(), "A distinct white shade must be usable when no other alternative exists");
        Check(CoverColor(blue, new[] { blue }, new[] { 48 }).ToArgb() == Color.White.ToArgb(), "Matching colorful artwork must fall back to white even when the cover contains no white");
        Check(CoverColor(blue, new[] { gray, Color.FromArgb(102, 170, 242) }, new[] { 32, 16 }).ToArgb() == Color.White.ToArgb(), "Gray must not replace white as the fallback when the only colorful shade matches the other song");
        Check(CoverColor(blue, new[] { Color.White, blue, red }, new[] { 32, 12, 4 }).ToArgb() == red.ToArgb(), "A small distinct colorful area must beat dominant white and a matching colorful shade");
        Check(CoverColor(Color.White, new[] { gray, Color.White }, new[] { 32, 16 }).ToArgb() == gray.ToArgb(), "A white original must select an available dark gray shade from monochrome artwork");
        Color readableBlack = CoverColor(Color.Empty, new[] { Color.Black }, new[] { 48 });
        Check(readableBlack.R > 0 && readableBlack.R < 180, "Black cover accents must remain readable as dark gray on the player background");
        Check(CoverColor(Color.White, new[] { Color.White, Color.Black }, new[] { 40, 8 }).ToArgb() == readableBlack.ToArgb(), "A white original must skip dominant white for the added cover's black shade");
        Check(CoverColor(readableBlack, new[] { Color.Black, Color.White }, new[] { 40, 8 }).ToArgb() == Color.White.ToArgb(), "An original black shade brightened for display must select the added cover's white shade");
        Check(CoverColor(Color.White, new[] { Color.White }, new[] { 48 }).ToArgb() == readableBlack.ToArgb(), "All-white added artwork must use the opposite dark fallback against a white original");
        Check(CoverColor(readableBlack, new[] { Color.Black }, new[] { 48 }).ToArgb() == Color.White.ToArgb(), "All-black added artwork must use the opposite white fallback against a black original");
        Check(CoverColor(readableBlack, new[] { gray }, new[] { 48 }).ToArgb() == Color.White.ToArgb(), "An added cover containing only dark gray must use white against an original black shade");
        Color nearWhite = Color.FromArgb(235, 235, 235), darkGray = Color.FromArgb(180, 180, 180);
        Check(CoverColor(nearWhite, new[] { nearWhite, gray, darkGray }, new[] { 32, 10, 6 }).ToArgb() == gray.ToArgb(), "Monochrome fallback must choose the most common shade in the opposite family");
        Check(CoverColor(blue, new[] { gray }, new[] { 48 }).ToArgb() == gray.ToArgb(), "A chromatic original must preserve the added cover's neutral accent");
        Check(CoverColor(Color.White, new[] { Color.Transparent }, new[] { 48 }).ToArgb() == PlayerTheme.Purple.ToArgb(), "Empty artwork must retain the default accent");
    }
    private static float[] Read(StemMixer mixer, int samples) { float[] result = new float[samples]; Check(mixer.Read(result, 0, samples) == samples, "Expected the requested number of stereo samples"); return result; }
    private static void Write(string file, int rate, int frames, float value, float slope)
    {
        using (WaveFileWriter writer = new WaveFileWriter(file, new WaveFormat(rate, 24, 2)))
        for (int frame = 0; frame < frames; frame++) { writer.WriteSample(value + slope * frame); writer.WriteSample(value + slope * frame); }
    }
    private static void WriteTone(string file, int rate, int frames, double frequency)
    {
        using (WaveFileWriter writer = new WaveFileWriter(file, new WaveFormat(rate, 24, 2)))
        for (int i = 0; i < frames; i++) { float sample = (float)(.25 * Math.Sin(2 * Math.PI * frequency * i / rate)); writer.WriteSample(sample); writer.WriteSample(sample); }
    }
    private static double ToneFrequency(string file, double start, double seconds)
    {
        using (WaveFileReader reader = new WaveFileReader(file))
        {
            reader.CurrentTime = TimeSpan.FromSeconds(start); var provider = StemMixer.Samples(reader); int frames = (int)(seconds * reader.WaveFormat.SampleRate);
            float[] samples = new float[frames * 2]; int read = provider.Read(samples, 0, samples.Length), crossings = 0;
            for (int i = 2; i < read; i += 2) if (samples[i - 2] <= 0 && samples[i] > 0) crossings++;
            return crossings / ((double)(read / 2) / reader.WaveFormat.SampleRate);
        }
    }
    private static void WritePulses(string file)
    {
        const int rate = 22050;
        using (WaveFileWriter writer = new WaveFileWriter(file, new WaveFormat(rate, 24, 2)))
        for (int frame = 0; frame < rate * 10; frame++)
        {
            double phase = (frame / (double)rate - .2) % 1;
            float value = phase >= 0 && phase < .04 ? (float)(.7 * Math.Sin(2 * Math.PI * 1000 * frame / rate) * Math.Sin(Math.PI * phase / .04)) : 0;
            writer.WriteSample(value); writer.WriteSample(value);
        }
    }
    private static System.Collections.Generic.List<double> PulseStarts(string file)
    {
        using (WaveFileReader reader = new WaveFileReader(file))
        {
            var provider = StemMixer.Samples(reader); float[] samples = new float[(int)reader.Length / 3]; int read = provider.Read(samples, 0, samples.Length);
            var starts = new System.Collections.Generic.List<double>(); double last = -1;
            for (int i = 0; i < read; i += 2)
            {
                double time = i / 2.0 / reader.WaveFormat.SampleRate;
                if (Math.Abs(samples[i]) > .12 && time - last > .3) { starts.Add(time); last = time; }
            }
            return starts;
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        string folder = Path.Combine(Path.GetTempPath(), "stemkit-audio-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            CheckSongColors();
            PlayerMatchedCacheTests.Run(folder, Check);
            string audio = Path.Combine(folder, "original.wav");
            Write(audio, 48000, 96000, 0.2f, 0);
            float[] levels = { 0.03125f, 0.0625f, 0.125f, 0.25f };
            for (int i = 0; i < 4; i++) Write(Path.Combine(folder, StemMixer.StemNames[i] + ".wav"), 44100, 88200, levels[i], i == 2 ? 0.000001f : 0);
            using (StemMixer mixer = new StemMixer(audio, folder))
            {
                Check(mixer.HasStems && mixer.WaveFormat.SampleRate == 44100 && mixer.WaveFormat.Channels == 2, "Expected a single stereo timeline at the stems' sample rate");
                Check(mixer.TotalFrames == 88200, "Duration must come from the actual stem frame count");
                mixer.Volume = 1;
                float[] all = Read(mixer, 4096);
                Check(Math.Abs(all[0] - 0.46875f) < 0.00001, "All enabled stems must sum once, with no original MP3 added");
                mixer.EnableStem(0, false); Read(mixer, 4096);
                float[] muted = Read(mixer, 4096);
                float expected = 0.4375f + mixer.PositionFrames * 0.000001f - 2048 * 0.000001f;
                Check(Math.Abs(muted[0] - expected) < 0.00001, "Muting vocals must remove only that stem");
                for (int i = 0; i < 4; i++) mixer.EnableStem(i, false);
                Read(mixer, 4096); long before = mixer.PositionFrames;
                float[] silence = Read(mixer, 4096);
                Check(Array.TrueForAll(silence, value => value == 0), "All stems off must produce silence");
                Check(mixer.PositionFrames == before + 2048, "The shared timeline must advance during silence");
                mixer.EnableStem(2, true); Read(mixer, 4096);
                long resumedFrame = mixer.PositionFrames; float[] bass = Read(mixer, 4096);
                Check(Math.Abs(bass[0] - (0.125f + resumedFrame * 0.000001f)) < 0.00001, "Unmuted bass must rejoin at the current frame rather than restart");
                mixer.Seek(1); float[] seeked = Read(mixer, 4096);
                Check(Math.Abs(seeked[0] - (0.125f + 44100 * 0.000001f)) < 0.00001, "Seeking must reposition all stems to the same frame");
                mixer.OriginalMode = true; Read(mixer, 4096); float[] original = Read(mixer, 4096);
                Check(Math.Abs(original[500] - 0.2f) < 0.0001, "Original mode must play only the resampled original recording");
                mixer.OriginalMode = false; mixer.Volume = 0; Read(mixer, 4096);
                Check(Array.TrueForAll(Read(mixer, 4096), value => value == 0), "Master volume zero must silence output");
                mixer.Seek(mixer.Duration); float[] end = new float[4096]; Check(mixer.Read(end, 0, end.Length) == 0, "The stream must end once at the shared song duration");
            }
            using (StemMixer mixer = new StemMixer(audio, folder))
            {
                PlayerMix mix = new PlayerMix(); mixer.Volume = 1;
                mix.ApplyPreset("karaoke"); mixer.ApplyMix(mix); mixer.Seek(0);
                Check(Math.Abs(Read(mixer, 4096)[0] - .4375f) < .00001, "Karaoke must exclude vocals and retain all other parts");
                mix.ApplyPreset("acapella"); mixer.ApplyMix(mix); mixer.Seek(0);
                Check(Math.Abs(Read(mixer, 4096)[0] - .03125f) < .00001, "Acapella must isolate the HQ vocals");
                mix.ApplyPreset("drumnbass"); mix.Levels[1] = .5f; mixer.ApplyMix(mix); mixer.Seek(0);
                Check(Math.Abs(Read(mixer, 4096)[0] - .15625f) < .00001, "Drums + Bass must honor each fader independently");
                mix.ToggleMute(1); mixer.ApplyMix(mix); Read(mixer, 4096);
                Check(!mix.Solo[1] && !mix.Audible(1) && mix.Audible(2), "Muting a soloed stem must remove its solo and keep other solos");
                long frame = mixer.PositionFrames;
                Check(Math.Abs(Read(mixer, 4096)[0] - (.125f + frame * .000001f)) < .00001, "A soloed stem must keep advancing on the shared timeline");
                mix.ToggleSolo(1); mixer.ApplyMix(mix); Read(mixer, 4096);
                Check(!mix.Muted[1] && mix.Solo[1] && mix.Solo[2], "Solo must clear the same stem's mute, while allowing multiple solos");
                mix.ApplyPreset("all"); mixer.ApplyMix(mix); mixer.Seek(0);
                Check(Math.Abs(Read(mixer, 4096)[0] - .4375f) < .00001, "All must clear mute/solo without resetting fader levels");
                for (int i = 0; i < 4; i++) mix.Levels[i] = 0;
                mixer.ApplyMix(mix); Read(mixer, 4096);
                Check(Array.TrueForAll(Read(mixer, 4096), sample => sample == 0), "All zero faders must produce silence while the timeline advances");
            }
            float[] peaks = PlayerWaveforms.Read(Path.Combine(folder, "bass.wav"), CancellationToken.None);
            Check(peaks.Length == 1600 && peaks[0] < peaks[1599] && Math.Abs(peaks[1599] - 1) < .00001, "Waveform peaks must reflect the actual audio and normalize display only");
            string silent = Path.Combine(folder, "silent.wav"); Write(silent, 44100, 40, 0, 0);
            Check(Array.TrueForAll(PlayerWaveforms.Read(silent, CancellationToken.None), peak => peak == 0), "Short silent WAVs must have finite, empty peaks");
            string replacement = Path.Combine(folder, "replacement.wav"); Write(replacement, 44100, 88200, .35f, .000001f);
            using (StemMixer mixer = new StemMixer(audio, folder))
            {
                PlayerMix mix = new PlayerMix(); mix.ApplyPreset("acapella"); mixer.ApplyMix(mix); mixer.Volume = 1; mixer.Seek(.75);
                long before = mixer.PositionFrames; mixer.ReplaceStem(0, replacement);
                Check(mixer.PositionFrames == before, "Swapping a stem must preserve the shared playback position");
                float[] swapped = Read(mixer, 4096);
                Check(Math.Abs(swapped[0] - .03125f) < .00001 && Math.Abs(swapped[4000] - (.35f + (before + 2000) * .000001f)) < .00002, "Swaps must crossfade from the old stem to the donor at the current frame");
                mixer.Seek(.5); Check(Math.Abs(Read(mixer, 4096)[0] - (.35f + 22050 * .000001f)) < .00002, "Seek must reposition the replacement alongside the remaining stems");
                for (int i = 1; i < 4; i++) mix.Muted[i] = true;
                mix.ToggleMute(0); mixer.ApplyMix(mix); Read(mixer, 4096); Check(Array.TrueForAll(Read(mixer, 4096), sample => sample == 0), "Replacement stems must honor mute/solo and keep advancing");
                mixer.ReplaceStem(0, Path.Combine(folder, "vocals.wav")); mix.ApplyPreset("acapella"); mixer.ApplyMix(mix); mixer.Seek(.2);
                Check(Math.Abs(Read(mixer, 4096)[0] - .03125f) < .00001, "Restoring a stem must recover the original audio");
                bool invalidSwap = false; try { mixer.ReplaceStem(1, silent); } catch (InvalidDataException) { invalidSwap = true; }
                Check(invalidSwap, "A replacement shorter than the required timeline must not disturb the active mixer");
                Check(mixer.Read(new float[512], 0, 512) == 512, "A rejected replacement must leave playback usable");
            }
            string scanRoot = Path.Combine(folder, "scan-library"), scanAudio = Path.Combine(scanRoot, "mp3", "Artist", "Song.mp3"), scanFolder = Path.Combine(scanRoot, "stems", "Artist", "Song");
            Directory.CreateDirectory(Path.GetDirectoryName(scanAudio)); Directory.CreateDirectory(scanFolder); File.WriteAllText(scanAudio, "scan fixture");
            string metadataFile = Path.Combine(scanFolder, "metadata.json");
            string savedMetadata = "{\"id\":\"BaW_jenozKc\",\"title\":\"Clean song title\",\"artist\":\"Clean artist\",\"downloadedAt\":\"2026-10-05T12:00:00Z\",\"bpm\":120,\"key\":\"Eb minor\",\"analysis\":{\"version\":2,\"source\":\"mp3\",\"beatOffset\":0.13,\"beatConfidence\":0.8,\"keyConfidence\":0.7}}";
            File.WriteAllText(metadataFile, savedMetadata);
            var scanned = PlayerLibrary.Scan(scanRoot); Check(scanned.Count == 1 && scanned[0].Title == "Clean song title" && scanned[0].Artist == "Clean artist", "Player must read compact title/artist metadata");
            Check(scanned[0].AddedAt.HasValue && scanned[0].YouTubeId == "BaW_jenozKc", "Compact metadata must preserve added date and source identity");
            Check(scanned[0].Bpm == 120 && scanned[0].Key == "E♭ minor" && scanned[0].MusicDetails == "120 BPM · E♭ minor", "Library song cards must expose saved BPM and readable key names");
            MusicTraits savedTraits;
            Check(PlayerMatching.TrySavedAnalysis(metadataFile, out savedTraits) && savedTraits.Bpm == 120 && savedTraits.Tonic == 3 && savedTraits.Minor && savedTraits.BeatOffset == .13, "Persisted MP3 analysis must preserve BPM/key and beat timing");
            Check(PlayerMatching.Analyze(folder, scanned[0], CancellationToken.None).GetAwaiter().GetResult().Tonic == 3, "Saved analysis must be usable without invoking Python or requiring stems");
            File.WriteAllText(metadataFile, savedMetadata.Replace("\"bpm\":120", "\"bpm\":null").Replace("\"beatOffset\":0.13", "\"beatOffset\":0"));
            savedTraits = PlayerMatching.Analyze(folder, scanned[0], CancellationToken.None, true).GetAwaiter().GetResult();
            Check(savedTraits.Bpm == 0 && savedTraits.Tonic == 3, "Key editing must retain a known key without inventing an unknown BPM");
            bool unknownBpmRejected = false;
            try { PlayerMatching.Analyze(folder, scanned[0], CancellationToken.None).GetAwaiter().GetResult(); } catch (InvalidOperationException) { unknownBpmRejected = true; }
            Check(unknownBpmRejected, "Automatic replacement must still require a known BPM");
            File.WriteAllText(metadataFile, savedMetadata.Replace("Eb minor", "F# major"));
            Check(PlayerMatching.TrySavedAnalysis(metadataFile, out savedTraits) && savedTraits.Tonic == 6 && !savedTraits.Minor, "ASCII sharp keys in JSON must map to player key controls");
            File.WriteAllText(metadataFile, savedMetadata.Replace("\"bpm\":120", "\"bpm\":999"));
            Check(!PlayerMatching.TrySavedAnalysis(metadataFile, out savedTraits), "Invalid saved BPM must not be trusted");
            Check(!PlayerLibrary.Scan(scanRoot)[0].Bpm.HasValue, "Invalid BPM must appear as unknown in library cards");
            File.WriteAllText(metadataFile, savedMetadata.Replace("\"bpm\":120", "\"bpm\":null").Replace("\"key\":\"Eb minor\"", "\"key\":null"));
            Check(PlayerLibrary.Scan(scanRoot)[0].MusicDetails == "BPM unknown · Key unknown", "Missing music estimates must not produce invented library values");
            File.WriteAllText(metadataFile, savedMetadata.Replace("\"beatOffset\":0.13", "\"beatOffset\":9"));
            Check(!PlayerMatching.TrySavedAnalysis(metadataFile, out savedTraits), "Invalid saved beat phase must not be trusted");
            File.WriteAllText(metadataFile, savedMetadata.Replace("\"source\":\"mp3\"", "\"source\":\"old\""));
            Check(!PlayerMatching.TrySavedAnalysis(metadataFile, out savedTraits), "Player must reject metadata from a different analysis source");

            MusicTraits reference = new MusicTraits { Bpm = 120, Tonic = 0, Minor = true, BeatOffset = .12 };
            MusicTraits donor = new MusicTraits { Bpm = 80, Tonic = 1, Minor = true, BeatOffset = .05 };
            StemMatchPlan matched = StemMatchPlan.Create(reference, donor);
            Check(matched.Tempo == .75 && matched.MatchedBpm == 60, "An 80 BPM source must match a 120 BPM reference at half time with the closest speed change");
            donor.Bpm = 220; Check(StemMatchPlan.Create(reference, donor).MatchedBpm == 240, "A source near double time must match twice the reference BPM");
            donor.Bpm = 120; Check(StemMatchPlan.Create(reference, donor).Tempo == 1, "Equal BPM must retain the original speed");
            donor.Bpm = 80;
            var sharedPlan = StemMatchPlan.AtBpm(reference, donor, 150);
            Check(sharedPlan.MatchedBpm == 150 && sharedPlan.Tempo == 1.875, "Manual shared BPM must match the donor to the exact chosen tempo without octave substitution");
            Check(Math.Abs((reference.BeatOffset / 1.25 - donor.BeatOffset / 1.875 - sharedPlan.Offset) / .4 - Math.Round((reference.BeatOffset / 1.25 - donor.BeatOffset / 1.875 - sharedPlan.Offset) / .4)) < .000001, "Shared tempo must align both beat phases on the transformed original timeline");
            Check(Camelot.Section(9, true) == 7 && Camelot.Code(7) == "8A" && Camelot.Key(7) == "A minor", "A minor must occupy Camelot 8A");
            Check(Camelot.Section(0, false) == 19 && Camelot.Code(19) == "8B" && Camelot.Key(19) == "C major", "C major must occupy Camelot 8B");
            Check(Camelot.Shift(11, 0) == 1 && Camelot.Shift(0, 11) == -1 && Camelot.Shift(3, 3) == 0, "Key shifts must choose the nearest transposition and restore zero shift for the original key");
            reference.Bpm = 300; donor.Bpm = 38; Check(StemMatchPlan.Create(reference, donor).MatchedBpm == 37.5, "Tempo selection must also support one eighth of the reference BPM");
            reference.Bpm = 40; donor.Bpm = 290; Check(StemMatchPlan.Create(reference, donor).MatchedBpm == 320, "Tempo selection must support eight times the reference BPM");
            reference.Bpm = 120; donor.Bpm = 80; donor.Tonic = -1;
            Check(StemMatchPlan.Create(reference, donor).Tempo == .75, "An unknown key must not prevent tempo-only matching");
            donor.Tonic = 11; donor.Minor = false;
            Check(StemMatchPlan.Create(reference, donor).Tempo == .75, "Changing the source key/mode must not alter the tempo plan");
            double offset = StemMatchPlan.Create(reference, donor).Offset;
            double difference = reference.BeatOffset - donor.BeatOffset / .75 - offset;
            Check(Math.Abs(difference / .5 - Math.Round(difference / .5)) < .000001, "Beat phase must align to the shared reference grid");
            bool badBpm = false; donor.Bpm = Double.NaN; try { StemMatchPlan.Create(reference, donor); } catch (ArgumentException) { badBpm = true; }
            Check(badBpm, "Invalid BPM estimates must not reach the audio renderer");
            var sources = new StemSourceState(folder);
            var bankA = new StemSourceBank { Song = new SongInfo { Title = "Source A" }, Plan = matched, Files = new[] { replacement, replacement, replacement, replacement }, Peaks = new[] { new float[] {.2f}, new float[] {.3f}, new float[] {.4f}, new float[] {.5f} } };
            sources.Stage(bankA, 0);
            Check(sources.Available == bankA && sources.Active[0] == bankA && sources.Active[1] == null && sources.Active[2] == null && sources.Active[3] == null, "A drop must stage four sources but activate only the chosen lane");
            Check(sources.ToggleFile(1) == replacement, "Undropped lanes must have a prepared source immediately available");
            sources.Toggle(1); Check(sources.File(1) == replacement && sources.Peaks(1) == bankA.Peaks[1], "Toggling an undropped lane must reuse prepared audio and waveform");
            sources.Toggle(1); Check(sources.File(1) == sources.Original(1) && sources.Available == bankA, "Returning to original must retain the replacement for instant reuse");
            var bankB = new StemSourceBank { Song = new SongInfo { Title = "Source B" }, Plan = matched, Files = (string[])bankA.Files.Clone(), Peaks = bankA.Peaks };
            sources.Stage(bankB, 2);
            Check(sources.Active[0] == bankA && sources.Active[2] == bankB && sources.Active[1] == null, "A later drop must not switch the other active lanes");
            sources.Toggle(0); sources.Toggle(0); Check(sources.Active[0] == bankB, "Toggling back to replacement must select the latest staged song");
            bool incompleteBank = false; try { sources.Stage(new StemSourceBank { Song = bankA.Song, Files = new string[4], Peaks = bankA.Peaks }, 1); } catch (ArgumentException) { incompleteBank = true; }
            Check(incompleteBank && sources.Available == bankB, "An incomplete preparation must leave the current source bank intact");
            sources.Reset(); Check(sources.Available == null && Array.TrueForAll(sources.Active, bank => bank == null), "Reset must clear active and staged replacements");
            if (args.Length > 1)
            {
                string externalCache = Path.Combine(folder, "external-matched-stems");
                File.WriteAllText(Path.Combine(folder, "settings.json"), new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new { matchedStemsRoot = externalCache }));
                string donorFolder = Path.Combine(folder, "tonal-donor"); Directory.CreateDirectory(donorFolder);
                WriteTone(Path.Combine(donorFolder, "vocals.wav"), 22050, 220500, 440);
                SongInfo source = new SongInfo { Folder = donorFolder, HasStems = true };
                StemMatchPlan plan = new StemMatchPlan { Tempo = 1.25, Offset = .125 };
                string transformed = PlayerMatching.Prepare(folder, source, 0, plan, 22050, 308700, CancellationToken.None, source).GetAwaiter().GetResult();
                Check(Path.GetDirectoryName(transformed) == externalCache && File.Exists(transformed), "Real matched audio must render into the configured cache folder");
                Check(PlayerMatchedCache.Protected().Contains(transformed), "A rendered file must be protected before the preparation caller receives it");
                DateTime renderedAt = File.GetLastWriteTimeUtc(transformed);
                string reused = PlayerMatching.Prepare(folder, source, 0, plan, 22050, 308700, CancellationToken.None, source).GetAwaiter().GetResult();
                Check(reused == transformed && File.GetLastWriteTimeUtc(reused) == renderedAt, "Reusing cached audio must preserve the rendered file and its identity");
                Check(Math.Abs(ToneFrequency(transformed, 2, 2) - 440) < 3, "Speeding up vocals must retain the exact source pitch");
                for (int stem = 1; stem < 4; stem++)
                {
                    File.Copy(Path.Combine(donorFolder, "vocals.wav"), Path.Combine(donorFolder, StemMixer.StemNames[stem] + ".wav"));
                    string preserved = PlayerMatching.Prepare(folder, source, stem, new StemMatchPlan { Tempo = .75 }, 22050, 308700, CancellationToken.None).GetAwaiter().GetResult();
                    double frequency = ToneFrequency(preserved, 2, 2);
                    Check(Math.Abs(frequency - 440) < 3, "Slowing any replacement stem must preserve its pitch/key (stem " + stem + ", " + frequency + " Hz)");
                }
                using (WaveFileReader reader = new WaveFileReader(transformed))
                {
                    Check(reader.Length / reader.WaveFormat.BlockAlign == 308700, "Matched render must have the reference's exact stereo frame count");
                    reader.CurrentTime = TimeSpan.FromSeconds(10); float[] tail = new float[8192]; StemMixer.Samples(reader).Read(tail, 0, tail.Length);
                    Check(Array.TrueForAll(tail, sample => sample == 0), "Short donor stems must end with silence on the reference timeline");
                    reader.CurrentTime = TimeSpan.FromSeconds(.03); float[] intro = new float[512]; StemMixer.Samples(reader).Read(intro, 0, intro.Length);
                    Check(Array.TrueForAll(intro, sample => sample == 0), "Positive beat alignment must insert the exact leading delay");
                }
                Check(PlayerMatching.Prepare(folder, source, 0, plan, 22050, 308700, CancellationToken.None).GetAwaiter().GetResult() == transformed, "An unchanged source and match plan must reuse its render cache");
                StemMatchPlan pitchedPlan = new StemMatchPlan { Tempo = 1.5, Pitch = Camelot.Pitch(2) };
                string pitched = PlayerMatching.Prepare(folder, source, 0, pitchedPlan, 22050, 308700, CancellationToken.None).GetAwaiter().GetResult();
                Check(Math.Abs(ToneFrequency(pitched, .5, 1) - 440 * Math.Pow(2, 2.0 / 12)) < 3, "Selecting a key two semitones higher must change the audible frequency while using an independent tempo ratio");
                Check(pitched != transformed, "Different pitch settings must have different render cache files");
                string tunedOriginal = PlayerMatching.PrepareFile(folder, audio, new StemMatchPlan { Tempo = 1.5, Pitch = Camelot.Pitch(-2) }, 44100, 58800, CancellationToken.None).GetAwaiter().GetResult();
                using (WaveFileReader reader = new WaveFileReader(tunedOriginal)) Check(reader.WaveFormat.SampleRate == 44100 && reader.Length / reader.WaveFormat.BlockAlign == 58800, "Original audio playback must render to the changed shared timeline");
                WritePulses(Path.Combine(donorFolder, "drums.wav"));
                StemMatchPlan rhythmPlan = new StemMatchPlan { Tempo = 1.25, Offset = -.1 };
                string rhythmFile = PlayerMatching.Prepare(folder, source, 1, rhythmPlan, 22050, 308700, CancellationToken.None).GetAwaiter().GetResult();
                var onsets = PulseStarts(rhythmFile);
                Check(onsets.Count == 10, "Tempo rendering must preserve all ten donor drum pulses before silence padding");
                for (int i = 1; i < onsets.Count; i++) Check(Math.Abs(onsets[i] - onsets[i - 1] - .8) < .025, "A 1.25 tempo ratio must place drum pulses 0.8 seconds apart");
                Check(Math.Abs(onsets[0] - .06) < .03, "Negative beat alignment must trim the leading offset after stretching");
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    cancel.Cancel(); bool canceled = false;
                    try { PlayerMatching.Prepare(folder, source, 0, new StemMatchPlan { Tempo = .9 }, 22050, 308700, cancel.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { canceled = true; }
                    Check(canceled, "Canceled render jobs must not publish a partial matched stem");
                }
            }
            string exportFolder = Path.Combine(folder, "exports"); Directory.CreateDirectory(exportFolder);
            string export = PlayerExport.CopyAll(folder, exportFolder, "Artist - Song");
            Check(Directory.GetFiles(export).Length == 4, "Export all must contain exactly the four original WAVs");
            foreach (string stem in StemMixer.StemNames)
                Check(Convert.ToBase64String(File.ReadAllBytes(Path.Combine(folder, stem + ".wav"))) == Convert.ToBase64String(File.ReadAllBytes(Path.Combine(export, stem + ".wav"))), "Export must preserve every WAV byte");
            string second = PlayerExport.CopyAll(folder, exportFolder, "Artist - Song");
            Check(second != export && Directory.Exists(export), "Repeat exports must preserve previous exports");
            string single = Path.Combine(exportFolder, "vocals.wav"); PlayerExport.CopyStem(Path.Combine(folder, "vocals.wav"), single);
            Check(new FileInfo(single).Length == new FileInfo(Path.Combine(folder, "vocals.wav")).Length, "Single-stem export must copy the complete original WAV");
            bool selfCopy = false; try { PlayerExport.CopyStem(single, single); } catch (IOException) { selfCopy = true; }
            Check(selfCopy && File.Exists(single), "Export must never replace its own source");
            PlayerExport.CopyStem(audio, single);
            Check(Convert.ToBase64String(File.ReadAllBytes(audio)) == Convert.ToBase64String(File.ReadAllBytes(single)), "Replacing a confirmed export must preserve the complete new WAV");
            string incomplete = Path.Combine(folder, "incomplete"); Directory.CreateDirectory(incomplete);
            File.Copy(Path.Combine(folder, "vocals.wav"), Path.Combine(incomplete, "vocals.wav"));
            int beforeExports = Directory.GetDirectories(exportFolder).Length;
            bool failedExport = false; try { PlayerExport.CopyAll(incomplete, exportFolder, "Incomplete"); } catch (FileNotFoundException) { failedExport = true; }
            Check(failedExport && Directory.GetDirectories(exportFolder).Length == beforeExports, "A missing stem must leave no partial song export or staging directory");
            Write(Path.Combine(folder, "other.wav"), 44100, 44000, 0.1f, 0);
            bool rejected = false; try { using (StemMixer invalid = new StemMixer(audio, folder)) { } } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Mismatched stem lengths must be rejected");
            if (args.Length > 0 && !String.IsNullOrEmpty(args[0]))
            {
                var songs = PlayerLibrary.Scan(args[0]);
                Check(songs.Count > 0, "The downloaded library must be discoverable");
                foreach (SongInfo song in songs)
                using (StemMixer real = new StemMixer(song.Audio, song.HasStems ? song.Folder : ""))
                {
                    real.Seek(Math.Min(30, real.Duration / 2));
                    float[] frames = Read(real, 8192); Check(Array.Exists(frames, value => Math.Abs(value) > 0.0001), "Real song stems must contain audio");
                    real.OriginalMode = true; Read(real, 8192);
                    Check(Array.Exists(Read(real, 8192), value => Math.Abs(value) > 0.0001), "The downloaded MP3 must decode and resample successfully");
                    Console.WriteLine("Verified downloaded song: " + song.Title);
                }
            }
            Console.WriteLine("Player audio and colors: " + checks + " checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            string full = Path.GetFullPath(folder), temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("stemkit-audio-tests-")) Directory.Delete(full, true);
        }
    }
}
