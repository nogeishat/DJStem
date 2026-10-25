using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed class DropBox : Form
{
    private readonly string endpoint;
    private readonly string downloads;
    private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
    private readonly Label title = new Label();
    private readonly Label status = new Label();
    private readonly ToolTip tip = new ToolTip();
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    private readonly ToolStripMenuItem pauseItem = new ToolStripMenuItem("Pause queue");
    private bool polling;
    private bool paused;
    private DateTime noticeUntil;
    private readonly Color paper = Color.FromArgb(248, 250, 246);

    internal DropBox(string address, string directory)
    {
        endpoint = address.TrimEnd('/'); downloads = directory;
        Text = "DJStem";
        ClientSize = new Size(340, 112);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        ShowIcon = true; Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        MaximizeBox = false; MinimizeBox = true;
        StartPosition = FormStartPosition.Manual;
        Rectangle screen = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(screen.Right - Width - 32, screen.Bottom - Height - 32);
        TopMost = true; BackColor = paper; AllowDrop = true; KeyPreview = true;
        title.Text = "Drop YouTube or SoundCloud links";
        title.Font = new Font("Segoe UI", 12, FontStyle.Regular);
        title.ForeColor = Color.FromArgb(32, 60, 52);
        title.Bounds = new Rectangle(10, 25, 320, 30);
        title.TextAlign = ContentAlignment.MiddleCenter;
        status.Text = "MP3  ·  four stems  ·  cover";
        status.Font = new Font("Segoe UI", 9);
        status.ForeColor = Color.FromArgb(105, 120, 105);
        status.Bounds = new Rectangle(14, 60, 312, 34);
        status.TextAlign = ContentAlignment.TopCenter;
        status.AutoEllipsis = true;
        Controls.Add(title); Controls.Add(status);
        foreach (Control control in new Control[] { this, title, status })
        {
            control.AllowDrop = true;
            control.DragEnter += OnDragEnter;
            control.DragOver += OnDragEnter;
            control.DragLeave += delegate { BackColor = paper; };
            control.DragDrop += OnDrop;
            control.DoubleClick += delegate { OpenDownloads(); };
        }
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add("Open music player", null, delegate { OpenPlayer(); });
        menu.Items.Add("Open downloads", null, delegate { OpenDownloads(); });
        menu.Items.Add("Paste links", null, async delegate { await PasteLinks(); });
        menu.Items.Add("Retry failed processing", null, async delegate { await RetryFailed(); });
        pauseItem.Click += async delegate { try { await Post("/api/pause", new { paused = !paused }); await Poll(); } catch (Exception error) { ShowNotice(error.Message, true); } };
        menu.Items.Add(pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, delegate { Close(); });
        ContextMenuStrip = menu; title.ContextMenuStrip = menu; status.ContextMenuStrip = menu;
        KeyDown += async delegate(object sender, KeyEventArgs args) { if (args.Control && args.KeyCode == Keys.V) { args.SuppressKeyPress = true; await PasteLinks(); } };
        timer.Interval = 1000;
        timer.Tick += async delegate { await Poll(); };
        Shown += async delegate { timer.Start(); await Poll(); };
        FormClosed += delegate { timer.Stop(); };
        tip.SetToolTip(title, "Drag a YouTube video or SoundCloud track title, linked artwork, or browser address here. Right-click for options.");
    }

    private static string DataText(object value, Encoding encoding)
    {
        string text = value as string;
        if (text != null) return text;
        MemoryStream stream = value as MemoryStream;
        if (stream != null) return encoding.GetString(stream.ToArray()).TrimEnd('\0');
        byte[] bytes = value as byte[];
        return bytes == null ? "" : encoding.GetString(bytes).TrimEnd('\0');
    }

    internal static string ExtractDropText(IDataObject data)
    {
        List<string> parts = new List<string>();
        foreach (string format in new string[] { "UniformResourceLocatorW", "UniformResourceLocator", DataFormats.UnicodeText, DataFormats.Text, "text/uri-list" })
        {
            if (data.GetDataPresent(format)) parts.Add(DataText(data.GetData(format), format == "UniformResourceLocatorW" ? Encoding.Unicode : Encoding.UTF8));
        }
        if (data.GetDataPresent(DataFormats.Html))
        {
            string html = DataText(data.GetData(DataFormats.Html), Encoding.UTF8);
            foreach (Match match in Regex.Matches(html, "href\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase)) parts.Add(WebUtility.HtmlDecode(match.Groups[1].Value));
        }
        // A browser may advertise the same URL in several clipboard formats.
        HashSet<string> urls = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(String.Join("\n", parts), "https?://[^\\s<>\"'\\x00]+", RegexOptions.IgnoreCase)) urls.Add(match.Value);
        return String.Join("\n", urls);
    }

    private void OnDragEnter(object sender, DragEventArgs args)
    {
        bool links = false;
        try { links = !String.IsNullOrWhiteSpace(ExtractDropText(args.Data)); } catch { }
        args.Effect = links ? DragDropEffects.Copy : DragDropEffects.None;
        BackColor = links ? Color.FromArgb(229, 241, 221) : paper;
    }

    private async void OnDrop(object sender, DragEventArgs args)
    {
        BackColor = paper;
        try { await AddLinks(ExtractDropText(args.Data)); }
        catch (Exception error) { ShowNotice(error.Message, true); }
    }

    private async Task PasteLinks()
    {
        try { await AddLinks(Clipboard.GetText()); }
        catch (Exception error) { ShowNotice(error.Message, true); }
    }

    private async Task AddLinks(string text)
    {
        if (String.IsNullOrWhiteSpace(text)) { ShowNotice("Drop a YouTube or SoundCloud link", true); return; }
        Dictionary<string, object> result = await Post("/api/links", new { text = text });
        int added = Convert.ToInt32(result["added"]); int skipped = Convert.ToInt32(result["skipped"]);
        ShowNotice(added + " queued" + (skipped > 0 ? "  ·  " + skipped + " repeats skipped" : ""), false);
    }

    private async Task<Dictionary<string, object>> GetState()
    {
        using (WebClient client = new WebClient())
        {
            client.Encoding = Encoding.UTF8;
            return (Dictionary<string, object>)json.DeserializeObject(await client.DownloadStringTaskAsync(endpoint + "/api/state"));
        }
    }

    private async Task<Dictionary<string, object>> Post(string route, object body)
    {
        using (WebClient client = new WebClient())
        {
            client.Encoding = Encoding.UTF8;
            client.Headers[HttpRequestHeader.ContentType] = "application/json";
            try { return (Dictionary<string, object>)json.DeserializeObject(await client.UploadStringTaskAsync(endpoint + route, "POST", json.Serialize(body))); }
            catch (WebException error)
            {
                if (error.Response != null)
                {
                    using (StreamReader reader = new StreamReader(error.Response.GetResponseStream()))
                    {
                        var response = (Dictionary<string, object>)json.DeserializeObject(reader.ReadToEnd());
                        object detail;
                        if (response.TryGetValue("error", out detail)) throw new Exception(Convert.ToString(detail));
                    }
                }
                throw;
            }
        }
    }

    private async Task Poll()
    {
        if (polling || IsDisposed) return;
        polling = true;
        try
        {
            Dictionary<string, object> state = await GetState();
            if (IsDisposed) return;
            paused = Convert.ToBoolean(state["paused"]);
            pauseItem.Text = paused ? "Resume queue" : "Pause queue";
            if (DateTime.UtcNow < noticeUntil) return;
            Dictionary<string, object> active = null, failed = null;
            int queued = 0;
            foreach (object item in (object[])state["jobs"])
            {
                var job = (Dictionary<string, object>)item;
                string outcome = Convert.ToString(job["status"]);
                if (outcome == "downloading" || outcome == "separating" || outcome == "analyzing") active = job;
                if (outcome == "queued") queued++;
                if (outcome == "failed" && failed == null) failed = job;
            }
            status.ForeColor = Color.FromArgb(105, 120, 105);
            if (active != null)
            {
                string song = Convert.ToString(active["title"]);
                string detail = Convert.ToString(active["message"]);
                string phase = Convert.ToString(active["status"]);
                status.Text = (phase == "separating" ? "Stems: " : phase == "analyzing" ? "BPM / key: " : "") + (String.IsNullOrEmpty(song) ? detail : song) + (phase == "analyzing" ? "…" : "  ·  " + active["progress"] + "%");
                tip.SetToolTip(status, detail + (queued > 0 ? "\n" + queued + " waiting" : ""));
            }
            else if (paused) status.Text = "Queue paused  ·  right-click to resume";
            else if (queued > 0) status.Text = queued + " links waiting";
            else if (failed != null)
            {
                status.Text = "Processing failed  ·  right-click to retry";
                status.ForeColor = Color.FromArgb(165, 70, 45);
                tip.SetToolTip(status, Convert.ToString(failed["message"]));
            }
            else { status.Text = "MP3  ·  four stems  ·  cover"; tip.SetToolTip(status, downloads); }
        }
        catch { if (!IsDisposed) { status.Text = "Cannot reach downloader"; tip.SetToolTip(status, "Restart StemKit. Your saved links will resume."); } }
        finally { polling = false; }
    }

    private async Task RetryFailed()
    {
        try
        {
            Dictionary<string, object> state = await GetState(); int count = 0;
            foreach (object item in (object[])state["jobs"])
            {
                var job = (Dictionary<string, object>)item;
                if (Convert.ToString(job["status"]) != "failed") continue;
                await Post("/api/jobs/" + job["id"] + "/retry", new { }); count++;
            }
            ShowNotice(count + " songs queued to retry", false);
        }
        catch (Exception error) { ShowNotice(error.Message, true); }
    }

    private void ShowNotice(string text, bool error)
    {
        if (IsDisposed) return;
        status.Text = text; status.ForeColor = error ? Color.FromArgb(165, 70, 45) : Color.FromArgb(70, 115, 64);
        tip.SetToolTip(status, text); noticeUntil = DateTime.UtcNow.AddSeconds(5);
    }

    private void OpenDownloads()
    {
        try { Directory.CreateDirectory(downloads); Process.Start("explorer.exe", "\"" + downloads + "\""); }
        catch (Exception error) { ShowNotice(error.Message, true); }
    }

    private void OpenPlayer()
    {
        try
        {
            string player = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "StemKitPlayer.exe");
            if (File.Exists(player)) Process.Start(player, "\"" + downloads + "\"");
            else
            {
                string launcher = Path.Combine(Directory.GetParent(Path.GetDirectoryName(Application.ExecutablePath)).FullName, "Start-Player.vbs");
                Process.Start("wscript.exe", "\"" + launcher + "\"");
            }
        }
        catch (Exception error) { ShowNotice(error.Message, true); }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length >= 2 && args[0] == "--error") { MessageBox.Show(args[1], "StemKit", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
        if (args.Length < 2)
        {
            string launcher = Path.Combine(Directory.GetParent(Path.GetDirectoryName(Application.ExecutablePath)).FullName, "Start-StemKit.vbs");
            Process.Start("wscript.exe", "\"" + launcher + "\""); return 0;
        }
        bool fresh;
        using (Mutex mutex = new Mutex(true, "Local\\StemKitDesktopDropBox", out fresh))
        {
            if (!fresh) return 0;
            Application.Run(new DropBox(args[0], args[1]));
        }
        return 0;
    }
}
