using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Text.RegularExpressions;

// Only generated WAVs in the dedicated cache may be evicted. Owners keep
// prepared files alive even when a staged lane has no open audio reader yet.
internal static class PlayerMatchedCache
{
    internal const long DefaultLimitBytes = 30L * 1000 * 1000 * 1000;
    internal const decimal MinLimitGb = .1m, MaxLimitGb = 1000000m;
    internal static readonly SemaphoreSlim RenderGate = new SemaphoreSlim(1);
    private static readonly object ownersGate = new object();
    private sealed class OwnerFiles
    {
        internal WeakReference Owner;
        internal readonly HashSet<string> Files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
    private static readonly List<OwnerFiles> owners = new List<OwnerFiles>();
    private static readonly Regex generatedName = new Regex(@"^[a-f0-9]{64}(\.[a-f0-9]{32}\.tmp)?\.wav$", RegexOptions.IgnoreCase);

    private static Dictionary<string, object> ReadConfig(string project)
    {
        string file = PlayerData.Settings(project);
        return File.Exists(file) ? new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file)) : new Dictionary<string, object>();
    }
    internal static long LimitBytes(string project)
    {
        object value;
        if (ReadConfig(project).TryGetValue("matchedStemsMaxGb", out value) && value != null && !(value is bool))
        {
            try
            {
                decimal gb = Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
                if (gb >= MinLimitGb && gb <= MaxLimitGb && gb == Decimal.Round(gb, 1)) return (long)(gb * 1000000000m);
            }
            catch (FormatException) { }
            catch (InvalidCastException) { }
            catch (OverflowException) { }
        }
        return DefaultLimitBytes;
    }
    internal static async Task<long> SetLimit(string project, decimal gb)
    {
        if (gb < MinLimitGb || gb > MaxLimitGb || gb != Decimal.Round(gb, 1)) throw new ArgumentOutOfRangeException("gb");
        // Serialize changing the budget with rendering so a file already being
        // written finishes before the new budget is applied and reclaimed.
        await RenderGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(delegate
            {
                string folder = Folder(project), file = PlayerData.Settings(project), temporary = file + "." + Guid.NewGuid().ToString("N") + ".pending";
                var config = ReadConfig(project); config["matchedStemsMaxGb"] = gb;
                try
                {
                    File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(config));
                    if (File.Exists(file)) File.Replace(temporary, file, null); else File.Move(temporary, file);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return Reclaim(folder, (long)(gb * 1000000000m), 0);
            }).ConfigureAwait(false);
        }
        finally { RenderGate.Release(); }
    }

    internal static string Folder(string project)
    {
        string settings = PlayerData.Settings(project);
        if (File.Exists(settings))
        {
            var config = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(settings));
            object configured;
            if (config.TryGetValue("matchedStemsRoot", out configured) && !String.IsNullOrWhiteSpace(Convert.ToString(configured)))
            {
                string folder = Convert.ToString(configured);
                return Path.GetFullPath(Path.IsPathRooted(folder) ? folder : Path.Combine(PlayerData.Root(project), folder));
            }
        }
        return Path.GetFullPath(PlayerData.Data(project, "matched-stems"));
    }
    internal static void Keep(object owner, params string[] files)
    {
        if (owner == null || files == null) return;
        lock (ownersGate)
        {
            OwnerFiles entry = owners.Find(item => Object.ReferenceEquals(item.Owner.Target, owner));
            if (entry == null) { entry = new OwnerFiles { Owner = new WeakReference(owner) }; owners.Add(entry); }
            foreach (string file in files) if (!String.IsNullOrEmpty(file)) entry.Files.Add(Path.GetFullPath(file));
        }
    }
    internal static HashSet<string> Protected(params string[] extra)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (ownersGate)
        {
            for (int i = owners.Count - 1; i >= 0; i--)
            {
                object owner = owners[i].Owner.Target;
                if (owner == null) owners.RemoveAt(i);
                else { files.UnionWith(owners[i].Files); GC.KeepAlive(owner); }
            }
        }
        foreach (string file in extra) if (!String.IsNullOrEmpty(file)) files.Add(Path.GetFullPath(file));
        return files;
    }
    internal static void Touch(string file)
    {
        // Access time is explicit: Windows can disable its automatic updates.
        // Keep modification time stable because it forms part of render keys.
        try { File.SetLastAccessTimeUtc(file, DateTime.UtcNow); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    internal static long Trim(string folder, long limit, long reserve, HashSet<string> protectedFiles)
    {
        if (reserve < 0 || reserve > limit) throw new IOException("This prepared audio file exceeds the matched-stems size limit. Increase the maximum size in Settings.");
        Directory.CreateDirectory(folder);
        var entries = new DirectoryInfo(folder).GetFiles();
        long total = entries.Sum(file => file.Length);
        foreach (FileInfo file in entries.OrderBy(file => file.LastAccessTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal))
        {
            if (total <= limit - reserve) break;
            if (!generatedName.IsMatch(file.Name) || (file.Attributes & FileAttributes.ReparsePoint) != 0 || protectedFiles.Contains(file.FullName)) continue;
            // An unfinished render from another process might not have opened
            // its output yet; only reclaim abandoned temporary files.
            if (file.Name.EndsWith(".tmp.wav", StringComparison.OrdinalIgnoreCase) && file.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-1)) continue;
            try
            {
                long size = file.Length;
                file.Delete(); total -= size;
            }
            catch (IOException) { } // Windows protects files open for playback/rendering.
            catch (UnauthorizedAccessException) { }
        }
        return total;
    }
    private static long Reclaim(string folder, long limit, long reserve, params string[] extra)
    {
        long total = Trim(folder, limit, reserve, Protected(extra));
        if (total > limit - reserve)
        {
            // Release weak owners left by completed preparation tasks before
            // concluding that the remaining cache belongs to the live session.
            GC.Collect();
            total = Trim(folder, limit, reserve, Protected(extra));
        }
        return total;
    }
    internal static void EnsureRoom(string project, long reserve, params string[] extra)
    {
        long limit = LimitBytes(project);
        if (Reclaim(Folder(project), limit, reserve, extra) > limit - reserve)
            throw new IOException("The matched-stems cache cannot fit within its size limit while files are in use. Increase the maximum size in Settings or reset prepared replacements before trying again.");
    }
    internal static IDisposable StartMaintenance(string project)
    {
        return new Timer(delegate
        {
            if (!RenderGate.Wait(0)) return;
            try { EnsureRoom(project, 0); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally { RenderGate.Release(); }
        }, null, TimeSpan.Zero, TimeSpan.FromMinutes(10));
    }
}
