using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Collections.Generic;
using System.Windows.Forms;

[assembly: AssemblyTitle("DJStem")]
[assembly: AssemblyProduct("DJStem")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
internal static class ReleaseLauncher
{
    private static readonly object LogLock = new object();
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        string root = Path.GetDirectoryName(Application.ExecutablePath), log = null;
        try
        {
            string pointer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DJStem", "library-location.json");
            string library = null;
            bool choose = Array.IndexOf(args, "--choose-library") >= 0;
            int supplied = Array.IndexOf(args, "--library");
            if (supplied >= 0 && supplied + 1 < args.Length) library = Path.GetFullPath(args[supplied + 1]);
            if (library == null && !choose && File.Exists(pointer)) { var saved = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(pointer)); object value; if (saved.TryGetValue("library", out value)) library = Convert.ToString(value); }
            if (library != null && !Directory.Exists(library)) {
                if (MessageBox.Show("Your library folder is unavailable:\n\n" + library + "\n\nReconnect its drive, or choose a different library?", "DJStem", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return 0;
                library = null;
            }
            if (library == null || choose)
            {
                using (LibraryDialog dialog = new LibraryDialog(library)) { if (dialog.ShowDialog() != DialogResult.OK) return 0; library = dialog.Library; }
                Directory.CreateDirectory(Path.GetDirectoryName(pointer));
                string temporary = pointer + ".pending";
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(new { library = library }));
                if (File.Exists(pointer)) File.Replace(temporary, pointer, null); else File.Move(temporary, pointer);
            }
            Directory.CreateDirectory(Path.Combine(library, "data"));
            string mode = Array.IndexOf(args, "--downloader") >= 0 ? "downloader" : Array.IndexOf(args, "--check") >= 0 ? "check" : "player";
            log = Path.Combine(library, "data", "launcher-" + mode + ".log");
            using (StreamWriter writer = new StreamWriter(log, false, new UTF8Encoding(false)))
            using (Process process = new Process())
            {
                writer.AutoFlush = true;
                process.StartInfo = new ProcessStartInfo(Path.Combine(root, "tools", "node.exe"), "\"" + Path.Combine(root, "scripts", "release", "boot.mjs") + "\" " + mode) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
                process.StartInfo.EnvironmentVariables["STEMKIT_USER_DATA"] = library;
                process.StartInfo.EnvironmentVariables.Remove("NODE_OPTIONS"); process.StartInfo.EnvironmentVariables.Remove("NODE_PATH");
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock (LogLock) writer.WriteLine(e.Data); };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock (LogLock) writer.WriteLine(e.Data); };
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine(); process.WaitForExit();
                if (process.ExitCode != 0) throw new Exception("DJStem could not start. Details are saved in:\n" + log + "\n\n" + Tail(log));
            }
            return 0;
        }
        catch (Exception error) { MessageBox.Show(error.Message, "DJStem", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }
    private static string Tail(string file) { string text = File.ReadAllText(file); return text.Length <= 1400 ? text : text.Substring(text.Length - 1400); }
}

internal sealed class LibraryDialog : Form
{
    private readonly TextBox location = new TextBox();
    private readonly RadioButton existing = new RadioButton();
    internal string Library { get; private set; }
    internal LibraryDialog(string previous)
    {
        Text = "Welcome to DJStem"; ClientSize = new Size(640, 295); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10); Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Controls.Add(new Label { Text = "Choose your library folder", Font = new Font("Segoe UI", 16, FontStyle.Bold), Bounds = new Rectangle(24, 20, 590, 36) });
        Controls.Add(new Label { Text = "Your MP3s, stems, playlists, saved mixes and settings stay together here.\nChoose the same folder when updating DJStem or switching editions.", Bounds = new Rectangle(26, 66, 590, 52) });
        RadioButton create = new RadioButton { Text = "Create a new library", Checked = true, Bounds = new Rectangle(26, 125, 260, 28) };
        existing.Text = "Use an existing library"; existing.Bounds = new Rectangle(310, 125, 285, 28); Controls.Add(create); Controls.Add(existing);
        location.Bounds = new Rectangle(26, 166, 470, 28); location.Text = previous ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DJStem Library"); Controls.Add(location);
        Button browse = new Button { Text = "Browse…", Bounds = new Rectangle(507, 164, 108, 30) };
        browse.Click += delegate { using (FolderBrowserDialog picker = new FolderBrowserDialog { Description = "Choose the folder that will contain your entire DJStem library.", SelectedPath = Directory.Exists(location.Text) ? location.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), ShowNewFolderButton = true }) if (picker.ShowDialog(this) == DialogResult.OK) location.Text = picker.SelectedPath; };
        Controls.Add(browse);
        Button start = new Button { Text = "Continue", Bounds = new Rectangle(388, 235, 108, 34), BackColor = Color.FromArgb(244,155,191), FlatStyle = FlatStyle.Flat };
        Button cancel = new Button { Text = "Cancel", Bounds = new Rectangle(507, 235, 108, 34), DialogResult = DialogResult.Cancel }; Controls.Add(start); Controls.Add(cancel); AcceptButton = start; CancelButton = cancel;
        start.Click += delegate {
            try {
                string selected = Path.GetFullPath(location.Text.Trim());
                bool recognized = File.Exists(Path.Combine(selected, "djstem-library.json")) || (Directory.Exists(Path.Combine(selected, "mp3")) && Directory.Exists(Path.Combine(selected, "stems")));
                if (existing.Checked && !recognized) throw new Exception("Choose a DJStem library containing its mp3 and stems folders.");
                if (!existing.Checked && Directory.Exists(selected) && Directory.GetFileSystemEntries(selected).Length > 0) throw new Exception("This folder already contains files. Select Use an existing library, or choose a new empty folder.");
                Directory.CreateDirectory(selected); string probe = Path.Combine(selected, ".djstem-write-" + Guid.NewGuid().ToString("N")); File.WriteAllText(probe, ""); File.Delete(probe);
                Library = selected; DialogResult = DialogResult.OK;
            } catch (Exception error) { MessageBox.Show(this,error.Message,"Library folder",MessageBoxButtons.OK,MessageBoxIcon.Information); }
        };
    }
}
