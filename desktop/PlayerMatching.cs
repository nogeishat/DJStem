using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using NAudio.Wave;

internal sealed class MusicTraits
{
    internal double? TuningHz;
    internal double Bpm, BeatOffset, BeatConfidence, KeyConfidence;
    internal int Tonic;
    internal bool Minor;
    internal MusicTraits Copy() { return (MusicTraits)MemberwiseClone(); }
    internal static readonly string[] Notes = { "C", "C♯", "D", "E♭", "E", "F", "F♯", "G", "A♭", "A", "B♭", "B" };
    internal string Key { get { return Tonic < 0 ? "Unknown" : Notes[Tonic] + (Minor ? " minor" : " major"); } }
}

internal static class TuningReference
{
    internal static bool ValidTarget(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= 300 && value <= 600; }
    internal static double Pitch(double? original, double? target, int semitones = 0)
    {
        if (target.HasValue && !ValidTarget(target.Value)) throw new ArgumentException("Choose an A4 reference between 300 and 600 Hz.");
        double correction = original.HasValue && original.Value >= 427.47 && original.Value <= 452.9 && target.HasValue ? target.Value / original.Value : 1;
        // Rubber Band preserves pitch during tempo changes: do not divide this by tempo.
        return Math.Pow(2, semitones / 12.0) * correction;
    }
    internal static double? Read(Dictionary<string, object> data)
    {
        object value;
        if (!data.TryGetValue("tuningHz", out value) || value == null) return null;
        double hz; return Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out hz) && hz >= 427.47 && hz <= 452.9 ? (double?)hz : null;
    }
}

internal sealed class StemMatchPlan
{
    internal double Tempo, Offset, Pitch = 1;
    internal double MatchedBpm;
    internal static StemMatchPlan Create(MusicTraits reference, MusicTraits donor)
    {
        if (!ValidBpm(reference.Bpm) || !ValidBpm(donor.Bpm)) throw new ArgumentException("Both songs need a saved BPM estimate between 30 and 300 before replacing stems.");
        // Choose original BPM × 2^n, keeping the proportional speed change minimal.
        double octaves = Math.Round(Math.Log(donor.Bpm / reference.Bpm, 2));
        double matched = reference.Bpm * Math.Pow(2, octaves);
        double tempo = matched / donor.Bpm, period = 60 / reference.Bpm;
        double offset = reference.BeatOffset - donor.BeatOffset / tempo;
        offset -= Math.Floor(offset / period + .5) * period;
        return new StemMatchPlan { Tempo = tempo, MatchedBpm = matched, Offset = offset };
    }
    private static bool ValidBpm(double bpm) { return !Double.IsNaN(bpm) && !Double.IsInfinity(bpm) && bpm >= 30 && bpm <= 300; }
    internal static StemMatchPlan AtBpm(MusicTraits reference, MusicTraits donor, double target)
    {
        if (!ValidBpm(reference.Bpm) || !ValidBpm(donor.Bpm) || !ValidBpm(target)) throw new ArgumentException("A shared tempo needs BPM values between 30 and 300.");
        double tempo = target / donor.Bpm, referenceTempo = target / reference.Bpm, period = 60 / target;
        double offset = reference.BeatOffset / referenceTempo - donor.BeatOffset / tempo;
        offset -= Math.Floor(offset / period + .5) * period;
        return new StemMatchPlan { Tempo = tempo, MatchedBpm = target, Offset = offset };
    }
    internal string Summary { get { return MatchedBpm.ToString("0.00", CultureInfo.InvariantCulture) + " BPM · " + (Math.Abs(Pitch - 1) < .000001 ? "key unchanged" : Math.Round(12 * Math.Log(Pitch, 2)).ToString("+0;-0;0", CultureInfo.InvariantCulture) + " semitones"); } }
}

internal static class PlayerMatching
{
    private static readonly SemaphoreSlim analysisGate = new SemaphoreSlim(1);
    private static string Number(double value) { return value.ToString("0.########", CultureInfo.InvariantCulture); }
    private static string Hash(string value)
    {
        using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
    }
    private static string Identity(string file)
    {
        FileInfo info = new FileInfo(file); return info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
    }
    internal static async Task<MusicTraits> Analyze(string project, SongInfo song, CancellationToken cancellation, bool allowUnknownBpm = false)
    {
        cancellation.ThrowIfCancellationRequested();
        MusicTraits saved;
        if (TrySavedAnalysis(Path.Combine(song.Folder, "metadata.json"), out saved))
        {
            if (saved.Bpm == 0 && !allowUnknownBpm) throw new InvalidOperationException("No reliable BPM was detected in this MP3. BPM is unknown in metadata.");
            return saved;
        }
        if (!song.HasStems) throw new InvalidOperationException("Wait for stem separation to finish first.");
        string fingerprint = "analysis-v1|" + Identity(Path.Combine(project, "scripts", "analyze-music.py"));
        foreach (string stem in new string[] { "drums", "bass", "other" }) fingerprint += "|" + Identity(Path.Combine(song.Folder, stem + ".wav"));
        string file = PlayerData.Data(project, "music-analysis", Hash(fingerprint) + ".json");
        await analysisGate.WaitAsync(cancellation);
        try
        {
            if (!File.Exists(file))
            {
                string python = Path.Combine(project, "tools", "stem-runtime", "Scripts", "python.exe");
                if (!File.Exists(python)) throw new FileNotFoundException("Music matching needs the stem runtime. Run npm run setup:stems.");
                await Run(python, new string[] { Path.Combine(project, "scripts", "analyze-music.py"), "--folder", song.Folder, "--output", file }, project, cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            var data = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(file));
            return new MusicTraits { Bpm = Convert.ToDouble(data["bpm"]), BeatOffset = Convert.ToDouble(data["beatOffset"]), BeatConfidence = Convert.ToDouble(data["beatConfidence"]), Tonic = Convert.ToInt32(data["tonic"]), Minor = Convert.ToBoolean(data["minor"]), KeyConfidence = Convert.ToDouble(data["keyConfidence"]) };
        }
        finally { analysisGate.Release(); }
    }
    // Persisted MP3 analysis is the matching reference; the older stem cache is
    // only a fallback for libraries that have not yet been updated by the downloader.
    internal static bool TrySavedAnalysis(string file, out MusicTraits traits)
    {
        traits = null;
        try
        {
            var data = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(file));
            var analysis = data["analysis"] as Dictionary<string, object>;
            if (analysis == null || Convert.ToInt32(analysis["version"]) != 2 || Convert.ToString(analysis["source"]) != "mp3") return false;
            double bpm = data["bpm"] == null ? 0 : Convert.ToDouble(data["bpm"], CultureInfo.InvariantCulture);
            double offset = Convert.ToDouble(analysis["beatOffset"], CultureInfo.InvariantCulture), beat = Convert.ToDouble(analysis["beatConfidence"], CultureInfo.InvariantCulture), keyConfidence = Convert.ToDouble(analysis["keyConfidence"], CultureInfo.InvariantCulture);
            if (Double.IsNaN(bpm) || Double.IsInfinity(bpm) || (bpm != 0 && (bpm < 30 || bpm > 300)) || Double.IsNaN(offset) || Double.IsInfinity(offset) || offset < 0 || (bpm == 0 ? offset != 0 : offset >= 60 / bpm) || Double.IsNaN(beat) || Double.IsNaN(keyConfidence) || beat < 0 || beat > 1 || keyConfidence < 0 || keyConfidence > 1) return false;
            string key = Convert.ToString(data["key"]).Replace("#", "♯").Replace("b", "♭");
            int tonic = -1; bool minor = key.EndsWith(" minor", StringComparison.Ordinal);
            if (!String.IsNullOrEmpty(key))
            {
                for (int i = 0; i < MusicTraits.Notes.Length; i++) if (key == MusicTraits.Notes[i] + (minor ? " minor" : " major")) { tonic = i; break; }
                if (tonic < 0) return false;
            }
            traits = new MusicTraits { Bpm = bpm, Tonic = tonic, Minor = minor, BeatOffset = offset, BeatConfidence = beat, KeyConfidence = keyConfidence, TuningHz = TuningReference.Read(data) }; return true;
        }
        catch { return false; }
    }
    internal static string Filter(StemMatchPlan plan, int rate, long frames, int stem)
    {
        if (plan.Tempo <= 0 || Double.IsNaN(plan.Tempo) || Double.IsInfinity(plan.Tempo) || plan.Pitch <= 0 || Double.IsNaN(plan.Pitch) || Double.IsInfinity(plan.Pitch)) throw new ArgumentException("Tempo and pitch must be positive finite ratios.");
        // Crisp phase resets can detune sustained components when stretching
        // drums longer (verified with a 440 Hz source). Mixed mode preserves
        // those tones while still retaining high-frequency transients.
        string filter = "aresample=" + rate + ",rubberband=tempo=" + Number(plan.Tempo) + ":pitch=" + Number(plan.Pitch) + ":transients=" + (stem == 1 && plan.Tempo >= 1 ? "crisp" : "mixed") + ":formant=preserved:pitchq=quality:channels=together";
        long offset = (long)Math.Round(plan.Offset * rate);
        if (offset > 0) filter += ",adelay=" + offset + "S:all=1";
        else if (offset < 0) filter += ",atrim=start_sample=" + (-offset) + ",asetpts=PTS-STARTPTS";
        return filter + ",apad,atrim=end_sample=" + frames + ",asetpts=PTS-STARTPTS";
    }
    internal static async Task<string> Prepare(string project, SongInfo donor, int stem, StemMatchPlan plan, int rate, long frames, CancellationToken cancellation, object cacheOwner = null, Action<double> progress = null)
    {
        cancellation.ThrowIfCancellationRequested();
        Filter(plan, rate, frames, stem);
        string source = Path.Combine(donor.Folder, StemMixer.StemNames[stem] + ".wav");
        // The shared transport determines the minimum length, not the donor's
        // EOF. Preserve the matched tail so advancing a stem can reveal it.
        using (WaveFileReader reader = new WaveFileReader(source))
        {
            long matchedFrames = checked((long)Math.Ceiling(reader.Length / (double)reader.WaveFormat.BlockAlign / reader.WaveFormat.SampleRate / plan.Tempo * rate) + (long)Math.Round(plan.Offset * rate));
            frames = Math.Max(frames, matchedFrames);
        }
        return await PrepareFile(project, source, plan, rate, frames, cancellation, stem, cacheOwner, progress);
    }
    internal static async Task<string> PrepareFile(string project, string source, StemMatchPlan plan, int rate, long frames, CancellationToken cancellation, int stem = 0, object cacheOwner = null, Action<double> progress = null)
    {
        string filter = Filter(plan, rate, frames, stem);
        return await PrepareFiltered(project, source, filter, rate, frames, cancellation, cacheOwner, progress);
    }
    internal static PlaybackTransform LiveTransform(string source, StemMatchPlan plan, int rate, long frames, int stem)
    {
        using (var reader = new WaveFileReader(source))
        {
            long tail = (long)Math.Ceiling(reader.TotalTime.TotalSeconds / plan.Tempo * rate) + (long)Math.Round(plan.Offset * rate);
            return new PlaybackTransform(plan, rate, Math.Max(frames, tail), stem);
        }
    }
    internal static async Task<string> PrepareFiltered(string project, string source, string filter, int rate, long frames, CancellationToken cancellation, object cacheOwner = null, Action<double> progress = null)
    {
        cancellation.ThrowIfCancellationRequested();
        string folder = PlayerMatchedCache.Folder(project); Directory.CreateDirectory(folder);
        await PlayerMatchedCache.RenderGate.WaitAsync(cancellation);
        string temporary = null;
        try
        {
            string file = Path.Combine(folder, Hash("render-v1|" + Identity(source) + "|" + filter) + ".wav");
            if (Valid(file, rate, frames))
            {
                PlayerMatchedCache.Keep(cacheOwner, file); PlayerMatchedCache.Touch(file);
                PlayerMatchedCache.EnsureRoom(project, 0, source, file); if (progress != null) progress(1); return file;
            }
            // Stereo 24-bit PCM plus ample space for FFmpeg's WAV header.
            long reserve = checked(frames * 6 + 4096);
            PlayerMatchedCache.EnsureRoom(project, reserve, source);
            temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp.wav";
            string ffmpeg = Environment.GetEnvironmentVariable("STEMKIT_FFMPEG");
            if (String.IsNullOrWhiteSpace(ffmpeg)) ffmpeg = File.Exists(@"C:\ffmpeg\bin\ffmpeg.exe") ? @"C:\ffmpeg\bin\ffmpeg.exe" : "ffmpeg.exe";
            await Run(ffmpeg, new string[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-progress", "pipe:1", "-y", "-i", source, "-vn", "-af", filter, "-ar", rate.ToString(), "-ac", "2", "-c:a", "pcm_s24le", temporary }, project, cancellation, progress, frames / (double)rate);
            cancellation.ThrowIfCancellationRequested();
            if (!Valid(temporary, rate, frames)) throw new InvalidDataException("The prepared audio did not render to its expected frame count.");
            if (File.Exists(file)) File.Delete(file);
            File.Move(temporary, file);
            PlayerMatchedCache.Keep(cacheOwner, file); PlayerMatchedCache.Touch(file); if (progress != null) progress(1); return file;
        }
        finally
        {
            try { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
            finally { PlayerMatchedCache.RenderGate.Release(); GC.KeepAlive(cacheOwner); }
        }
    }
    private static bool Valid(string file, int rate, long frames)
    {
        try { using (WaveFileReader reader = new WaveFileReader(file)) return reader.WaveFormat.SampleRate == rate && reader.WaveFormat.Channels == 2 && reader.Length / reader.WaveFormat.BlockAlign == frames; } catch { return false; }
    }
    // Quote for Windows CreateProcess, never through a shell.
    private static string Quote(string value)
    {
        StringBuilder result = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { result.Append('\\', slashes * 2 + 1); result.Append(c); }
            else { result.Append('\\', slashes); result.Append(c); } slashes = 0;
        }
        result.Append('\\', slashes * 2); result.Append('"'); return result.ToString();
    }
    internal static Task<string> MeasureLoudness(string project, string audio, CancellationToken cancellation)
    {
        string ffmpeg = Environment.GetEnvironmentVariable("STEMKIT_FFMPEG");
        if (String.IsNullOrWhiteSpace(ffmpeg)) ffmpeg = File.Exists(@"C:\ffmpeg\bin\ffmpeg.exe") ? @"C:\ffmpeg\bin\ffmpeg.exe" : "ffmpeg.exe";
        return Run(ffmpeg, new[] { "-hide_banner", "-nostdin", "-i", audio, "-map", "0:a:0", "-vn", "-sn", "-dn", "-af", "loudnorm=I=-16:TP=-1.5:LRA=50:dual_mono=true:print_format=json", "-f", "null", "-" }, project, cancellation);
    }
    private static async Task<string> Run(string executable, string[] arguments, string project, CancellationToken cancellation, Action<double> progress = null, double seconds = 0)
    {
        cancellation.ThrowIfCancellationRequested();
        StringBuilder args = new StringBuilder(); foreach (string argument in arguments) { if (args.Length > 0) args.Append(' '); args.Append(Quote(argument)); }
        ProcessStartInfo start = new ProcessStartInfo(executable, args.ToString()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = PlayerData.Root(project) };
        start.EnvironmentVariables["NUMBA_CACHE_DIR"] = PlayerData.Data(project, "numba-cache"); start.EnvironmentVariables["PYTHONUTF8"] = "1";
        using (Process process = new Process { StartInfo = start })
        {
            process.Start(); Task<string> errors = process.StandardError.ReadToEndAsync(); Task output = ReadRenderProgress(process, progress, seconds);
            using (cancellation.Register(delegate { try { if (!process.HasExited) process.Kill(); } catch { } }))
            {
                await Task.Run(() => process.WaitForExit()); string error = await errors; await output; cancellation.ThrowIfCancellationRequested();
                if (process.ExitCode != 0) throw new InvalidOperationException("Music matching failed: " + (error.Length > 1600 ? error.Substring(error.Length - 1600) : error));
                return error;
            }
        }
    }
    private static async Task ReadRenderProgress(Process process, Action<double> progress, double seconds)
    {
        string line;
        while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            long microseconds;
            if (progress != null && seconds > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal) && Int64.TryParse(line.Substring(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out microseconds))
                progress(Math.Max(0, Math.Min(.99, microseconds / 1000000.0 / seconds)));
        }
    }
}
