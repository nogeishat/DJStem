using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("DJStem Setup")]
[assembly: AssemblyProduct("DJStem")]
[assembly: AssemblyVersion("1.0.2.0")]
[assembly: AssemblyFileVersion("1.0.2.0")]
internal sealed class Installer : Form
{
    private readonly string source = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "app");
    private readonly TextBox destination = new TextBox();
    private readonly CheckBox shortcut = new CheckBox(), launch = new CheckBox();
    private readonly ProgressBar progress = new ProgressBar();
    private readonly Label status = new Label();
    private readonly Button install = new Button(), browse = new Button();
    private bool busy;
    private readonly string edition;
    internal Installer()
    {
        string build = File.ReadAllText(Path.Combine(source,"release-build.json")); edition = build.Contains("nvidia") ? "NVIDIA" : "CPU";
        Text = "DJStem 1.0.2 Setup — " + edition; ClientSize = new Size(640, 362); FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterScreen; MaximizeBox = MinimizeBox = false; Font = new Font("Segoe UI", 10); Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Controls.Add(new Label { Text = "Install DJStem 1.0.2", Font = new Font("Segoe UI", 17, FontStyle.Bold), Bounds = new Rectangle(24,18,590,40) });
        Controls.Add(new Label { Text = edition == "NVIDIA" ? "NVIDIA edition · GPU or CPU stem separation" : "CPU edition · CPU stem separation", Bounds = new Rectangle(26,65,590,27) });
        Controls.Add(new Label { Text = "All required runtimes and separation models are included.\nYou will choose a separate folder for your music and settings on first launch.", Bounds = new Rectangle(26,96,590,48) });
        destination.Bounds = new Rectangle(26,154,470,28); destination.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","DJStem-"+edition); Controls.Add(destination);
        browse.Text = "Browse…"; browse.Bounds = new Rectangle(507,152,108,30); browse.Click += delegate { using (FolderBrowserDialog picker = new FolderBrowserDialog { Description = "Choose where to install DJStem.", ShowNewFolderButton = true }) if (picker.ShowDialog(this) == DialogResult.OK) destination.Text = picker.SelectedPath; }; Controls.Add(browse);
        shortcut.Text = "Create a desktop shortcut"; shortcut.Checked = true; shortcut.Bounds = new Rectangle(26,198,550,27); Controls.Add(shortcut);
        launch.Text = "Open DJStem after installation"; launch.Checked = true; launch.Bounds = new Rectangle(26,230,550,27); Controls.Add(launch);
        progress.Bounds = new Rectangle(26,273,589,15); Controls.Add(progress); status.Bounds = new Rectangle(26,304,430,40); Controls.Add(status);
        install.Text = "Install"; install.Bounds = new Rectangle(507,307,108,34); install.BackColor = Color.FromArgb(244,155,191); install.FlatStyle = FlatStyle.Flat; Controls.Add(install); AcceptButton = install;
        FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
        install.Click += async delegate {
            if (busy) return; busy = true; install.Enabled = browse.Enabled = destination.Enabled = shortcut.Enabled = launch.Enabled = false;
            try {
                string target = Path.GetFullPath(destination.Text.Trim()); bool addShortcut = shortcut.Checked;
                var report = new Progress<int>(value => { progress.Value = value; status.Text = "Installing… " + value + "%"; });
                await Task.Run(() => CopyApplication(target, addShortcut, report));
                busy = false; status.Text = "Installation complete.";
                if (launch.Checked) Process.Start(new ProcessStartInfo(Path.Combine(target,"DJStem.exe")) { WorkingDirectory = target, UseShellExecute = true });
                Close();
            } catch (Exception error) { busy = false; install.Enabled = browse.Enabled = destination.Enabled = shortcut.Enabled = launch.Enabled = true; status.Text = "Installation stopped."; MessageBox.Show(this,error.Message,"DJStem Setup",MessageBoxButtons.OK,MessageBoxIcon.Error); }
        };
    }
    internal void CopyApplication(string target, bool addShortcut, IProgress<int> report)
    {
        target = Path.GetFullPath(target);
        if (target.TrimEnd('\\').Equals(Path.GetPathRoot(target).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)) throw new Exception("Choose an application folder rather than a drive root.");
        if (File.Exists(Path.Combine(target,"djstem-library.json")) || Directory.Exists(Path.Combine(target,"mp3")) || Directory.Exists(Path.Combine(target,"stems"))) throw new Exception("Choose an application folder separate from your music library.");
        foreach (Process process in Process.GetProcesses()) {
            try { string exe = process.MainModule.FileName; if (exe.StartsWith(target.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)) throw new IOException("Close the installed DJStem windows before updating this folder."); }
            catch (IOException) { throw; } catch { }
        }
        var files = Directory.GetFiles(source,"*",SearchOption.AllDirectories); long total = files.Sum(file => new FileInfo(file).Length), done = 0;
        Directory.CreateDirectory(target);
        foreach (string file in files) {
            string relative = file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar), output = Path.GetFullPath(Path.Combine(target,relative));
            if (!output.StartsWith(target.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)) throw new Exception("Invalid installer file path.");
            Directory.CreateDirectory(Path.GetDirectoryName(output)); string temporary = output + ".installing";
            File.Copy(file,temporary,true); if (File.Exists(output)) File.Delete(output); File.Move(temporary,output);
            done += new FileInfo(file).Length; if (report != null) report.Report((int)(done*100/Math.Max(1,total)));
        }
        if (addShortcut) {
            MakeShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"DJStem "+edition+".lnk"),target,"");
            string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"DJStem "+edition); Directory.CreateDirectory(menu);
            MakeShortcut(Path.Combine(menu,"DJStem.lnk"),target,""); MakeShortcut(Path.Combine(menu,"Add songs.lnk"),target,"--downloader"); MakeShortcut(Path.Combine(menu,"Choose library folder.lnk"),target,"--choose-library");
        }
    }
    private static void MakeShortcut(string path,string root,string arguments)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell"); object shell = Activator.CreateInstance(shellType); object link = shellType.InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{path}); Type type = link.GetType();
        foreach (var pair in new[] { new[] {"TargetPath",Path.Combine(root,"DJStem.exe")},new[] {"WorkingDirectory",root},new[] {"Arguments",arguments},new[] {"IconLocation",Path.Combine(root,"DJStem.exe")+",0"},new[] {"Description","DJStem 1.0.2"} }) type.InvokeMember(pair[0],BindingFlags.SetProperty,null,link,new object[]{pair[1]});
        type.InvokeMember("Save",BindingFlags.InvokeMethod,null,link,null);
        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
    }
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        using (Installer setup = new Installer()) {
            int index = Array.IndexOf(args,"--extract-to");
            if (index >= 0 && index + 1 < args.Length) { try { setup.CopyApplication(args[index+1],false,null); return 0; } catch (Exception error) { File.WriteAllText(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath),"setup-error.log"),error.ToString()); return 1; } }
            Application.Run(setup); return 0;
        }
    }
}
