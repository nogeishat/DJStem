using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class PortableLauncher
{
    private static readonly object LogLock = new object();
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        string root = Path.GetDirectoryName(Application.ExecutablePath);
        string name = Path.GetFileNameWithoutExtension(Application.ExecutablePath);
        string mode = name == "Start-Player" ? "player" : name == "Check-USB" ? "check" : "downloader";
        bool quiet = Array.IndexOf(args, "--quiet") >= 0;
        if (Array.IndexOf(args, "--check") >= 0) mode = "check";
        string log = Path.Combine(root, "data", "launcher-" + mode + ".log");
        try
        {
            if (!Environment.Is64BitOperatingSystem) throw new Exception("This USB edition requires 64-bit Windows 10/11.");
            using (RegistryKey framework = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                if (framework == null || Convert.ToInt32(framework.GetValue("Release", 0)) < 528040) throw new Exception("This PC needs .NET Framework 4.8 or newer to run StemKit.");
            string node = Path.Combine(root, "tools", "node.exe");
            if (!File.Exists(node)) throw new Exception("The USB copy is incomplete. Copy the entire StemKit-USB folder, including tools.");
            Directory.CreateDirectory(Path.GetDirectoryName(log));
            using (var writer = new StreamWriter(log, false, new UTF8Encoding(false)))
            using (var process = new Process())
            {
                writer.AutoFlush = true;
                process.StartInfo = new ProcessStartInfo(node, "\"" + Path.Combine(root, "scripts", "portable", "boot.mjs") + "\" " + mode) {
                    WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                // Node reads NODE_OPTIONS before the JS bootstrap can clear it.
                process.StartInfo.EnvironmentVariables.Remove("NODE_OPTIONS");
                process.StartInfo.EnvironmentVariables.Remove("NODE_PATH");
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock (LogLock) writer.WriteLine(e.Data); };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock (LogLock) writer.WriteLine(e.Data); };
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine(); process.WaitForExit();
                if (process.ExitCode != 0) throw new Exception("StemKit could not start. Details:\n" + log + "\n\n" + Tail(log));
            }
            if (mode == "check" && !quiet) MessageBox.Show("USB checks passed.\n\n" + Path.Combine(root, "data", "usb-check.json"), "StemKit USB", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception error) {
            if (!quiet) MessageBox.Show(error.Message, "StemKit USB", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else { try { File.AppendAllText(log, "\n" + error); } catch { } }
            return 1;
        }
    }
    private static string Tail(string file) { string text = File.ReadAllText(file); return text.Length <= 1800 ? text : text.Substring(text.Length - 1800); }
}
