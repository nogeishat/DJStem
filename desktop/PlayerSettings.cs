using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

internal sealed partial class PlayerForm
{
    private void OpenPlayerSettings()
    {
        Func<AutoPlaySettings> read;
        using (Form dialog = CreatePlayerSettingsDialog(out read))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            tip.Active = ((CheckBox)dialog.Controls.Find("ShowHoverInfo", true).Single()).Checked;
            autoSettings = read(); autoSettings.Validate(); autoFailedSongs.Clear();
            tuningReferenceHz = ((Func<double?>)dialog.Tag)();
            if (!AutoTransitionActive) CancelAutoPreparation();
            UpdateAutoPreload(); UpdateAutoRestart(); SavePreferences();
            var cacheLimit = (NumericUpDown)dialog.Controls.Find("MatchedStemsMaxGb", true).Single();
            if (cacheLimit.Tag is long && (long)cacheLimit.Tag > (long)(cacheLimit.Value * 1000000000m))
                message.Text = "Matched-stems limit saved. Some files are in use or unavailable; cleanup will retry automatically.";
        }
    }
    private Form CreatePlayerSettingsDialog(out Func<AutoPlaySettings> read)
    {
            Form dialog = new Form { Text = "Settings", ClientSize = new Size(610, Math.Min(850, Screen.FromControl(this).WorkingArea.Height - 100)), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = PlayerTheme.Background, ForeColor = PlayerTheme.Text, Font = Font };
            Panel body = new Panel { Name = "SettingsContent", Dock = DockStyle.Fill, AutoScroll = true };
            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 60 };
            dialog.Controls.Add(body); dialog.Controls.Add(footer);
            Label title = new Label { Text = "Auto play", Bounds = new Rectangle(24, 18, 540, 35), Font = new Font("Segoe UI", 18, FontStyle.Bold) };
            Label description = new Label { Text = "Shuffle includes half/double time and further powers of two.\nOne bar = four beats. Changes apply to the next prepared transition.", Bounds = new Rectangle(26, 64, 560, 48), ForeColor = PlayerTheme.Muted };
            string[] labels = { "Next song: slower by up to", "Next song: faster by up to", "Tempo change duration", "Time between stem changes", "Cut off song ending" };
            double[] values = { autoSettings.Below, autoSettings.Above, autoSettings.RampBars, autoSettings.StemBars, autoSettings.EndCutBars };
            var fields = new NumericUpDown[5];
            for (int index = 0; index < fields.Length; index++)
            {
                fields[index] = new NumericUpDown { Bounds = new Rectangle(380, 127 + index * 43, 104, 28), Minimum = index == 3 ? .25m : 0, Maximum = index < 2 ? 270 : 64, DecimalPlaces = index < 2 ? 1 : 2, Increment = index < 2 ? 1 : .25m, Value = (decimal)values[index], AccessibleName = labels[index], BackColor = PlayerTheme.Glass, ForeColor = PlayerTheme.Text };
                body.Controls.Add(new Label { Text = labels[index], Bounds = new Rectangle(26, 129 + index * 43, 342, 28) });
                body.Controls.Add(fields[index]); body.Controls.Add(new Label { Text = index < 2 ? "BPM" : "bars", Bounds = new Rectangle(498, 129 + index * 43, 74, 28), ForeColor = PlayerTheme.Muted });
            }
            Label range = new Label { Bounds = new Rectangle(26, 344, 556, 48), ForeColor = PlayerTheme.Muted, Font = new Font("Segoe UI", 9) };
            Action updateRange = delegate { double bpm = sharedBpm ?? (currentNativeTraits == null ? current != null && current.Bpm.HasValue ? current.Bpm.Value : 120 : currentNativeTraits.Bpm); if (bpm < 30) bpm = 120; range.Text = "Shuffle range at " + bpm.ToString("0.##") + " BPM: " + Math.Max(30, bpm - (double)fields[0].Value).ToString("0.##") + "–" + Math.Min(300, bpm + (double)fields[1].Value).ToString("0.##") + " BPM (completed stems required).\nEnd cut: start about " + ((double)fields[4].Value * 240 / bpm).ToString("0.##") + " seconds earlier at this BPM."; };
            fields[0].ValueChanged += delegate { updateRange(); }; fields[1].ValueChanged += delegate { updateRange(); }; fields[4].ValueChanged += delegate { updateRange(); }; updateRange();
            CheckBox early = new CheckBox { Text = "Transition fading or quiet stems early", Bounds = new Rectangle(26, 402, 556, 28), Checked = autoSettings.EarlyStemTransitions, AccessibleName = "Transition fading or quiet stems early" };
            Label earlyHelp = new Label { Text = "Avoid peaks; vocals wait for quiet phrase boundaries in both songs.\nPriority: vocals, then drums/other, then bass. Switches stay apart.", Bounds = new Rectangle(46, 435, 536, 40), ForeColor = PlayerTheme.Muted, Font = new Font("Segoe UI", 9) };
            Label orderTitle = new Label { Text = "Stem transition order", Bounds = new Rectangle(26, 482, 450, 28), Font = new Font("Segoe UI", 10, FontStyle.Bold) };
            ListBox order = new ListBox { Bounds = new Rectangle(26, 516, 342, 122), BackColor = PlayerTheme.Glass, ForeColor = PlayerTheme.Text, BorderStyle = BorderStyle.FixedSingle, AccessibleName = "Stem transition order" };
            var lanes = autoSettings.Order.ToList();
            Action refresh = delegate { int selected = order.SelectedIndex; order.Items.Clear(); for (int index = 0; index < lanes.Count; index++) order.Items.Add((index + 1) + ". " + TitleCase(StemMixer.StemNames[lanes[index]])); order.SelectedIndex = Math.Max(0, selected); }; refresh();
            Action<int> move = delegate(int direction) { int index = order.SelectedIndex, target = index + direction; if (index < 0 || target < 0 || target >= 4) return; int lane = lanes[index]; lanes[index] = lanes[target]; lanes[target] = lane; refresh(); order.SelectedIndex = target; };
            Button up = new Button { Text = "Move up", Bounds = new Rectangle(388, 516, 164, 34) }, down = new Button { Text = "Move down", Bounds = new Rectangle(388, 561, 164, 34) };
            up.Click += delegate { move(-1); }; down.Click += delegate { move(1); };
            Button save = new Button { Text = "Save", Bounds = new Rectangle(386, 14, 86, 32) }, cancel = new Button { Text = "Cancel", Bounds = new Rectangle(484, 14, 100, 32), DialogResult = DialogResult.Cancel };
            foreach (Button button in new[] { up, down, save, cancel }) { button.FlatStyle = FlatStyle.Flat; button.BackColor = PlayerTheme.Glass; button.ForeColor = PlayerTheme.Text; button.FlatAppearance.BorderColor = PlayerTheme.Border; }
            body.Controls.AddRange(new Control[] { title, description, range, early, earlyHelp, orderTitle, order, up, down });
            footer.Controls.AddRange(new Control[] { save, cancel }); dialog.AcceptButton = save; dialog.CancelButton = cancel;
            GroupBox tuning = new GroupBox { Text = "Song tuning (A4 reference)", Bounds = new Rectangle(26, 656, 558, 124), ForeColor = PlayerTheme.Text };
            RadioButton original = new RadioButton { Text = "Original", Bounds = new Rectangle(14, 23, 114, 28), Checked = !tuningReferenceHz.HasValue, AccessibleName = "Original song tuning" };
            RadioButton specific = new RadioButton { Text = "Specific frequency", Bounds = new Rectangle(140, 23, 178, 28), Checked = tuningReferenceHz.HasValue, AccessibleName = "Specific A4 tuning frequency" };
            NumericUpDown hz = new NumericUpDown { Bounds = new Rectangle(328, 24, 110, 28), Minimum = 300, Maximum = 600, DecimalPlaces = 2, Increment = .1m, Value = (decimal)(tuningReferenceHz ?? 440), Enabled = tuningReferenceHz.HasValue, AccessibleName = "A4 frequency in Hz", BackColor = PlayerTheme.Glass, ForeColor = PlayerTheme.Text };
            specific.CheckedChanged += delegate { hz.Enabled = specific.Checked; };
            tuning.Controls.AddRange(new Control[] { original, specific, hz, new Label { Text = "Hz", Bounds = new Rectangle(449, 27, 60, 25) }, new Label { Text = "Applies when songs next load; BPM and key edits still work.\nUnknown tuning keeps its original pitch. Audio files are unchanged.", Bounds = new Rectangle(14, 66, 526, 44), ForeColor = PlayerTheme.Muted, Font = new Font("Segoe UI", 9) } });
            body.Controls.Add(tuning);
            foreach (Control control in body.Controls) control.Top += 274;
            GroupBox appearance = new GroupBox { Text = "Interface", Bounds = new Rectangle(26, 18, 558, 96), ForeColor = PlayerTheme.Text };
            CheckBox hoverInfo = new CheckBox { Name = "ShowHoverInfo", Text = "Show hover info", Bounds = new Rectangle(14, 24, 526, 28), Checked = tip.Active, AccessibleName = "Show hover info" };
            appearance.Controls.AddRange(new Control[] { hoverInfo, new Label { Text = "Show info boxes when you hover over controls. Off by default.", Bounds = new Rectangle(34, 58, 506, 25), ForeColor = PlayerTheme.Muted, Font = new Font("Segoe UI", 9) } });
            body.Controls.Add(appearance);
            long initialLimit = PlayerMatchedCache.LimitBytes(project);
            GroupBox cache = new GroupBox { Text = "Matched-stems storage", Bounds = new Rectangle(26, 128, 558, 146), ForeColor = PlayerTheme.Text };
            NumericUpDown cacheLimit = new NumericUpDown { Name = "MatchedStemsMaxGb", Bounds = new Rectangle(328, 28, 110, 28), Minimum = PlayerMatchedCache.MinLimitGb, Maximum = PlayerMatchedCache.MaxLimitGb, DecimalPlaces = 1, Increment = .1m, Value = initialLimit / 1000000000m, AccessibleName = "Maximum matched-stems folder size in GB", BackColor = PlayerTheme.Glass, ForeColor = PlayerTheme.Text };
            Label cacheStatus = new Label { Text = "Lowering this limit deletes the oldest unused files on Save.\nAudio in use is kept until it can be cleared. 1 GB = 1 billion bytes.", Bounds = new Rectangle(14, 72, 526, 60), ForeColor = PlayerTheme.Muted, Font = new Font("Segoe UI", 9) };
            cache.Controls.AddRange(new Control[] { new Label { Text = "Maximum folder size", Bounds = new Rectangle(14, 31, 300, 28) }, cacheLimit, new Label { Text = "GB", Bounds = new Rectangle(449, 31, 60, 25) }, cacheStatus });
            body.Controls.Add(cache);
            bool cpuEdition = Environment.GetEnvironmentVariable("STEMKIT_EDITION") == "cpu";
            GroupBox separation = new GroupBox { Text = "Stem separation", Bounds = new Rectangle(26, tuning.Bottom + 18, 558, 118), ForeColor = PlayerTheme.Text };
            ComboBox separationDevice = new ComboBox { Name = "SeparationDevice", Bounds = new Rectangle(14, 25, 526, 28), DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Stem separation processor", Enabled = !cpuEdition };
            separationDevice.Items.Add(cpuEdition ? "CPU only" : "Automatic (use a compatible NVIDIA GPU when available)");
            if (!cpuEdition) separationDevice.Items.Add("CPU only");
            separationDevice.SelectedIndex = !cpuEdition && PlayerData.SeparationDevice(project) == "cpu" ? 1 : 0;
            separation.Controls.AddRange(new Control[] { separationDevice, new Label { Text = cpuEdition ? "This edition uses your CPU for separation." : "Applies to the next song. CPU processing remains available.\nA compatible NVIDIA graphics driver is required for GPU processing.", Bounds = new Rectangle(14, 62, 526, 44), ForeColor = PlayerTheme.Muted, Font = new Font("Segoe UI", 9) } });
            body.Controls.Add(separation); body.AutoScrollMinSize = new Size(0, separation.Bottom + 18);
            bool saving = false;
            dialog.FormClosing += delegate(object sender, FormClosingEventArgs args) { if (saving) args.Cancel = true; };
            save.Click += async delegate
            {
                if (saving) return;
                bool saved = false; saving = true; save.Enabled = cancel.Enabled = body.Enabled = false;
                try
                {
                    if ((long)(cacheLimit.Value * 1000000000m) != initialLimit)
                    {
                        save.Text = "Saving…";
                        cacheLimit.Tag = await PlayerMatchedCache.SetLimit(project, cacheLimit.Value);
                    }
                    if (!cpuEdition) PlayerData.SaveSeparationDevice(project, separationDevice.SelectedIndex == 1 ? "cpu" : "auto");
                    saved = true;
                }
                catch (Exception error) { cacheStatus.Text = error.Message; cacheStatus.ForeColor = PlayerTheme.Stems[1]; body.ScrollControlIntoView(cache); }
                finally { saving = false; save.Text = "Save"; save.Enabled = cancel.Enabled = body.Enabled = true; }
                if (saved) dialog.DialogResult = DialogResult.OK;
            };
            dialog.Tag = (Func<double?>)delegate { return specific.Checked ? (double?)hz.Value : null; };
            read = delegate { return new AutoPlaySettings { Below = (double)fields[0].Value, Above = (double)fields[1].Value, RampBars = (double)fields[2].Value, StemBars = (double)fields[3].Value, EndCutBars = (double)fields[4].Value, EarlyStemTransitions = early.Checked, Order = lanes.ToArray() }; };
            return dialog;
    }
}
