using System;
using System.IO;
using System.Linq;

internal static class ReleaseLauncherTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static int Main(string[] args)
    {
        try
        {
            string library = Path.GetFullPath(args[0]), data = Path.Combine(library, "data"); Directory.CreateDirectory(data);
            Check(ReleaseLauncher.ResolveMode(new string[0]) == "player", "Default mode changed.");
            Check(ReleaseLauncher.ResolveMode(new[] { "--downloader", "--library", library }) == "downloader", "Add songs did not select downloader mode.");
            Check(ReleaseLauncher.ResolveMode(new[] { "--check" }) == "check", "Check mode changed.");
            string legacy = Path.Combine(data, "launcher-player.log");
            using (FileStream locked = new FileStream(legacy, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                string first, second, downloader;
                using (StreamWriter a = ReleaseLauncher.OpenLog(library, "player", out first))
                using (StreamWriter b = ReleaseLauncher.OpenLog(library, "player", out second))
                using (StreamWriter c = ReleaseLauncher.OpenLog(library, "downloader", out downloader))
                {
                    Check(first != second && first != downloader && second != downloader, "Launches share a log filename.");
                    a.WriteLine("first player"); b.WriteLine("second player"); c.WriteLine("drop box");
                    Check(ReleaseLauncher.Tail(first).Contains("first player"), "An active log cannot be read for diagnostics.");
                    Check(ReleaseLauncher.Tail(second).Contains("second player"), "The second launch overwrote the first log.");
                    Check(ReleaseLauncher.Tail(downloader).Contains("drop box"), "The drop box cannot log while the player is running.");
                    a.WriteLine("spawn UNKNOWN: original startup failure");
                    Check(ReleaseLauncher.Tail(first).Contains("original startup failure"), "Reporting startup failure masks its original diagnostic.");
                }
                Check(ReleaseLauncher.Tail(legacy).Contains("could not be read"), "A locked diagnostic log must not replace the original startup error with another exception.");
                // A stream per launch also tolerates many starts within the same millisecond.
                string[] logs = new string[30];
                for (int i = 0; i < logs.Length; i++) using (StreamWriter writer = ReleaseLauncher.OpenLog(library, "player", out logs[i])) writer.WriteLine(i);
                Check(logs.Distinct().Count() == logs.Length, "Rapid repeated launches reuse a log.");
            }
            Console.WriteLine("Launcher regression checks passed: locked legacy log, concurrent logs, rapid repeats, and launch modes."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
