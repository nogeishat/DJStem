using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static class PlayerMatchedCacheTests
{
    private static string Write(string folder, char id, int size, int age)
    {
        string file = Path.Combine(folder, new string(id, 64) + ".wav");
        File.WriteAllBytes(file, new byte[size]);
        File.SetLastAccessTimeUtc(file, DateTime.UtcNow.AddDays(-age)); return file;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference KeepTemporary(string file)
    {
        object owner = new object(); PlayerMatchedCache.Keep(owner, file); return new WeakReference(owner);
    }
    private static void CheckLimits(string root, Action<bool, string> check)
    {
        string project = Path.Combine(root, "limit-project"), folder = Path.Combine(root, "limit-cache"), library = Path.Combine(root, "library");
        Directory.CreateDirectory(project); Directory.CreateDirectory(folder); Directory.CreateDirectory(library);
        string configFile = Path.Combine(project, "settings.json"), source = Path.Combine(library, "vocals.wav");
        File.WriteAllText(source, "original audio");
        File.WriteAllText(configFile, new JavaScriptSerializer().Serialize(new { matchedStemsRoot = folder, libraryRoot = library, otherSetting = "kept" }));
        check(PlayerMatchedCache.LimitBytes(project) == PlayerMatchedCache.DefaultLimitBytes, "Existing installations must keep the default 30 GB budget");
        string[] files = new string[3];
        for (int index = 0; index < files.Length; index++)
        {
            files[index] = Write(folder, (char)('a' + index), 0, 3 - index);
            using (var writer = new FileStream(files[index], FileMode.Open, FileAccess.Write)) writer.SetLength(40000000);
        }
        long total = Task.Run(() => PlayerMatchedCache.SetLimit(project, .1m)).GetAwaiter().GetResult();
        check(total == 80000000 && !File.Exists(files[0]) && File.Exists(files[1]) && File.Exists(files[2]), "Saving a lower budget must immediately delete only enough of the oldest files to fit");
        var saved = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(configFile));
        check(PlayerMatchedCache.LimitBytes(project) == 100000000 && Convert.ToString(saved["libraryRoot"]) == library && Convert.ToString(saved["matchedStemsRoot"]) == folder && Convert.ToString(saved["otherSetting"]) == "kept", "A custom budget must persist without replacing library paths or other configuration");
        PlayerMatchedCache.EnsureRoom(project, 30000000);
        check(!File.Exists(files[1]) && File.Exists(files[2]), "Future renders must reserve space against the saved custom budget");
        bool oversized = false;
        try { PlayerMatchedCache.EnsureRoom(project, 100000001); } catch (IOException error) { oversized = !error.Message.Contains("30 GB"); }
        check(oversized, "Oversized renders must use the configured budget and describe the editable setting");
        PlayerMatchedCache.RenderGate.Wait(); Task<long> pending;
        try
        {
            pending = PlayerMatchedCache.SetLimit(project, .2m);
            check(!pending.IsCompleted && PlayerMatchedCache.LimitBytes(project) == 100000000, "Budget changes must wait for an ongoing render before changing or deleting files");
        }
        finally { PlayerMatchedCache.RenderGate.Release(); }
        // This test runner has no UI context, so the queued update can finish independently.
        check(pending.GetAwaiter().GetResult() == 40000000 && File.Exists(files[2]) && PlayerMatchedCache.LimitBytes(project) == 200000000, "Increasing the budget must retain cached audio and persist the larger size");
        for (int index = 0; index < 2; index++)
        {
            using (var writer = new FileStream(files[index], FileMode.Create, FileAccess.Write)) writer.SetLength(40000000);
        }
        object owner = new object(); PlayerMatchedCache.Keep(owner, files);
        total = Task.Run(() => PlayerMatchedCache.SetLimit(project, .1m)).GetAwaiter().GetResult();
        check(total == 120000000 && Array.TrueForAll(files, File.Exists) && PlayerMatchedCache.LimitBytes(project) == 100000000, "Lowering the limit must protect live prepared audio while remembering the new budget for later cleanup");
        bool rejected = false;
        try { Task.Run(() => PlayerMatchedCache.SetLimit(project, 0)).GetAwaiter().GetResult(); } catch (ArgumentOutOfRangeException) { rejected = true; }
        check(rejected && PlayerMatchedCache.LimitBytes(project) == 100000000 && File.ReadAllText(source) == "original audio", "Invalid limits must preserve configuration, and cache cleanup must leave original stems untouched");
        GC.KeepAlive(owner);
        foreach (object invalid in new object[] { -1, 0, "invalid", true, 1000001, .15m })
        {
            File.WriteAllText(configFile, new JavaScriptSerializer().Serialize(new { matchedStemsMaxGb = invalid }));
            check(PlayerMatchedCache.LimitBytes(project) == PlayerMatchedCache.DefaultLimitBytes, "Invalid saved budgets must fall back to the default");
        }
    }
    internal static void Run(string root, Action<bool, string> check)
    {
        string project = Path.Combine(root, "cache-project"), folder = Path.Combine(root, "external-cache");
        Directory.CreateDirectory(project); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(project, "settings.json"), new JavaScriptSerializer().Serialize(new { matchedStemsRoot = folder }));
        check(PlayerMatchedCache.Folder(project) == folder, "Matched stems must honor their configured external folder");
        check(PlayerMatchedCache.Folder(Path.Combine(root, "unconfigured-project")) == Path.Combine(root, "unconfigured-project", "data", "matched-stems"), "Unconfigured projects must retain an isolated local cache");
        check(PlayerMatchedCache.DefaultLimitBytes == 30000000000L, "Cache must default to 30 decimal GB");
        string reservedFolder = Path.Combine(root, "reserved-cache"); Directory.CreateDirectory(reservedFolder);
        string reservedOldest = Write(reservedFolder, 'a', 6, 3); Write(reservedFolder, 'b', 6, 2); Write(reservedFolder, 'c', 6, 1);
        check(PlayerMatchedCache.Trim(reservedFolder, 18, 6, new HashSet<string>()) == 12 && !File.Exists(reservedOldest), "Cleanup must make room for a complete new render before it starts writing");
        string oldest = Write(folder, 'a', 6, 3), middle = Write(folder, 'b', 6, 2), newest = Write(folder, 'c', 6, 1);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        check(PlayerMatchedCache.Trim(folder, 12, 0, keep) == 12 && !File.Exists(oldest) && File.Exists(middle) && File.Exists(newest), "Cleanup must evict the least recently used file first");
        oldest = Write(folder, 'a', 6, 3);
        DateTime modified = File.GetLastWriteTimeUtc(oldest); PlayerMatchedCache.Touch(oldest);
        check(File.GetLastWriteTimeUtc(oldest) == modified, "Reuse must not change the source identity used by chained renders");
        check(PlayerMatchedCache.Trim(folder, 12, 0, keep) == 12 && File.Exists(oldest) && !File.Exists(middle), "Recently reused audio must survive ahead of less recently used audio");
        middle = Write(folder, 'b', 6, 4);
        object bank = new object(); PlayerMatchedCache.Keep(bank, middle);
        check(PlayerMatchedCache.Trim(folder, 12, 0, PlayerMatchedCache.Protected()) == 12 && File.Exists(middle) && !File.Exists(newest), "Prepared stems must survive cleanup even before their readers are opened");
        newest = Write(folder, 'c', 6, 5);
        using (var reader = new FileStream(newest, FileMode.Open, FileAccess.Read, FileShare.Read))
            check(PlayerMatchedCache.Trim(folder, 12, 0, PlayerMatchedCache.Protected()) == 12 && File.Exists(newest) && !File.Exists(oldest), "Audio open for playback must be skipped and cleanup must continue to the next file");
        GC.KeepAlive(bank);
        keep.Add(middle); keep.Add(newest);
        check(PlayerMatchedCache.Trim(folder, 12, 6, keep) == 12, "Cleanup must preserve live files when space cannot be reclaimed");
        bool oversized = false;
        try { PlayerMatchedCache.Trim(folder, 12, 13, keep); } catch (IOException) { oversized = true; }
        check(oversized, "A render larger than the cache budget must be rejected before writing");
        bool full = false;
        try { PlayerMatchedCache.EnsureRoom(project, PlayerMatchedCache.DefaultLimitBytes, middle, newest); } catch (IOException) { full = true; }
        check(full && File.Exists(middle) && File.Exists(newest), "A full protected cache must reject new rendering without deleting live audio");
        string abandoned = Path.Combine(folder, new string('d', 64) + "." + Guid.NewGuid().ToString("N") + ".tmp.wav");
        File.WriteAllBytes(abandoned, new byte[6]); File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddDays(-2));
        check(PlayerMatchedCache.Trim(folder, 12, 0, keep) == 12 && !File.Exists(abandoned), "Abandoned partial renders must count toward the budget and be reclaimable");
        string unrelated = Path.Combine(folder, "personal.wav"); File.WriteAllBytes(unrelated, new byte[6]);
        check(PlayerMatchedCache.Trim(folder, 12, 0, keep) == 18 && File.Exists(unrelated), "Cleanup must never delete files outside StemKit's generated filename pattern");
        File.Delete(unrelated);
        WeakReference retired = KeepTemporary(oldest);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        check(!retired.IsAlive && !PlayerMatchedCache.Protected().Contains(oldest), "Files owned only by retired preparation objects must become evictable");
        File.WriteAllText(Path.Combine(project, "settings.json"), "{\"matchedStemsRoot\":\"cache\"}");
        check(PlayerMatchedCache.Folder(project) == Path.Combine(project, "cache"), "Relative cache folders must resolve against the project");
        CheckLimits(root, check);
    }
}
