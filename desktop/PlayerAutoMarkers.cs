using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

internal sealed partial class PlayerForm
{
    private readonly GlassButton transitionEditorButton = new GlassButton();
    private bool transitionEditorEnabled;
    private PreparedAutoTransition zoomedAutoPlan;
    private double autoViewStart, autoViewSeconds;
    private AutoWaveformWheelFilter autoWheelFilter;
    private bool AutoEditorEnabled { get { return autoPlayEnabled && transitionEditorEnabled; } }
    private void InitializeTransitionEditor()
    {
        SetupButton(transitionEditorButton, "Transition editor", content, null);
        transitionEditorButton.Toggle = true; transitionEditorButton.SelectedFill = PlayerTheme.Purple;
        transitionEditorButton.Font = new Font("Segoe UI", 8.5f); transitionEditorButton.Visible = false;
        transitionEditorButton.AccessibleName = "Switch Auto play transition editor view";
        transitionEditorButton.Click += delegate { SetTransitionEditor(!transitionEditorEnabled); };
        tip.SetToolTip(transitionEditorButton, "Optional aligned OUT/IN waveforms, transition editing and mouse-wheel zoom. The standard view is the default.");
        autoWheelFilter = new AutoWaveformWheelFilter(this); Application.AddMessageFilter(autoWheelFilter);
        Disposed += delegate { Application.RemoveMessageFilter(autoWheelFilter); };
    }
    private void SetTransitionEditor(bool enabled)
    {
        transitionEditorEnabled = enabled;
        foreach (WaveformView view in waveforms) view.ResetGesture();
        if (AutoEditorEnabled && AutoMarkersReady) ShowAddedSong(autoTransition.Song, autoTransition.Traits, autoTransition.Bank.Plan);
        else if (!AutoTransitionActive)
        {
            StemSourceBank bank = stemSources == null ? null : stemSources.Available;
            ShowAddedSong(bank == null ? null : bank.Song, bank == null ? null : bank.Traits, bank == null ? null : bank.Plan);
        }
        LayoutContent(); UpdateStemGestureMode(); SetEnabled(mixer != null); UpdateTransport();
    }
    private void ZoomAutoWaveforms(int delta, double fraction)
    {
        if (!AutoEditorEnabled || !AutoMarkersReady || delta == 0) return;
        EnsureAutoViewport(autoTransition);
        fraction = Math.Max(0, Math.Min(1, fraction));
        double duration = autoTransition.Bridge.Duration, anchor = autoViewStart + fraction * autoViewSeconds;
        double minimum = Math.Min(duration, Math.Max(.25, duration / 1600 * 4));
        double span = Math.Max(minimum, Math.Min(duration, autoViewSeconds * Math.Pow(1.35, -delta / 120.0)));
        autoViewStart = Math.Max(0, Math.Min(duration - span, anchor - fraction * span)); autoViewSeconds = span;
        UpdateAutoMarkers();
    }
    private void EnsureAutoViewport(PreparedAutoTransition plan)
    {
        if (zoomedAutoPlan == plan) return;
        zoomedAutoPlan = plan; autoViewStart = 0; autoViewSeconds = plan.Bridge.Duration;
    }
    // Route the wheel by hover position, even when a different control has focus.
    private sealed class AutoWaveformWheelFilter : IMessageFilter
    {
        private readonly PlayerForm form;
        internal AutoWaveformWheelFilter(PlayerForm owner) { form = owner; }
        public bool PreFilterMessage(ref Message message)
        {
            if (message.Msg != 0x020A || form.IsDisposed || !form.Visible || !form.AutoEditorEnabled || !form.AutoMarkersReady) return false;
            Control target = Control.FromHandle(message.HWnd);
            if (target == null || target.FindForm() != form) return false;
            long location = message.LParam.ToInt64(); Point screen = new Point((short)(location & 0xffff), (short)((location >> 16) & 0xffff));
            int delta = (short)((message.WParam.ToInt64() >> 16) & 0xffff);
            foreach (WaveformView view in form.waveforms)
            {
                Point point = view.PointToClient(screen);
                if (!view.Visible || !view.Enabled || view.AutoMarkers == null || !view.ClientRectangle.Contains(point)) continue;
                form.ZoomAutoWaveforms(delta, point.X / (double)Math.Max(1, view.Width - 1)); return true;
            }
            return false;
        }
    }
    private static string TransitionClock(double seconds)
    {
        int tenths = Math.Max(0, (int)Math.Round(seconds * 10));
        return (tenths / 600) + ":" + (tenths / 10 % 60).ToString("00") + "." + (tenths % 10);
    }
    private bool AutoMarkersReady
    {
        get
        {
            PreparedAutoTransition plan = autoTransition;
            return plan != null && plan.Bridge != null && plan.Next != null && plan.Bank != null && plan.SwapFrames != null && plan.NextPeaks != null && plan.OutgoingPeaks != null && plan.IncomingPeaks != null && plan.OutgoingDynamics != null && plan.IncomingDynamics != null && plan.NextPeaks.All(peaks => peaks != null) && plan.OutgoingPeaks.All(peaks => peaks != null) && plan.IncomingPeaks.All(peaks => peaks != null);
        }
    }
    private static double AutoOutgoingSeconds(PreparedAutoTransition plan, long frame)
    {
        return plan.SourceStart + plan.Timing.SourceAt(frame / (double)plan.Timing.Rate);
    }
    private static double AutoIncomingSeconds(PreparedAutoTransition plan, long frame)
    {
        return plan.Traits.BeatOffset + plan.Timing.IncomingSourceFrame(frame) / (double)plan.Timing.Rate;
    }
    private void UpdateAutoMarkers()
    {
        if (autoTransition == null) { zoomedAutoPlan = null; autoViewStart = autoViewSeconds = 0; }
        transitionEditorButton.Visible = autoPlayEnabled;
        if (transitionEditorButton.Selected != AutoEditorEnabled) { transitionEditorButton.Selected = AutoEditorEnabled; transitionEditorButton.Invalidate(); }
        PreparedAutoTransition plan = AutoEditorEnabled && AutoMarkersReady ? autoTransition : null;
        if (plan != null) EnsureAutoViewport(plan);
        bool bridge = plan != null && playback != null && playback.Current == plan.Bridge;
        for (int lane = 0; lane < 4; lane++)
        {
            WaveformView view = waveforms[lane];
            if (plan == null) { if (view.AutoMarkers != null) { view.AutoMarkers = null; view.Invalidate(); } continue; }
            long cut = plan.CutFrames == null ? plan.SwapFrames[lane] : plan.CutFrames[lane], arrival = plan.SwapFrames[lane];
            double elapsed = bridge ? Math.Max(0, plan.Bridge.Seconds - .1) : 0;
            view.AutoMarkers = new AutoWaveformMarkers
            {
                OutgoingPeaks = plan.OutgoingPeaks[lane], IncomingPeaks = plan.IncomingPeaks[lane],
                OutgoingDuration = plan.Bridge.Duration, IncomingDuration = plan.Bridge.Duration,
                Cut = cut / (double)plan.Timing.Rate, Start = arrival / (double)plan.Timing.Rate,
                OutgoingMinimum = 0, IncomingMinimum = 0,
                OutgoingProgress = bridge ? elapsed / plan.Bridge.Duration : -1,
                IncomingProgress = bridge ? elapsed / plan.Bridge.Duration : -1,
                ViewStart = autoViewStart, ViewSeconds = autoViewSeconds,
                OutgoingColor = SongColor(current), IncomingColor = SongColor(plan.Song, true),
                CutText = TransitionClock(AutoOutgoingSeconds(plan, cut)), StartText = TransitionClock(AutoIncomingSeconds(plan, arrival))
            };
            view.Invalidate();
        }
    }
    private bool EditAutoTransitionPoint(int lane, bool incoming, double at)
    {
        return SetAutoTransitionPoint(lane, incoming, at, (ModifierKeys & Keys.Shift) == 0);
    }
    private bool SetAutoTransitionPoint(int lane, bool incoming, double at, bool snap)
    {
        if (!AutoEditorEnabled || !AutoMarkersReady || playback == null || loading || editingTuning || editingArtists || exportCancellation != null || lane < 0 || lane > 3) return false;
        PreparedAutoTransition plan = autoTransition;
        double seconds = at;
        long chosen = 0;
        bool changed = playback.EditScheduledTransition(plan.Reference, plan.Bridge, minimum =>
        {
            long oldCut = plan.CutFrames == null ? plan.SwapFrames[lane] : plan.CutFrames[lane];
            long oldStart = plan.SwapFrames[lane];
            AutoAudioDynamics dynamics = incoming ? plan.IncomingDynamics[lane] : plan.OutgoingDynamics[lane];
            long? snapped = AutoStemTransitions.SnapEditor(seconds, lane, incoming, dynamics, plan.Timing, snap);
            if (!snapped.HasValue) return false;
            long cut = incoming ? oldCut : snapped.Value, start = incoming ? snapped.Value : oldStart;
            if (!plan.Bridge.RescheduleStemTransition(lane, cut, start, minimum)) return false;
            if (plan.CutFrames == null) plan.CutFrames = (long[])plan.SwapFrames.Clone();
            plan.CutFrames[lane] = cut; plan.SwapFrames[lane] = start; chosen = snapped.Value; return true;
        });
        if (changed)
        {
            PollAutoTransition();
            UpdateAutoMarkers(); UpdateSourceButtons();
            message.Text = "Auto play · " + TitleCase(StemMixer.StemNames[lane]) + (incoming ? " comes in at " : " cuts out at ") + TransitionClock(incoming ? AutoIncomingSeconds(plan, chosen) : AutoOutgoingSeconds(plan, chosen));
        }
        else message.Text = "Auto play · wait for the next song and transition range to finish loading.";
        return changed;
    }
    private void SetAutoSourceButton(int lane)
    {
        GlassButton button = sourceButtons[lane]; PreparedAutoTransition plan = AutoMarkersReady ? autoTransition : null;
        button.DisplayOnly = AutoEditorEnabled; button.Toggle = !AutoEditorEnabled;
        button.AccessibleRole = AutoEditorEnabled ? AccessibleRole.StaticText : AccessibleRole.PushButton;
        button.Cursor = AutoEditorEnabled ? Cursors.Default : Cursors.Hand;
        if (!AutoEditorEnabled) { button.Subtitle = null; button.Text = StemMixer.StemNames[lane].ToUpperInvariant(); button.AccessibleName = "Toggle " + StemMixer.StemNames[lane] + " original / replacement"; return; }
        long cut = plan == null ? 0 : plan.CutFrames == null ? plan.SwapFrames[lane] : plan.CutFrames[lane];
        button.Text = plan == null ? "Next song…" : plan.Song.Title;
        button.Subtitle = plan == null ? "Preparing transition" : "OUT " + TransitionClock(AutoOutgoingSeconds(plan, cut)) + " · IN " + TransitionClock(AutoIncomingSeconds(plan, plan.SwapFrames[lane]));
        button.Selected = false; button.Enabled = mixer != null;
        button.Swatch = plan == null ? PlayerTheme.Glass : SongColor(plan.Song, true);
        string description = plan == null ? "Next song and transition points appear when all four stems finish loading." : "Next: " + plan.Song.Title + " — " + plan.Song.Artist + ".\n" + TitleCase(StemMixer.StemNames[lane]) + " cuts out at " + Clock(AutoOutgoingSeconds(plan, cut)) + " in the current song; comes in at " + Clock(AutoIncomingSeconds(plan, plan.SwapFrames[lane])) + " in the next song.";
        button.AccessibleName = TitleCase(StemMixer.StemNames[lane]) + " next song and transition"; button.AccessibleDescription = description; tip.SetToolTip(button, null); button.Invalidate();
    }
}
