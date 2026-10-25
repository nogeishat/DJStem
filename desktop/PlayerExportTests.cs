using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using NAudio.Wave;

internal static class PlayerExportTests
{
    private const int Rate = 16000, Seconds = 6;
    private static int checks;
    private static readonly string Ffmpeg = FindTool("ffmpeg"), Ffprobe = FindTool("ffprobe");
    private static string FindTool(string name)
    {
        string configured = Environment.GetEnvironmentVariable("STEMKIT_" + name.ToUpperInvariant());
        if (!String.IsNullOrWhiteSpace(configured)) return configured;
        string installed = Path.Combine(@"C:\ffmpeg\bin", name + ".exe");
        if (File.Exists(installed)) return installed;
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Select(dir => Path.Combine(dir, name + ".exe")).FirstOrDefault(File.Exists) ?? name + ".exe";
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static void Tone(string file, double frequency, int seconds = Seconds)
    {
        using (var writer = new WaveFileWriter(file, new WaveFormat(Rate, 24, 2)))
            for (int frame = 0; frame < Rate * seconds; frame++)
            {
                double time = (double)frame / Rate; float value = (float)((.04 + .015 * time) * Math.Sin(2 * Math.PI * frequency * time));
                writer.WriteSample(value); writer.WriteSample(value * .7f);
            }
    }
    private static float[] Render(StemMixer mixer)
    {
        mixer.SeekFrame(0); var samples = new float[mixer.TotalFrames * 2]; int offset = 0;
        while (offset < samples.Length) { int read = mixer.Read(samples, offset, Math.Min(8192, samples.Length - offset)); if (read == 0) break; offset += read; }
        Check(offset == samples.Length && mixer.Read(new float[2], 0, 2) == 0, "Export must cover the entire original transport exactly once"); return samples;
    }
    private static void Equal(float[] expected, float[] actual, string message)
    {
        Check(expected.Length == actual.Length && expected.Zip(actual, (a, b) => Math.Abs(a - b)).Max() < .000001, message);
    }
    private static bool Near(Color actual, Color expected) { return Math.Abs(actual.R - expected.R) < 12 && Math.Abs(actual.G - expected.G) < 12 && Math.Abs(actual.B - expected.B) < 12; }
    private static byte[] Artwork()
    {
        using (var original = new Bitmap(128, 64))
        using (var added = new Bitmap(64, 128))
        {
            using (var graphics = Graphics.FromImage(original))
            {
                graphics.Clear(Color.Purple); graphics.FillRectangle(Brushes.Red, 32, 0, 32, 64); graphics.FillRectangle(Brushes.Lime, 64, 0, 32, 64);
            }
            using (var graphics = Graphics.FromImage(added))
            {
                graphics.Clear(Color.Purple); graphics.FillRectangle(Brushes.Blue, 0, 32, 32, 64); graphics.FillRectangle(Brushes.Yellow, 32, 32, 32, 64);
            }
            byte[] cover = PlayerExport.Cover(original, added);
            using (var stream = new MemoryStream(cover))
            using (var image = new Bitmap(stream))
            {
                Check(image.Width == 512 && image.Height == 512, "Export artwork must be a square 512-pixel image");
                Check(new[] { 20, 128, 240 }.All(x => new[] { 20, 256, 490 }.All(y => Near(image.GetPixel(x, y), Color.Red))), "The left half must use only the original cover's left half after square cropping");
                Check(new[] { 272, 384, 490 }.All(x => new[] { 20, 256, 490 }.All(y => Near(image.GetPixel(x, y), Color.Yellow))), "The right half must use only the added cover's right half after square cropping");
            }
            using (var stream = new MemoryStream(PlayerExport.Cover(original, null)))
            using (var image = new Bitmap(stream)) Check(Near(image.GetPixel(128, 256), Color.Red) && Near(image.GetPixel(384, 256), Color.Lime), "With one song, export must keep its complete square cover");
            using (var stream = new MemoryStream(PlayerExport.Cover(null, added)))
            using (var image = new Bitmap(stream)) Check(Near(image.GetPixel(128, 256), Color.Blue) && Near(image.GetPixel(384, 256), Color.Yellow), "If one cover is missing, export must use the available full cover");
            Check(PlayerExport.Cover(null, null) == null, "Missing artwork must still allow audio export");
            return cover;
        }
    }
    private static string Run(string executable, string arguments)
    {
        using (var process = Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
        {
            string output = process.StandardOutput.ReadToEnd(), errors = process.StandardError.ReadToEnd(); process.WaitForExit(); Check(process.ExitCode == 0, "The exported media must be readable: " + errors); return output;
        }
    }
    private static void EmbeddedArtwork(string root, string mp3, byte[] cover)
    {
        string report = Run(Ffprobe, "-v error -show_entries stream=codec_type,codec_name,width,height:stream_disposition=attached_pic:stream_tags=comment -of json \"" + mp3 + "\"");
        var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(report);
        var streams = ((System.Collections.IEnumerable)data["streams"]).Cast<Dictionary<string, object>>().ToArray();
        Check(streams.Count(stream => (string)stream["codec_type"] == "audio") == 1 && streams.Count(stream => (string)stream["codec_type"] == "video") == 1, "The MP3 must contain one audio stream and one embedded cover");
        var picture = streams.Single(stream => (string)stream["codec_type"] == "video");
        Check((string)picture["codec_name"] == "mjpeg" && Convert.ToInt32(picture["width"]) == 512 && Convert.ToInt32(picture["height"]) == 512 && Convert.ToInt32(((Dictionary<string, object>)picture["disposition"])["attached_pic"]) == 1, "The JPEG must be tagged as attached album artwork");
        Check((string)((Dictionary<string, object>)picture["tags"])["comment"] == "Cover (front)", "The embedded picture must be tagged as the front cover");
        string extracted = Path.Combine(root, "extracted-cover.jpg");
        Run(Ffmpeg, "-hide_banner -loglevel error -nostdin -y -i \"" + mp3 + "\" -map 0:v:0 -c:v copy \"" + extracted + "\"");
        Check(File.ReadAllBytes(extracted).SequenceEqual(cover), "Extracted MP3 artwork must exactly match the original-left/added-right composite");
    }
    private sealed class Progress : IProgress<int>
    {
        internal int Last = -1;
        internal Action<int> Changed;
        public void Report(int value) { Check(value >= Last && value <= 100, "Export progress must increase from zero to 100"); Last = value; if (Changed != null) Changed(value); }
    }
    private static void Encode(string root, StemMixer snapshot, float[] expected)
    {
        string mp3 = Path.Combine(root, "Mixed song ü.mp3"); var progress = new Progress(); byte[] cover = Artwork();
        PlayerExport.Mp3(snapshot, mp3, "Song \"mix\" ü", "Artist", CancellationToken.None, progress, cover);
        Check(progress.Last == 100 && File.Exists(mp3), "A successful export must publish its MP3 and finish at 100 percent");
        EmbeddedArtwork(root, mp3, cover);
        string decoded = Path.Combine(root, "decoded.wav");
        using (var process = Process.Start(new ProcessStartInfo(Ffmpeg, "-hide_banner -loglevel error -nostdin -y -i \"" + mp3 + "\" -ar " + Rate + " -ac 2 -c:a pcm_f32le \"" + decoded + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true }))
        {
            string errors = process.StandardError.ReadToEnd(); process.WaitForExit(); Check(process.ExitCode == 0, "The exported MP3 must decode: " + errors);
        }
        using (var reader = new WaveFileReader(decoded))
        {
            var source = StemMixer.Samples(reader); var actual = new float[reader.Length / sizeof(float)]; int offset = 0, read;
            while (offset < actual.Length && (read = source.Read(actual, offset, actual.Length - offset)) > 0) offset += read;
            Check(Math.Abs(offset - expected.Length) <= Rate * .04 * 2, "MP3 duration must match the full song within encoder padding");
            int start = Rate / 5 * 2, end = Math.Min(expected.Length, offset) - start;
            double error = 0, energy = 0;
            for (int sample = start; sample < end; sample++) { double delta = expected[sample] - actual[sample]; error += delta * delta; energy += expected[sample] * expected[sample]; }
            Check(Math.Sqrt(error / Math.Max(.000001, energy)) < .04, "Decoded MP3 must audibly match the exact mixer output, including offsets, loops and donor gains");
        }
        Check(!Directory.GetFiles(root, ".stemkit-export-*").Any(), "Successful exports must remove all staging files");

        byte[] before = File.ReadAllBytes(mp3);
        using (var canceled = new CancellationTokenSource())
        {
            var cancelProgress = new Progress { Changed = value => canceled.Cancel() }; bool stopped = false;
            try { PlayerExport.Mp3(snapshot, mp3, "Canceled", "Artist", canceled.Token, cancelProgress, cover); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped && File.ReadAllBytes(mp3).SequenceEqual(before) && !Directory.GetFiles(root, ".stemkit-export-*").Any(), "Canceling during encoding must leave an existing destination intact and remove partial audio");
        }
        string configured = Environment.GetEnvironmentVariable("STEMKIT_FFMPEG");
        try
        {
            Environment.SetEnvironmentVariable("STEMKIT_FFMPEG", Path.Combine(root, "missing-ffmpeg.exe")); bool failed = false;
            try { PlayerExport.Mp3(snapshot, mp3, "Failure", "Artist", CancellationToken.None, coverJpeg: cover); } catch (System.ComponentModel.Win32Exception) { failed = true; }
            Check(failed && File.ReadAllBytes(mp3).SequenceEqual(before) && !Directory.GetFiles(root, ".stemkit-export-*").Any(), "Encoder startup failure must preserve the destination and clean up");
            Environment.SetEnvironmentVariable("STEMKIT_FFMPEG", Ffmpeg);
            string invalid = Path.Combine(root, "invalid-encoder.exe"); File.Copy(Ffprobe, invalid);
            Environment.SetEnvironmentVariable("STEMKIT_FFMPEG", invalid); failed = false;
            try { PlayerExport.Mp3(snapshot, mp3, "Failure", "Artist", CancellationToken.None, coverJpeg: cover); } catch (IOException) { failed = true; }
            Check(failed && File.ReadAllBytes(mp3).SequenceEqual(before) && !Directory.GetFiles(root, ".stemkit-export-*").Any(), "An encoder that exits early must not deadlock or publish a partial export");
        }
        finally { Environment.SetEnvironmentVariable("STEMKIT_FFMPEG", configured); }
        PlayerExport.Mp3(snapshot, mp3, "Replace", "Artist", CancellationToken.None, coverJpeg: cover);
        Check(File.ReadAllBytes(mp3).Length > 1000 && !Directory.GetFiles(root, ".stemkit-export-*").Any(), "A completed export must safely replace a confirmed existing destination");
    }
    private static void Tests(string root)
    {
        string audio = Path.Combine(root, "song.wav"); Tone(audio, 440);
        for (int lane = 0; lane < 4; lane++) Tone(Path.Combine(root, StemMixer.StemNames[lane] + ".wav"), 110 * (lane + 1));
        string donor = Path.Combine(root, "donor.wav"), earlierDonor = Path.Combine(root, "earlier-donor.wav"); Tone(donor, 550, 9); Tone(earlierDonor, 660, 8);
        using (var live = new StemMixer(audio, root))
        {
            live.SetLoudness(1.3f); live.ReplaceStem(0, donor, .8f); live.ReplaceStem(2, earlierDonor, 1.1f);
            live.Volume = .48f; var mix = new PlayerMix(); mix.Levels[0] = .37f; mix.Levels[1] = .2f; mix.Levels[2] = .6f; mix.Muted[3] = true; live.ApplyMix(mix);
            live.SetStemShift(0, -3); live.SetStemShift(1, .8); live.Seek(2); live.StartStemLoop(2, 1, 120, 0); live.ExtendStemLoop(2, 5, 120);
            long position = live.PositionFrames;
            using (var snapshot = live.ExportCopy())
            {
                Check(snapshot.PositionFrames == 0 && live.PositionFrames == position && snapshot.TotalFrames == live.TotalFrames && snapshot.StemDuration(0) == 9, "Taking an export snapshot must start at zero, preserve playback position and keep longer donor tails");
                float[] expected = Render(live); Equal(expected, Render(snapshot), "The export copy must exactly reproduce source choices, faders, mute, master volume, loudness, positive/negative offsets and loops");
                live.Volume = 0; mix.ApplyPreset("acapella"); live.ApplyMix(mix); live.SetStemShift(0, 0); live.ReplaceStem(0, earlierDonor, 2); live.Seek(3);
                Equal(expected, Render(snapshot), "Further playback edits and source replacements must not change the captured export");
                live.Dispose(); Encode(root, snapshot, expected);
            }
        }
        using (var live = new StemMixer(audio, root))
        {
            live.Volume = .6f; var mix = new PlayerMix(); mix.Solo[0] = mix.Solo[2] = true; mix.Muted[2] = true; mix.Levels[0] = .4f; live.ApplyMix(mix);
            using (var snapshot = live.ExportCopy()) Equal(Render(live), Render(snapshot), "Multiple solos and muted solo lanes must retain their audible selection");
            live.OriginalMode = true; live.SetLoudness(.7f); live.SetStemShift(0, 2);
            using (var snapshot = live.ExportCopy()) Equal(Render(live), Render(snapshot), "Original playback mode must export the original audio at its current volume, without adding stems");
        }
        using (var live = new StemMixer(audio, ""))
        using (var snapshot = live.ExportCopy())
        {
            Equal(Render(live), Render(snapshot), "Songs without stems must still export their original audio");
            bool invalidExtension = false; try { PlayerExport.Mp3(snapshot, audio, "Song", "Artist", CancellationToken.None); } catch (IOException) { invalidExtension = true; }
            Check(invalidExtension && File.Exists(audio), "Export must reject a filename with a different audio extension");
        }
        string originalMp3 = Path.Combine(root, "original.mp3");
        using (var live = new StemMixer(audio, ""))
        using (var snapshot = live.ExportCopy()) PlayerExport.Mp3(snapshot, originalMp3, "Original", "Artist", CancellationToken.None);
        using (var live = new StemMixer(originalMp3, ""))
        using (var snapshot = live.ExportCopy())
        {
            live.Volume = 0; live.Dispose();
            Check(Render(snapshot).Any(sample => Math.Abs(sample) > .01), "An MP3-only snapshot must keep its own decoder and frozen volume after the player closes the source");
            bool protectedSource = false; try { PlayerExport.Mp3(snapshot, originalMp3, "Song", "Artist", CancellationToken.None); } catch (IOException) { protectedSource = true; }
            Check(protectedSource && File.Exists(originalMp3), "Export must reject overwriting its source MP3");
        }
        string tunedFolder = Path.Combine(root, "live-export"); Directory.CreateDirectory(tunedFolder);
        var originalTransform = new PlaybackTransform(new StemMatchPlan { Tempo = 1.25, Pitch = Math.Pow(2, 2 / 12.0) }, Rate, Rate * Seconds * 4 / 5);
        var transforms = Enumerable.Range(0, 4).Select(lane => new PlaybackTransform(originalTransform.Plan, Rate, originalTransform.Frames, lane)).ToArray();
        using (var live = new StemMixer(audio, root, null, originalTransform, transforms))
        {
            live.SetStemShift(0, -.4); live.StartStemLoop(1, 1.2, 150, 0); live.Volume = .4f;
            using (var snapshot = live.ExportCopy())
            {
                float[] expected = Render(live);
                Equal(expected, Render(snapshot), "Live tempo/key snapshots must exactly reproduce transformed audio, source shifts and loops");
                live.Dispose(); Encode(tunedFolder, snapshot, expected);
            }
        }
    }
    [STAThread]
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "stemkit-export-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { Tests(root); Console.WriteLine("MP3 export: " + checks + " checks passed."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "stemkit-export-"), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true); }
    }
}
