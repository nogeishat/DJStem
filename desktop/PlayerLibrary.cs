using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;

internal sealed class SongInfo
{
    internal string Title, Artist, Audio, Folder, Cover;
    internal string YouTubeId;
    internal DateTime? AddedAt;
    internal double? Bpm;
    internal double? TuningHz;
    internal string Key;
    internal bool HasStems;
    internal SavedMix SavedMix;
    internal string PlaybackKey { get { return SavedMix == null ? Audio : "mix:" + SavedMix.Id; } }
    internal string MusicDetails { get { return (Bpm.HasValue ? Bpm.Value.ToString("0.##", CultureInfo.InvariantCulture) + " BPM" : "BPM unknown") + " · " + (String.IsNullOrWhiteSpace(Key) ? "Key unknown" : Key); } }
    public override string ToString() { return Title + " — " + Artist; }
}

internal static class PlayerLibrary
{
    internal sealed class CompletedDownload
    {
        internal string Id;
        internal SongInfo Song;
        internal DateTime FinishedAt;
    }

    // Download status alone also means the MP3 is saved before analysis and
    // separation. Only the final state of all three stages is queue-ready.
    internal static List<CompletedDownload> CompletedDownloads(string project, string library, List<SongInfo> songs)
    {
        string file = PlayerData.Data(project, "queue.json");
        if (!File.Exists(file)) return new List<CompletedDownload>();
        try
        {
            var json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
            var state = json.DeserializeObject(File.ReadAllText(file)) as Dictionary<string, object>;
            object raw; object[] jobs;
            if (state == null || !state.TryGetValue("jobs", out raw) || (jobs = raw as object[]) == null) return null;
            var byPath = songs.ToDictionary(song => Path.GetFullPath(song.Audio), StringComparer.OrdinalIgnoreCase);
            var completed = new List<CompletedDownload>();
            foreach (object item in jobs)
            {
                var job = item as Dictionary<string, object>;
                if (job == null) continue;
                Func<string, string> text = key => job.TryGetValue(key, out raw) ? Convert.ToString(raw) : "";
                if (text("outputType") != "mp3" || text("status") != "completed" || text("analysisStatus") != "completed" || text("stemStatus") != "completed") continue;
                string id = text("id"), filename = text("filename"); DateTime finished;
                if (String.IsNullOrWhiteSpace(id) || String.IsNullOrWhiteSpace(filename) || !DateTime.TryParse(text("completedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out finished)) continue;
                try
                {
                    string audio = Path.GetFullPath(Path.Combine(library, filename)); SongInfo song;
                    if (!Inside(library, audio) || !byPath.TryGetValue(audio, out song) || !song.HasStems || new FileInfo(audio).Length == 0) continue;
                    var metadata = json.DeserializeObject(File.ReadAllText(Path.Combine(song.Folder, "metadata.json"))) as Dictionary<string, object>;
                    object analysisValue, sourceValue, versionValue, metadataId;
                    if (metadata == null || !metadata.TryGetValue("id", out metadataId) || Convert.ToString(metadataId) != text("videoId") ||
                        !metadata.TryGetValue("analysis", out analysisValue)) continue;
                    var analysis = analysisValue as Dictionary<string, object>;
                    if (analysis == null || !analysis.TryGetValue("source", out sourceValue) || Convert.ToString(sourceValue) != "mp3" ||
                        !analysis.TryGetValue("version", out versionValue) || Convert.ToInt32(versionValue) != 2) continue;
                    // The completion marker is written after the WAVs; reject
                    // missing, empty, or partially replaced artifacts on disk.
                    if (!(json.DeserializeObject(File.ReadAllText(Path.Combine(song.Folder, "separation.json"))) is Dictionary<string, object>)) continue;
                    bool valid = true;
                    foreach (string stem in StemMixer.StemNames)
                    {
                        using (var reader = new BinaryReader(File.OpenRead(Path.Combine(song.Folder, stem + ".wav"))))
                        {
                            if (reader.BaseStream.Length <= 44 || new string(reader.ReadChars(4)) != "RIFF") { valid = false; break; }
                            reader.BaseStream.Position = 8;
                            if (new string(reader.ReadChars(4)) != "WAVE") { valid = false; break; }
                        }
                    }
                    if (valid) completed.Add(new CompletedDownload { Id = id, Song = song, FinishedAt = finished });
                }
                catch { /* A worker can replace files while a refresh is reading. Retry next time. */ }
            }
            return completed.OrderBy(download => download.FinishedAt).ThenBy(download => download.Id, StringComparer.Ordinal).ToList();
        }
        catch { return null; /* A transient queue read must not forget observed completions. */ }
    }

    internal static bool Inside(string root, string file)
    {
        return Path.GetFullPath(file).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static List<SongInfo> Scan(string root)
    {
        string mp3 = Path.Combine(root, "mp3"), stems = Path.Combine(root, "stems");
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The music drive is unavailable: " + root);
        List<SongInfo> songs = new List<SongInfo>();
        if (!Directory.Exists(mp3)) return songs;
        JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
        foreach (string artistDir in Directory.GetDirectories(mp3))
        foreach (string audio in Directory.GetFiles(artistDir, "*.mp3"))
        {
            if (!Inside(mp3, audio)) continue;
            string artist = Path.GetFileName(artistDir), title = Path.GetFileNameWithoutExtension(audio);
            string folder = Path.Combine(stems, artist, title);
            if (!Inside(stems, folder)) continue;
            string metadataFile = Path.Combine(folder, "metadata.json");
            string videoId = null, key = null; DateTime? addedAt = null; double? bpm = null, tuningHz = null;
            try
            {
                var metadata = (Dictionary<string, object>)json.DeserializeObject(File.ReadAllText(metadataFile));
                tuningHz = TuningReference.Read(metadata);
                object value;
                if (metadata.TryGetValue("artist", out value) && !String.IsNullOrWhiteSpace(Convert.ToString(value))) artist = Convert.ToString(value);
                if ((metadata.TryGetValue("track", out value) || metadata.TryGetValue("title", out value)) && !String.IsNullOrWhiteSpace(Convert.ToString(value))) title = Convert.ToString(value);
                if (metadata.TryGetValue("id", out value)) { string id = Convert.ToString(value); if (System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_-]{11}$")) videoId = id; }
                double tempo;
                if (metadata.TryGetValue("bpm", out value) && Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out tempo) && tempo >= 30 && tempo <= 300) bpm = tempo;
                if (metadata.TryGetValue("key", out value))
                {
                    string savedKey = Convert.ToString(value).Trim();
                    if (System.Text.RegularExpressions.Regex.IsMatch(savedKey, "^[A-G](?:#|b|♯|♭)? (?:major|minor)$")) key = savedKey.Replace("#", "♯").Replace("b", "♭");
                }
                DateTime downloaded;
                if (metadata.TryGetValue("downloadedAt", out value) && DateTime.TryParse(Convert.ToString(value), out downloaded)) addedAt = downloaded.ToLocalTime();
                else if (metadata.TryGetValue("stemkit", out value))
                {
                    var kit = value as Dictionary<string, object>; object date; DateTime parsed;
                    if (kit != null && kit.TryGetValue("downloadedAt", out date) && DateTime.TryParse(Convert.ToString(date), out parsed)) addedAt = parsed.ToLocalTime();
                }
            }
            catch { /* MP3s can still play before metadata or stems are available. */ }
            bool ready = File.Exists(Path.Combine(folder, "separation.json"));
            foreach (string stem in StemMixer.StemNames) ready &= File.Exists(Path.Combine(folder, stem + ".wav"));
            songs.Add(new SongInfo { Title = title, Artist = artist, Audio = audio, Folder = folder, Cover = Path.Combine(folder, "cover.jpg"), HasStems = ready, YouTubeId = videoId, AddedAt = addedAt, Bpm = bpm, Key = key, TuningHz = tuningHz });
        }
        songs.Sort(delegate(SongInfo a, SongInfo b) { int artist = String.Compare(a.Artist, b.Artist, StringComparison.CurrentCultureIgnoreCase); return artist != 0 ? artist : String.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase); });
        return songs;
    }

    internal static string ConfiguredRoot(string project)
    {
        string custom = Environment.GetEnvironmentVariable("STEMKIT_LIBRARY");
        if (!String.IsNullOrWhiteSpace(custom)) return Path.GetFullPath(custom);
        string file = PlayerData.Settings(project);
        if (File.Exists(file))
        {
            var settings = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(file));
            object value;
            if (settings.TryGetValue("libraryRoot", out value)) return Path.GetFullPath(Path.Combine(PlayerData.Root(project), Convert.ToString(value)));
        }
        return Path.Combine(project, "downloads");
    }
}
