using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

internal static class PlayerExport
{
    // Stream the playback mix to the encoder, keeping memory use independent of song length.
    internal static void Mp3(StemMixer snapshot, string destination, string title, string artist, CancellationToken cancellation, IProgress<int> progress = null, byte[] coverJpeg = null)
    {
        cancellation.ThrowIfCancellationRequested();
        destination = Path.GetFullPath(destination);
        if (!Path.GetExtension(destination).Equals(".mp3", StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose an MP3 filename ending in .mp3.");
        if (snapshot.UsesFile(destination)) throw new IOException("Choose a new filename outside the source audio files.");
        string temporary = Path.Combine(Path.GetDirectoryName(destination), ".stemkit-export-" + Guid.NewGuid().ToString("N") + ".tmp.mp3");
        string artwork = coverJpeg == null ? null : Path.ChangeExtension(temporary, ".cover.jpg");
        string ffmpeg = Environment.GetEnvironmentVariable("STEMKIT_FFMPEG");
        if (String.IsNullOrWhiteSpace(ffmpeg)) ffmpeg = File.Exists(@"C:\ffmpeg\bin\ffmpeg.exe") ? @"C:\ffmpeg\bin\ffmpeg.exe" : "ffmpeg.exe";
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "f32le", "-ar", snapshot.WaveFormat.SampleRate.ToString(), "-ac", "2", "-i", "pipe:0" };
        if (artwork != null) arguments.AddRange(new[] { "-i", artwork, "-map", "0:a:0", "-map", "1:v:0", "-c:v", "copy", "-disposition:v:0", "attached_pic", "-metadata:s:v:0", "title=Cover", "-metadata:s:v:0", "comment=Cover (front)" });
        else arguments.Add("-vn");
        arguments.AddRange(new[] { "-c:a", "libmp3lame", "-ar", "44100", "-b:a", "320k", "-id3v2_version", "3", "-metadata", "title=" + title, "-metadata", "artist=" + artist, "-f", "mp3", temporary });
        var args = new StringBuilder(); foreach (string argument in arguments) { if (args.Length > 0) args.Append(' '); args.Append(Quote(argument)); }
        try
        {
            if (artwork != null) File.WriteAllBytes(artwork, coverJpeg);
            using (var encoder = new Process { StartInfo = new ProcessStartInfo(ffmpeg, args.ToString()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true } })
            {
                encoder.Start(); Task<string> errors = encoder.StandardError.ReadToEndAsync();
                using (cancellation.Register(delegate { try { if (!encoder.HasExited) encoder.Kill(); } catch { } }))
                {
                    try
                    {
                        snapshot.SeekFrame(0); var samples = new float[8192]; var bytes = new byte[samples.Length * sizeof(float)]; int read, lastProgress = -1;
                        while ((read = snapshot.Read(samples, 0, samples.Length)) > 0)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            Buffer.BlockCopy(samples, 0, bytes, 0, read * sizeof(float)); encoder.StandardInput.BaseStream.Write(bytes, 0, read * sizeof(float));
                            int percent = (int)(snapshot.PositionFrames * 99 / Math.Max(1, snapshot.TotalFrames));
                            if (percent != lastProgress && progress != null) { progress.Report(percent); lastProgress = percent; }
                        }
                        encoder.StandardInput.Close(); encoder.WaitForExit();
                        string error = errors.GetAwaiter().GetResult(); cancellation.ThrowIfCancellationRequested();
                        if (encoder.ExitCode != 0) throw new IOException("MP3 export failed: " + Tail(error));
                    }
                    catch (IOException)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (encoder.HasExited) throw new IOException("MP3 export failed: " + Tail(errors.GetAwaiter().GetResult()));
                        throw;
                    }
                    finally { try { if (!encoder.HasExited) { encoder.Kill(); encoder.WaitForExit(); } } catch { } }
                }
            }
            cancellation.ThrowIfCancellationRequested();
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) throw new IOException("The MP3 encoder produced no audio file.");
            // Only replace a confirmed destination after the encoder finishes successfully.
            if (File.Exists(destination)) File.Replace(temporary, destination, null); else File.Move(temporary, destination);
            if (progress != null) progress.Report(100);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally { if (artwork != null && File.Exists(artwork)) File.Delete(artwork); }
        }
    }
    internal static byte[] Cover(Image original, Image added)
    {
        if (original == null && added == null) return null;
        const int size = 512;
        using (var cover = new Bitmap(size, size, PixelFormat.Format24bppRgb))
        using (var graphics = Graphics.FromImage(cover))
        using (var output = new MemoryStream())
        using (var settings = new EncoderParameters(1))
        {
            graphics.Clear(Color.Black); graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            // Crop each source to the same square as Now playing, then retain
            // its actual left/right half instead of squeezing a whole cover.
            if (original != null && added != null)
            {
                DrawCoverHalf(graphics, original, new Rectangle(0, 0, size / 2, size), size);
                DrawCoverHalf(graphics, added, new Rectangle(size / 2, 0, size / 2, size), size);
            }
            else DrawCoverHalf(graphics, original ?? added, new Rectangle(0, 0, size, size), size);
            settings.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 95L);
            ImageCodecInfo jpeg = Array.Find(ImageCodecInfo.GetImageEncoders(), codec => codec.FormatID == ImageFormat.Jpeg.Guid);
            cover.Save(output, jpeg, settings); return output.ToArray();
        }
    }
    private static void DrawCoverHalf(Graphics graphics, Image source, Rectangle half, int size)
    {
        int side = Math.Min(source.Width, source.Height);
        graphics.SetClip(half);
        graphics.DrawImage(source, new Rectangle(0, 0, size, size), new Rectangle((source.Width - side) / 2, (source.Height - side) / 2, side, side), GraphicsUnit.Pixel);
        graphics.ResetClip();
    }
    private static string Tail(string error) { return error.Length > 1600 ? error.Substring(error.Length - 1600) : error; }
    private static string Quote(string value)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); result.Append(c); slashes = 0;
        }
        result.Append('\\', slashes * 2); return result.Append('"').ToString();
    }
    internal static void CopyStem(string source, string destination)
    {
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose a location outside the source stem file.");
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary, false);
            // SaveFileDialog has already confirmed replacement of an existing destination.
            if (File.Exists(destination)) File.Replace(temporary, destination, null); else File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static string CopyAll(string sourceFolder, string parent, string name)
    {
        parent = Path.GetFullPath(parent);
        string destination = Path.Combine(parent, SafeName(name)); int suffix = 2;
        while (Directory.Exists(destination) || File.Exists(destination)) destination = Path.Combine(parent, SafeName(name) + " (" + suffix++ + ")");
        // Stage the complete set; a failed copy never presents a partial export as finished.
        string staging = Path.Combine(parent, ".stemkit-export-" + Guid.NewGuid().ToString("N"));
        if (!PlayerLibrary.Inside(parent, staging) || !PlayerLibrary.Inside(parent, destination)) throw new IOException("Invalid export destination.");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (string stem in StemMixer.StemNames) File.Copy(Path.Combine(sourceFolder, stem + ".wav"), Path.Combine(staging, stem + ".wav"), false);
            Directory.Move(staging, destination); return destination;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    internal static string SafeName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        name = name.Trim().TrimEnd('.'); if (name.Length > 120) name = name.Substring(0, 120).TrimEnd('.', ' ');
        return name.Length == 0 ? "Stems" : name;
    }
}

internal sealed partial class StemMixer
{
    internal bool UsesFile(string file) { return Array.Exists(sourceFiles, source => String.Equals(source, Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)); }
    internal StemMixer ExportCopy() { return new StemMixer(this); }
    // Freeze settings under the live lock, then open independent readers outside
    // it so decoder initialization cannot hold up the playback callback.
    private StemMixer(StemMixer live)
    {
        try
        {
            lock (live.gate)
            {
                if (live.disposed) throw new ObjectDisposedException("StemMixer");
                HasStems = live.HasStems; TotalFrames = live.TotalFrames; WaveFormat = live.WaveFormat;
                originalMode = live.originalMode; volume = live.volume; loudnessMatched = live.loudnessMatched;
                Array.Copy(live.sourceFiles, sourceFiles, sourceFiles.Length); Array.Copy(live.sourceLoudness, sourceLoudness, sourceLoudness.Length);
                Array.Copy(live.sourceTransforms, sourceTransforms, sourceTransforms.Length);
                for (int lane = 0; lane < 4; lane++)
                {
                    enabled[lane] = live.enabled[lane]; solo[lane] = live.solo[lane]; levels[lane] = live.levels[lane]; stemTiming[lane] = live.stemTiming[lane].Copy();
                    scheduledLevels[lane] = live.scheduledLevels[lane]; scheduledVolumes[lane] = live.scheduledVolumes[lane];
                }
                PlayerMatchedCache.Keep(this, sourceFiles);
            }
            for (int source = 0; source < readers.Length; source++)
            {
                if (sourceFiles[source] != null) readers[source] = PlaybackTransform.OpenFile(sourceFiles[source], sourceTransforms[source]);
            }
            SeekFrame(0);
        }
        catch { Dispose(); throw; }
    }
}
