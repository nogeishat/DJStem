using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using NAudio.Wave;

internal static class PlayerBrowserTests
{
    private static int checks;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    private static object Invoke(object instance, string name, params object[] arguments)
    {
        return instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(instance, arguments);
    }
    private static void Set(object instance, string name, object value) { instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(instance, value); }
    private static object Get(object instance, string name) { return instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance); }
    private sealed class PreviewHost : Form { protected override bool ShowWithoutActivation { get { return true; } } }
    private static void SavePreview(Control control, string file)
    {
        Control parent = control.Parent; DockStyle dock = control.Dock; Rectangle bounds = control.Bounds; bool visible = control.Visible;
        using (var host = new PreviewHost { ShowInTaskbar = false, Opacity = 0, FormBorderStyle = FormBorderStyle.None, ClientSize = control.Size })
        {
            host.Controls.Add(control); control.Dock = DockStyle.Fill; control.Visible = true; host.Show(); Application.DoEvents();
            using (var bitmap = new Bitmap(control.Width, control.Height)) { control.DrawToBitmap(bitmap, control.ClientRectangle); bitmap.Save(file); }
            host.Controls.Remove(control); parent.Controls.Add(control); control.Dock = dock; control.Bounds = bounds; control.Visible = visible;
        }
    }
    private static void Finish(Task task)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!task.IsCompleted && clock.ElapsedMilliseconds < 30000) { Application.DoEvents(); Thread.Sleep(10); }
        Check(task.IsCompleted, "Playback preparation must complete without blocking the UI thread"); task.GetAwaiter().GetResult();
    }
    private static void CheckCacheSettings(string root)
    {
        string project = Path.Combine(root, "cache-settings"), cache = Path.Combine(project, "cache");
        Directory.CreateDirectory(cache);
        string config = Path.Combine(project, "settings.json");
        File.WriteAllText(config, new JavaScriptSerializer().Serialize(new { matchedStemsRoot = "cache", libraryRoot = root }));
        string[] files = Enumerable.Range(0, 3).Select(index => Path.Combine(cache, new string((char)('a' + index), 64) + ".wav")).ToArray();
        for (int index = 0; index < files.Length; index++)
        {
            using (var writer = new FileStream(files[index], FileMode.Create, FileAccess.Write)) writer.SetLength(40000000);
            File.SetLastAccessTimeUtc(files[index], DateTime.UtcNow.AddDays(index - 3));
        }
        using (PlayerForm form = new PlayerForm(root, project))
        {
            object[] arguments = { null };
            using (Form dialog = (Form)Invoke(form, "CreatePlayerSettingsDialog", arguments))
            {
                var limit = (NumericUpDown)dialog.Controls.Find("MatchedStemsMaxGb", true).Single();
                Check(limit.Value == 30 && limit.Minimum == .1m && limit.DecimalPlaces == 1, "Settings must show the default matched-stems limit in editable GB");
                limit.Value = .1m;
                Check(PlayerMatchedCache.LimitBytes(project) == 30000000000L && files.All(File.Exists), "Canceled cache settings must leave both the saved budget and cached files unchanged");
            }
            using (Form dialog = (Form)Invoke(form, "CreatePlayerSettingsDialog", arguments))
            {
                var limit = (NumericUpDown)dialog.Controls.Find("MatchedStemsMaxGb", true).Single(); limit.Value = .1m;
                var body = (Panel)dialog.Controls.Find("SettingsContent", true).Single();
                dialog.ShowInTaskbar = false; dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-32000, -32000); dialog.Opacity = 0;
                using (var click = new System.Windows.Forms.Timer { Interval = 20 })
                {
                    click.Tick += delegate { click.Stop(); ((Button)dialog.AcceptButton).PerformClick(); };
                    click.Start();
                    Check(dialog.ShowDialog() == DialogResult.OK, "Saving the cache setting must finish cleanup before accepting the dialog");
                }
                Check(PlayerMatchedCache.LimitBytes(project) == 100000000 && !File.Exists(files[0]) && File.Exists(files[1]) && File.Exists(files[2]), "The Settings Save button must persist the selected size and immediately remove only the needed oldest cache files");
                Check(body.Bottom <= ((Button)dialog.AcceptButton).Parent.Top && body.VerticalScroll.Visible, "Long settings must scroll while Save and Cancel remain visible");
            }
        }
        using (PlayerForm reopened = new PlayerForm(root, project))
        {
            object[] arguments = { null };
            using (Form dialog = (Form)Invoke(reopened, "CreatePlayerSettingsDialog", arguments))
            {
                Check(((NumericUpDown)dialog.Controls.Find("MatchedStemsMaxGb", true).Single()).Value == .1m, "Reopening the player must restore the chosen cache limit");
                dialog.ShowInTaskbar = false; dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-32000, -32000); dialog.Opacity = 0; dialog.Show(); Application.DoEvents();
                string preview = Path.Combine(Directory.GetCurrentDirectory(), "data", "matched-stems-settings-preview.png"); Directory.CreateDirectory(Path.GetDirectoryName(preview));
                using (var bitmap = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size)); bitmap.Save(preview); }
                var body = (Panel)dialog.Controls.Find("SettingsContent", true).Single();
                body.ScrollControlIntoView(body.Controls.OfType<GroupBox>().Single(control => control.Text == "Song tuning (A4 reference)")); Application.DoEvents();
                using (var bitmap = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size)); bitmap.Save(Path.Combine(Path.GetDirectoryName(preview), "matched-stems-settings-scrolled-preview.png")); }
            }
        }
    }
    private static void Tone(string file, double frequency)
    {
        using (WaveFileWriter writer = new WaveFileWriter(file, new WaveFormat(22050, 24, 2)))
        for (int frame = 0; frame < 44100; frame++) { float sample = (float)(.1 * Math.Sin(2 * Math.PI * frequency * frame / 22050)); writer.WriteSample(sample); writer.WriteSample(sample); }
    }
    private static void CheckPlaybackTuning(string root)
    {
        string originalFolder = Path.Combine(root, "original"), donorFolder = Path.Combine(root, "donor"); Directory.CreateDirectory(originalFolder); Directory.CreateDirectory(donorFolder);
        string audio = Path.Combine(root, "audio.wav"); Tone(audio, 220);
        foreach (string stem in StemMixer.StemNames) { Tone(Path.Combine(originalFolder, stem + ".wav"), 220); Tone(Path.Combine(donorFolder, stem + ".wav"), 261.626); }
        SongInfo original = new SongInfo { Audio = audio, Folder = originalFolder, Cover = Path.Combine(root, "no-cover.jpg"), Title = "Original", Artist = "Artist", HasStems = true };
        SongInfo donor = new SongInfo { Audio = audio + ".donor", Folder = donorFolder, Cover = Path.Combine(root, "no-donor-cover.jpg"), Title = "Donor", Artist = "Other", HasStems = true };
        MusicTraits originalTraits = new MusicTraits { Bpm = 120, Tonic = 9, Minor = true }, donorTraits = new MusicTraits { Bpm = 80, Tonic = 0, Minor = true };
        byte[] savedOriginal = File.ReadAllBytes(Path.Combine(originalFolder, "vocals.wav"));
        using (PlayerForm form = new PlayerForm(root, root))
        {
            IntPtr handle = form.Handle;
            StemMixer native = new StemMixer(audio, originalFolder); native.Seek(.5);
            var bank = new StemSourceBank { Song = donor, Traits = donorTraits, Plan = StemMatchPlan.Create(originalTraits, donorTraits), Files = new string[4], Peaks = new float[4][] };
            for (int lane = 0; lane < 4; lane++) { bank.Files[lane] = Path.Combine(donorFolder, StemMixer.StemNames[lane] + ".wav"); bank.Peaks[lane] = new float[1600]; }
            var sources = new StemSourceState(originalFolder); sources.Stage(bank, 0); native.ReplaceStem(0, bank.Files[0]);
            Set(form, "current", original); Set(form, "currentNativeTraits", originalTraits); Set(form, "nativeFrames", native.TotalFrames); Set(form, "nativeRate", native.WaveFormat.SampleRate); Set(form, "mixer", native); Set(form, "stemSources", sources); Set(form, "displayedAddedSong", donor);
            var shifts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { { original.Audio, 2 }, { donor.Audio, -1 } };
            try
            {
                Finish((Task)Invoke(form, "ApplyTuning", (double?)150, shifts));
                StemMixer tuned = (StemMixer)Get(form, "mixer"); sources = (StemSourceState)Get(form, "stemSources");
                Check((double?)Get(form, "sharedBpm") == 150 && tuned.TotalFrames == 35280, "Shared BPM must change the original playback duration to the chosen tempo");
                Check(Math.Abs(tuned.Seconds - .4) < .002, "Changing BPM must preserve the current musical position");
                Check(sources.Available.Plan.MatchedBpm == 150 && sources.Available.Plan.Tempo == 1.875, "The added song must play at exactly the same selected BPM");
                Check(sources.Active[0] == sources.Available && sources.Active[1] == null, "Tempo and key changes must preserve each lane's original/replacement selection");
                Check(Math.Abs(sources.Available.Plan.Pitch - Camelot.Pitch(-1)) < .000001 && (int)Invoke(form, "KeyShift", original) == 2, "Independent song key choices must both reach playback");
                for (int lane = 0; lane < 4; lane++)
                {
                    using (var reader = PlaybackTransform.OpenFile(sources.OriginalFiles[lane], sources.OriginalTuning[lane])) Check(reader.Length / reader.WaveFormat.BlockAlign == tuned.TotalFrames, "Every transformed original stem must share the same exact virtual timeline");
                    using (var reader = PlaybackTransform.OpenFile(sources.Available.Files[lane], sources.Available.Tuning[lane])) Check(reader.Length / reader.WaveFormat.BlockAlign >= tuned.TotalFrames, "Every transformed donor must cover the transport and retain its full tail");
                }
                string displayedBpm = ((GlassButton)Get(form, "currentBpm")).Text;
                Check(displayedBpm.StartsWith("150") && displayedBpm.EndsWith(" BPM") && displayedBpm == ((GlassButton)Get(form, "addedBpm")).Text, "Both song headers must display the shared playback BPM");
                Check(((GlassButton)Get(form, "currentKey")).Text.Contains("B minor") && ((GlassButton)Get(form, "addedKey")).Text.Contains("B minor"), "Both headers must display their independently transposed keys");
                Task canceled = (Task)Invoke(form, "ApplyTuning", (double?)135, new Dictionary<string, int>());
                Invoke(form, "CancelTuning"); Finish(canceled);
                Check(Object.ReferenceEquals(tuned, Get(form, "mixer")) && (double?)Get(form, "sharedBpm") == 150 && (int)Invoke(form, "KeyShift", original) == 2, "Canceling preparation must preserve the existing mixer, BPM, and keys");
                Check(((GlassButton)Get(form, "currentBpm")).Enabled && !(bool)Get(form, "editingTuning"), "Canceling preparation must reenable the editing controls");
                Finish((Task)Invoke(form, "ApplyTuning", null, shifts));
                tuned = (StemMixer)Get(form, "mixer"); sources = (StemSourceState)Get(form, "stemSources");
                Check(Get(form, "sharedBpm") == null && tuned.TotalFrames == 44100, "Restoring original BPM must restore the original duration");
                Check((int)Invoke(form, "KeyShift", original) == 2 && sources.Available.Plan.Pitch == Camelot.Pitch(-1), "Restoring tempo must retain the chosen keys");
                Invoke(form, "RestoreStems");
                Check(((StemSourceState)Get(form, "stemSources")).Available == null && (int)Invoke(form, "KeyShift", original) == 2, "Reset stems must retain the original song's selected pitch");
                Finish((Task)Invoke(form, "ApplyTuning", (double?)100, new Dictionary<string, int>()));
                Check((double?)Get(form, "sharedBpm") == 100 && ((StemMixer)Get(form, "mixer")).TotalFrames == 52920, "The same tempo slider must also work with only one song loaded");
                Check(File.ReadAllBytes(Path.Combine(originalFolder, "vocals.wav")).SequenceEqual(savedOriginal), "Playback edits must preserve every original source byte");
                Invoke(form, "CloseAudio"); Invoke(form, "ResetTuning");
                StemMixer standalone = new StemMixer(audio, "");
                Set(form, "current", original); Set(form, "currentNativeTraits", originalTraits); Set(form, "nativeFrames", standalone.TotalFrames); Set(form, "nativeRate", standalone.WaveFormat.SampleRate); Set(form, "mixer", standalone); Set(form, "stemSources", new StemSourceState(Path.Combine(root, "incomplete"))); Set(form, "displayedAddedSong", null);
                // The song folder may exist while stems are still unavailable;
                // tuning must use the MP3/audio mixer capability, not the folder.
                original.HasStems = false;
                Finish((Task)Invoke(form, "ApplyTuning", (double?)150, new Dictionary<string, int> { { original.Audio, 3 } }));
                Check((double?)Get(form, "sharedBpm") == 150 && !((StemMixer)Get(form, "mixer")).HasStems && ((StemMixer)Get(form, "mixer")).OriginalMode, "BPM and key edits must work in original audio mode before stems are available");
                Check(((StemMixer)Get(form, "mixer")).TotalFrames == 35280 && (int)Invoke(form, "KeyShift", original) == 3, "Standalone audio tuning must apply both tempo and key changes");
                originalTraits = originalTraits.Copy(); originalTraits.Bpm = 0; Set(form, "currentNativeTraits", originalTraits);
                Finish((Task)Invoke(form, "ApplyTuning", null, new Dictionary<string, int> { { original.Audio, 2 } }));
                Check(((StemMixer)Get(form, "mixer")).TotalFrames == 44100 && ((GlassButton)Get(form, "currentBpm")).Text == "BPM unknown" && ((GlassButton)Get(form, "currentKey")).Text.Contains("B minor"), "A known key must remain editable when the song's BPM is unknown");
            }
            finally { Invoke(form, "CancelTuning"); Invoke(form, "CloseAudio"); }
        }
    }
    private static void CheckLibraryBpmFilters(string root)
    {
        root = Path.Combine(root, "bpm-filter-tests"); Directory.CreateDirectory(root);
        Func<string, string, double?, SongInfo> song = (folder, title, bpm) => new SongInfo { Audio = Path.Combine(root, "mp3", folder, title + ".mp3"), Folder = Path.Combine(root, "stems", folder, title), Cover = Path.Combine(root, "missing.jpg"), Artist = "Displayed artist", Title = title, Bpm = bpm };
        SongInfo unknown = song("Folder A", "Unknown", null), fast = song("Folder B", "Fast", 180), zeta = song("Folder A", "Zeta", 120), slow = song("Folder B", "Slow", 85.5), low = song("Folder A", "Low", 100), alpha = song("Folder B", "Alpha", 120);
        var original = new List<SongInfo> { unknown, fast, zeta, slow, low, alpha };
        using (var form = new PlayerForm(root, root))
        {
            IntPtr handle = form.Handle; Set(form, "songs", original); Set(form, "current", low); Invoke(form, "FollowSessionSong", low);
            var queue = (PlaybackQueue)Get(form, "songQueue"); string[] ids = queue.Items.Select(entry => entry.Id).ToArray();
            var sort = (ComboBox)Get(form, "libraryBpmSort"); var range = (CheckBox)Get(form, "libraryBpmRange");
            var minimum = (NumericUpDown)Get(form, "libraryBpmFrom"); var maximum = (NumericUpDown)Get(form, "libraryBpmTo");
            var search = (TextBox)Get(form, "librarySearch"); var grid = (FlowLayoutPanel)Get(form, "libraryGrid");
            Func<SongInfo[]> cards = () => grid.Controls.OfType<LibraryCard>().Where(card => !card.Folder).Select(card => (SongInfo)card.Tag).ToArray();
            Invoke(form, "RenderBrowser", true); Check(cards().SequenceEqual(original), "Default library order must retain the library's existing order");
            sort.SelectedIndex = 1;
            Check(cards().SequenceEqual(new[] { slow, low, alpha, zeta, fast, unknown }), "Ascending BPM must sort numeric tempos with stable title ties and unknown BPM last");
            sort.SelectedIndex = 2;
            Check(cards().SequenceEqual(new[] { fast, alpha, zeta, low, slow, unknown }), "Descending BPM must also keep unknown BPM last");
            range.Checked = true; minimum.Value = 100; maximum.Value = 120;
            Check(cards().SequenceEqual(new[] { alpha, zeta, low }), "The selected BPM range must include both endpoints and exclude unknown BPM");
            Check(((Label)Get(form, "libraryCount")).Text.StartsWith("3 songs"), "Library counts must follow the filtered cards");
            sort.SelectedIndex = 1; search.Text = "Zeta";
            Check(cards().SequenceEqual(new[] { zeta }), "Search must combine with sorting and BPM range filtering");
            search.Text = "Folder B"; Check(cards().SequenceEqual(new[] { alpha }), "Folder-name search must still combine with the BPM filter");
            search.Text = ""; Set(form, "artistsView", true); Set(form, "artistFolder", null); Invoke(form, "RenderBrowser", true);
            Check(grid.Controls.OfType<LibraryCard>().All(card => card.Folder) && grid.Controls.Count == 2 && !sort.Enabled, "Artist folders must show only groups with matching songs and reserve BPM sorting for song views");
            LibraryCard artist = grid.Controls.OfType<LibraryCard>().First(card => (string)card.Tag == "Folder A"); Invoke(artist, "OnClick", EventArgs.Empty);
            Check(cards().SequenceEqual(new[] { low, zeta }) && sort.Enabled, "Opening an artist folder must retain the BPM range and sort that folder's song cards");
            sort.SelectedIndex = 2; Check(cards().SequenceEqual(new[] { zeta, low }), "Descending BPM must work inside a selected artist folder");
            maximum.Value = 85.5m;
            Check(minimum.Value == 85.5m && cards().Length == 0, "A lower upper bound must move the lower bound with it and show an empty result when the artist has no match");
            Set(form, "artistFolder", null); Set(form, "artistsView", false); Invoke(form, "RenderBrowser", true);
            Check(cards().SequenceEqual(new[] { slow }), "Fractional BPM ranges must compare actual tempos inclusively");
            minimum.Value = 170;
            Check(maximum.Value == 170 && cards().Length == 0, "Raising the lower bound past the upper bound must keep the selected interval valid");
            var bar = (Control)Get(form, "libraryBpmBar"); var reset = bar.Controls.OfType<GlassButton>().Single(button => button.Text == "Reset"); Invoke(reset, "OnClick", EventArgs.Empty);
            Check(!range.Checked && sort.SelectedIndex == 0 && minimum.Value == 30 && maximum.Value == 300 && cards().SequenceEqual(original), "Reset must restore the full library and original order");
            Check(Object.ReferenceEquals(original, Get(form, "songs")) && original.SequenceEqual(new[] { unknown, fast, zeta, slow, low, alpha }) && Get(form, "current") == low && queue.Items.Select(entry => entry.Id).SequenceEqual(ids), "BPM controls must leave playback order, the current song, and session history intact");
            Control browser = (Control)Get(form, "browser"); browser.Size = new Size(1120, 740); Invoke(form, "LayoutBrowser");
            Check(bar.Bounds.Right <= browser.Width - 24 && bar.Controls.Cast<Control>().All(control => bar.ClientRectangle.Contains(control.Bounds)) && grid.Top > bar.Bottom && grid.Bottom < ((Control)Get(form, "libraryFooter")).Top, "BPM controls, song cards, and the playback footer must fit the minimum browser size");
            var gridView = (GlassButton)Get(form, "libraryGridView"); var listView = (GlassButton)Get(form, "libraryListView");
            Check(gridView.Parent == bar && listView.Parent == bar && gridView.Left > reset.Right && listView.Right == bar.Width - 12 && gridView.Selected && !listView.Selected, "View buttons must sit on the right of the filter box with the active grid selected");
            Invoke(listView, "OnClick", EventArgs.Empty);
            Check(listView.Selected && !gridView.Selected && !grid.WrapContents && grid.FlowDirection == FlowDirection.TopDown && cards().SequenceEqual(original), "List view must keep song order and switch to a single vertical column");
            Check(grid.Controls.OfType<LibraryCard>().All(card => card.ListRow && card.Height == 72 && card.Width == grid.ClientSize.Width - 2), "List rows must be compact and fill the available browser width");
            IntPtr gridHandle = grid.Handle; grid.PerformLayout(); Invoke(grid, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, 40, 40, -120));
            Stopwatch scrollWait = Stopwatch.StartNew();
            while (grid.AutoScrollPosition.Y == 0 && scrollWait.ElapsedMilliseconds < 2000) { Application.DoEvents(); Thread.Sleep(5); }
            Check(grid.AutoScrollPosition.Y < 0, "The mouse wheel must scroll list rows beyond the visible area: display=" + grid.DisplayRectangle + ", client=" + grid.ClientRectangle + ", position=" + ((LibraryGridPanel)grid).ScrollOffset + ", target=" + ((LibraryGridPanel)grid).ScrollTarget);
            browser.Width = 1400; Invoke(form, "LayoutBrowser");
            Check(listView.Right == bar.Width - 12 && grid.Controls.OfType<LibraryCard>().All(card => card.Width == grid.ClientSize.Width - 2), "List rows and view buttons must resize with the library");
            sort.SelectedIndex = 1; range.Checked = true; minimum.Value = 100; maximum.Value = 120; search.Text = "Folder B";
            Check(cards().SequenceEqual(new[] { alpha }) && grid.Controls.OfType<LibraryCard>().All(card => card.ListRow), "List view must combine search, BPM sorting, and inclusive range filtering");
            search.Text = ""; Set(form, "artistsView", true); Invoke(form, "RenderBrowser", true);
            Func<Control, bool> locallyVisible = control => (bool)typeof(Control).GetMethod("GetState", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(control, new object[] { 2 });
            Check(grid.Controls.OfType<LibraryCard>().All(card => card.Folder && !card.ListRow && card.Size == new Size(200, 258)) && grid.Controls.Count == 2 && grid.WrapContents && !locallyVisible(gridView) && !locallyVisible(listView), "The artist-folder overview must always show a grid and hide both view buttons even when songs use list view");
            browser.Width = 1120; Invoke(form, "LayoutBrowser"); Invoke(gridView, "OnClick", EventArgs.Empty);
            Check(grid.Controls.OfType<LibraryCard>().All(card => card.Size == new Size(200, 258)) && (bool)Get(form, "libraryListMode"), "Resizing the artist overview and hidden view actions must retain folder-card sizes and the remembered song view");
            artist = grid.Controls.OfType<LibraryCard>().First(card => (string)card.Tag == "Folder A"); Invoke(artist, "OnKeyDown", new KeyEventArgs(Keys.Enter));
            Check(cards().SequenceEqual(new[] { low, zeta }) && grid.Controls.OfType<LibraryCard>().All(card => card.ListRow) && locallyVisible(gridView) && locallyVisible(listView), "Opening an artist must restore the song view buttons and remembered list view with the existing filters");
            Invoke(gridView, "OnClick", EventArgs.Empty);
            Check(gridView.Selected && !listView.Selected && grid.WrapContents && grid.FlowDirection == FlowDirection.LeftToRight && cards().SequenceEqual(new[] { low, zeta }) && grid.Controls.OfType<LibraryCard>().All(card => !card.ListRow && card.Size == new Size(200, 284)), "Returning to the grid must restore cover cards without resetting the folder or filters");
            Check(Get(form, "current") == low && queue.Items.Select(entry => entry.Id).SequenceEqual(ids), "Switching library views must preserve playback and session history");
            Set(form, "artistFolder", null); Set(form, "artistsView", false); search.Text = ""; browser.Size = new Size(1120, 740); Invoke(form, "LayoutBrowser"); Invoke(form, "RenderBrowser", true);
            string preview = Environment.GetEnvironmentVariable("STEMKIT_TEST_PREVIEW");
            if (!String.IsNullOrWhiteSpace(preview))
            {
                sort.SelectedIndex = 1; range.Checked = true; minimum.Value = 100; maximum.Value = 120;
                SavePreview(browser, preview);
            }
            string listPreview = Environment.GetEnvironmentVariable("STEMKIT_LIST_PREVIEW");
            if (!String.IsNullOrWhiteSpace(listPreview))
            {
                Invoke(listView, "OnClick", EventArgs.Empty); SavePreview(browser, listPreview);
            }
        }
    }
    private static void CheckSidebarRefreshScroll(string root)
    {
        string library = Path.Combine(root, "scroll-library"), artist = Path.Combine(library, "mp3", "Artist");
        Directory.CreateDirectory(artist);
        for (int i = 0; i < 40; i++) File.WriteAllText(Path.Combine(artist, "Song " + i.ToString("D2") + ".mp3"), "test");
        using (PlayerForm form = new PlayerForm(library, root))
        {
            IntPtr handle = form.Handle;
            var list = (SongList)Get(form, "list"); handle = list.Handle;
            Set(form, "current", PlayerLibrary.Scan(library)[0]);
            Finish((Task)Invoke(form, "RefreshLibrary"));
            list.TopIndex = 20;
            string anchor = ((SongInfo)list.Items[list.TopIndex]).Audio;
            object[] rows = list.Items.Cast<object>().ToArray();
            int selectionChanges = 0; list.SelectedIndexChanged += delegate { selectionChanges++; };
            for (int refresh = 0; refresh < 3; refresh++)
            {
                Finish((Task)Invoke(form, "RefreshLibrary"));
                Check(selectionChanges == 0 && list.Items.Cast<object>().SequenceEqual(rows),
                    "Unchanged refreshes must leave native rows and selection untouched to prevent flashing");
                Check(list.TopIndex == 20 && ((SongInfo)list.Items[list.TopIndex]).Audio == anchor,
                    "Repeated library refreshes must retain the sidebar viewport while the selected song is offscreen");
                Check(list.SelectedIndex == 0 && Get(form, "current") != null,
                    "Preserving the sidebar viewport must retain the selected and loaded song");
            }
            string stemFolder = Path.Combine(library, "stems", "Artist", "Song 25"); Directory.CreateDirectory(stemFolder);
            File.WriteAllText(Path.Combine(stemFolder, "metadata.json"), new JavaScriptSerializer().Serialize(new { track = "Song 25 updated", artist = "Artist", bpm = 123, key = "A minor" }));
            File.WriteAllText(Path.Combine(stemFolder, "separation.json"), "{}");
            foreach (string stem in StemMixer.StemNames) File.WriteAllText(Path.Combine(stemFolder, stem + ".wav"), "test");
            Finish((Task)Invoke(form, "RefreshLibrary"));
            var updated = list.Items.Cast<SongInfo>().Single(song => Path.GetFileName(song.Audio) == "Song 25.mp3");
            Check(updated.Title == "Song 25 updated" && updated.Bpm == 123 && updated.Key == "A minor" && updated.HasStems && list.TopIndex == 20,
                "Changed metadata and newly available stems must still update sidebar entries without moving the viewport");
            rows = list.Items.Cast<object>().ToArray(); selectionChanges = 0;
            Finish((Task)Invoke(form, "RefreshLibrary"));
            Check(selectionChanges == 0 && list.Items.Cast<object>().SequenceEqual(rows),
                "Polling must stop rebuilding again after metadata updates settle");
            var search = (TextBox)Get(form, "search"); search.Text = "updated";
            Check(list.Items.Count == 1 && ((SongInfo)list.Items[0]).Title == "Song 25 updated" && list.SelectedIndex == -1,
                "Search changes must still update rows when the underlying library is unchanged");
            rows = list.Items.Cast<object>().ToArray(); selectionChanges = 0;
            Finish((Task)Invoke(form, "RefreshLibrary"));
            Check(selectionChanges == 0 && list.Items.Cast<object>().SequenceEqual(rows),
                "Unchanged filtered refreshes must also leave native rows untouched");
            search.Text = ""; list.TopIndex = 20;
            SongInfo original = (SongInfo)Get(form, "current");
            Set(form, "current", list.Items[10]);
            Finish((Task)Invoke(form, "RefreshLibrary"));
            Check(list.SelectedIndex == 10 && list.TopIndex == 20,
                "A changed current song must still update the sidebar selection without moving the viewport");
            Set(form, "current", original);
            Finish((Task)Invoke(form, "RefreshLibrary"));
            File.WriteAllText(Path.Combine(artist, "000 new download.mp3"), "test");
            Finish((Task)Invoke(form, "RefreshLibrary"));
            Check(list.TopIndex == 21 && ((SongInfo)list.Items[list.TopIndex]).Audio == anchor && list.SelectedIndex == 1,
                "A download inserted above the viewport must keep the same top song and current selection");
            File.Delete(anchor);
            Finish((Task)Invoke(form, "RefreshLibrary"));
            Check(list.TopIndex == 21, "Removing the top visible song must retain the nearest surviving scroll position");
            foreach (string file in Directory.GetFiles(artist).Where(file => Path.GetFileName(file) != "Song 00.mp3")) File.Delete(file);
            Finish((Task)Invoke(form, "RefreshLibrary"));
            Check(list.Items.Count == 1 && list.TopIndex == 0 && list.SelectedIndex == 0,
                "A shrinking library must clamp its viewport to a valid row");
            File.Delete(Path.Combine(artist, "Song 00.mp3"));
            Finish((Task)Invoke(form, "RefreshLibrary"));
            Check(list.Items.Count == 0 && list.SelectedIndex == -1, "Refreshing an empty sidebar must clear its selection safely");
        }
    }
    [STAThread]
    private static int Main(string[] arguments)
    {
        string root = Path.Combine(Path.GetTempPath(), "stemkit-browser-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            if (arguments.Contains("--sidebar-scroll"))
            {
                CheckSidebarRefreshScroll(root);
                Console.WriteLine("Sidebar refresh scrolling: " + checks + " checks passed."); return 0;
            }
            using (CamelotWheel wheel = new CamelotWheel { Current = 7, Original = 6, Other = 19, MinorMode = true })
            using (Bitmap render = new Bitmap(570, 570))
            {
                for (int section = 0; section < 24; section++) Check(wheel.SectionAt(wheel.SectionPoint(section)) == section, "Every Camelot section must map its displayed label to the correct click target");
                Check(wheel.SectionAt(new Point(285, 285)) == -1 && wheel.SectionAt(new Point(0, 0)) == -1, "The wheel center and corners must not select a key");
                Check(wheel.Highlights(7) == 1 && wheel.Highlights(6) == 2 && wheel.Highlights(19) == 4, "Current, original, and other song keys must have distinct fills");
                wheel.Other = 7; Check(wheel.Highlights(7) == 5, "Overlapping song keys must show both highlight fills");
                int selected = -1; wheel.Selected = delegate(int section) { selected = section; };
                Point point = wheel.SectionPoint(8); Invoke(wheel, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));
                Check(selected == 8, "Clicking a minor sector must select that key for a minor song");
                point = wheel.SectionPoint(20); Invoke(wheel, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));
                Check(selected == 8, "A pitch change must preserve the song's major/minor mode");
                wheel.Other = 19; wheel.DrawToBitmap(render, wheel.ClientRectangle);
                string preview = Path.Combine(Directory.GetCurrentDirectory(), "data", "camelot-wheel-preview.png"); Directory.CreateDirectory(Path.GetDirectoryName(preview)); render.Save(preview, System.Drawing.Imaging.ImageFormat.Png);
            }
            Color coverColor = Color.FromArgb(40, 170, 100);
            using (Bitmap artwork = new Bitmap(256, 256))
            using (Graphics g = Graphics.FromImage(artwork))
            using (LibraryCard card = new LibraryCard { Folder = true, Title = "Test artist", Subtitle = "2 songs", Size = new Size(200, 258) })
            {
                int clicks = 0, renames = 0; card.Click += delegate { clicks++; }; card.RenameFolder = delegate { renames++; };
                Invoke(card, "OnMouseDown", new MouseEventArgs(MouseButtons.Right, 1, 40, 40, 0)); Invoke(card, "OnClick", EventArgs.Empty);
                Check(clicks == 0, "Right-click must leave the artist overview open for the context menu");
                Invoke(card, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 40, 40, 0)); Invoke(card, "OnClick", EventArgs.Empty);
                Check(clicks == 1, "Left-click must still open an artist folder");
                Invoke(card, "OnKeyDown", new KeyEventArgs(Keys.F2)); Check(renames == 1, "F2 must invoke artist renaming");
                Invoke(card, "OnKeyDown", new KeyEventArgs(Keys.Enter)); Check(clicks == 2, "Enter must open an artist folder");
                using (Bitmap render = new Bitmap(card.Width, card.Height))
                {
                    card.DrawToBitmap(render, card.ClientRectangle);
                    Check(render.GetPixel(53, 100).ToArgb() == PlayerTheme.Purple.ToArgb(), "Artist folders without covers must display their folder icon");
                    g.Clear(coverColor); card.Artwork = artwork;
                    card.DrawToBitmap(render, card.ClientRectangle);
                    Check(render.GetPixel(100, 100).ToArgb() == coverColor.ToArgb(), "Artist cover pictures must fill the card artwork square");
                    Check(render.GetPixel(100, 220).ToArgb() != coverColor.ToArgb(), "Artist cover pictures must leave the title and song count visible");
                }
            }
            using (var row = new LibraryCard { ListRow = true, Title = "Song", Size = new Size(1000, 72) })
            {
                int drags = 0, plays = 0; row.BeginSongDrag = delegate { drags++; }; row.Click += delegate { plays++; };
                Invoke(row, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 20, 20, 0));
                Invoke(row, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, 150, 20, 0)); Invoke(row, "OnClick", EventArgs.Empty);
                Check(drags == 1 && plays == 0, "Dragging a song row must prepare a stem drag without also playing the song");
                Invoke(row, "OnKeyDown", new KeyEventArgs(Keys.Enter));
                Check(plays == 1, "Enter must play a song row after a drag");
            }
            Directory.CreateDirectory(Path.Combine(root, ".artist-covers"));
            string cover = Path.Combine(root, ".artist-covers", "test.png");
            using (Bitmap image = new Bitmap(32, 32)) using (Graphics g = Graphics.FromImage(image)) { g.Clear(coverColor); image.Save(cover, System.Drawing.Imaging.ImageFormat.Png); }
            File.WriteAllText(Path.Combine(root, ".artist-folders.json"), new JavaScriptSerializer().Serialize(new { version = 1, aliases = new { }, covers = new Dictionary<string, string> { { "test artist", Path.Combine(".artist-covers", "test.png") }, { "outside", "..\\outside.png" } } }));
            ContextMenuStrip ownedMenu = null;
            using (PlayerForm form = new PlayerForm(root, root))
            {
                var fields = BindingFlags.Instance | BindingFlags.NonPublic;
                Control sidebar = (Control)typeof(PlayerForm).GetField("sidebar", fields).GetValue(form);
                Control content = (Control)typeof(PlayerForm).GetField("content", fields).GetValue(form);
                Control libraryButton = (Control)typeof(PlayerForm).GetField("libraryTab", fields).GetValue(form);
                Check(libraryButton.Parent == sidebar && libraryButton.Text == "Library" && libraryButton.Top == 20, "Library navigation must appear at the top of the sidebar");
                bool playerTabExists = false, libraryLabelExists = false;
                foreach (Control control in content.Controls) if (control.Text == "Now playing") playerTabExists = true;
                foreach (Control control in sidebar.Controls) if (control is Label && control.Text == "LIBRARY") libraryLabelExists = true;
                Check(!playerTabExists && !libraryLabelExists, "The player must remove its redundant Now playing tab and replace the Library label");
                foreach (string field in new[] { "currentBpm", "currentKey", "addedBpm", "addedKey" }) Check(typeof(PlayerForm).GetField(field, fields).GetValue(form) is GlassButton, "Both song headers must offer separate clickable BPM and key controls");
                Invoke(form, "ReadArtistCovers");
                ContextMenuStrip menu = (ContextMenuStrip)Invoke(form, "ArtistMenu", "Test artist"); ownedMenu = menu;
                Check(menu.Items[0].Text == "Rename artist folder…" && menu.Items[1].Text == "Choose cover picture…", "Artist menus must expose renaming and choosing a cover");
                Check(menu.Items[2].Enabled, "An artist with a saved cover must allow removing it");
                foreach (ToolStripDropDownCloseReason reason in new[] { ToolStripDropDownCloseReason.ItemClicked, ToolStripDropDownCloseReason.Keyboard, ToolStripDropDownCloseReason.AppFocusChange })
                {
                    Invoke(menu, "OnClosed", new ToolStripDropDownClosedEventArgs(reason));
                    Check(!menu.IsDisposed && menu.Handle != IntPtr.Zero, "Closing the artist menu must leave it alive for ToolStrip's remaining click processing");
                }
                using (LibraryCard first = new LibraryCard { Tag = "Test artist", ContextMenuStrip = menu })
                using (LibraryCard second = new LibraryCard { Tag = "Another artist", ContextMenuStrip = (ContextMenuStrip)Invoke(form, "ArtistMenu", "Another artist") })
                {
                    Check(Object.ReferenceEquals(first.ContextMenuStrip, second.ContextMenuStrip), "Artist cards must reuse the menu owned by the player");
                    first.Dispose();
                    Check(!menu.IsDisposed, "Rebuilding artist cards must not dispose the menu while it handles a click");
                }
                Check(menu.Items.Count == 3, "Reopening the artist menu must not duplicate actions");
                IntPtr formHandle = form.Handle;
                bool actionRan = false;
                Invoke(form, "QueueArtistAction", new Func<Task>(delegate { actionRan = true; return Task.FromResult(0); }));
                Check(!actionRan, "Artist actions must wait until the current menu click finishes");
                Application.DoEvents();
                Check(actionRan, "The deferred artist action must run on the next UI message");
                Image image = (Image)Invoke(form, "ArtistCover", "Test artist");
                Check(image != null && ((Bitmap)image).GetPixel(100, 100).ToArgb() == coverColor.ToArgb(), "Saved artist covers must load on a new player instance");
                ContextMenuStrip outsideMenu = (ContextMenuStrip)Invoke(form, "ArtistMenu", "outside"); Check(!outsideMenu.Items[2].Enabled, "Cover settings outside the artist cover directory must be ignored");
                Check(Invoke(form, "ArtistCover", "Unknown artist") == null, "Artists without covers must retain the default folder icon");
                image.Dispose();
            }
            Check(ownedMenu.IsDisposed, "Closing the player must dispose its artist menu");
            CheckCacheSettings(root); CheckPlaybackTuning(root); CheckLibraryBpmFilters(root); CheckSidebarRefreshScroll(root);
            Console.WriteLine("Artist browser: " + checks + " checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }
}
