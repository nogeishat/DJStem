using System;
using System.Linq;

internal sealed partial class PlayerForm
{
    private bool StemGestureActive { get { return waveforms.Any(waveform => waveform != null && waveform.Gesturing); } }
    private double StemGestureBpm { get { return sharedBpm ?? (currentNativeTraits != null ? currentNativeTraits.Bpm : current != null && current.Bpm.HasValue ? current.Bpm.Value : 0); } }
    private double StemGesturePhase { get { return currentNativeTraits == null ? 0 : currentNativeTraits.BeatOffset / originalTempo; } }
    private void UpdateStemGestureMode()
    {
        foreach (WaveformView waveform in waveforms)
        {
            waveform.TimingEnabled = !autoPlayEnabled;
            string description = AutoEditorEnabled ? "Aligned transition preview: right-click the upper waveform to move OUT or the lower waveform to move IN once the next song is ready. Points move independently throughout the transition range and snap to musical boundaries; Shift with right-click places a point exactly. Hover any stem and scroll to zoom all stems around the mouse position." : autoPlayEnabled ? "Left click seeks all stems before a transition begins. Manual offsets and loops are disabled during Auto play. Switch on Transition editor for aligned waveforms, point editing and mouse-wheel zoom." : "Left click seeks all stems. Right drag offsets only this stem relative to the others. Drag right to delay it or left to advance it; release keeps the offset rounded to whole beats. Hold right click for 1.5 seconds for a bar loop; drag its end to extend it.";
            waveform.AccessibleDescription = description;
            tip.SetToolTip(waveform, AutoEditorEnabled ? null : description);
        }
    }
    private void InitializeStemGestures(int lane)
    {
        WaveformView waveform = waveforms[lane];
        waveform.TransitionPointRequested += delegate(bool incoming, double seconds) { EditAutoTransitionPoint(lane, incoming, seconds); };
        waveform.ZoomRequested += ZoomAutoWaveforms;
        waveform.AccessibleDescription = "Left click seeks all stems. Right drag offsets only this stem relative to the others. Drag right to delay it or left to advance it; release keeps the offset rounded to whole beats. Hold right click for 1.5 seconds for a bar loop; drag its end to extend it.";
        tip.SetToolTip(waveform, "Right drag: offset only this stem from the others.\nRight delays it; left advances it. Release keeps the offset in whole beats.\nHold right click 1.5 seconds: loop the nearest bar.\nDrag the loop's end: change its length in whole bars.");
        waveform.BeginGesture = delegate
        {
            if (autoPlayEnabled || loading || editingTuning || editingArtists || AutoTransitionActive || mixer == null || !mixer.HasStems || mixer.PositionFrames >= mixer.TotalFrames) return false;
            if (currentNativeTraits == null || StemGestureBpm < 30 || StemGestureBpm > 300) { message.Text = "Wait for BPM and beat analysis before moving or looping a stem."; return false; }
            CancelAutoPreparation();
            if (AutoTransitionActive) return false;
            waveform.Bpm = StemGestureBpm;
            SelectMix(); return true;
        };
        waveform.ShiftChanged += delegate(double seconds, bool committed)
        {
            if (autoPlayEnabled || mixer == null || AutoTransitionActive) return;
            mixer.SetStemShift(lane, seconds); waveform.ShowTiming(mixer.StemState(lane)); UpdateStemTimingLabels(lane, mixer.StemState(lane), StemGestureBpm);
            if (committed)
            {
                double beats = Math.Abs(mixer.StemState(lane).Shift * StemGestureBpm / 60);
                message.Text = TitleCase(StemMixer.StemNames[lane]) + " · " + (beats < .00001 ? "aligned with the other stems" : beats.ToString("0") + (Math.Abs(beats - 1) < .00001 ? " beat " : " beats ") + (seconds > 0 ? "later" : "earlier") + " than the other stems");
            }
        };
        waveform.LoopRequested += delegate(double at)
        {
            if (autoPlayEnabled || mixer == null || AutoTransitionActive) return;
            try
            {
                mixer.StartStemLoop(lane, at, StemGestureBpm, StemGesturePhase); waveform.ShowTiming(mixer.StemState(lane)); UpdateStemTimingLabels(lane, mixer.StemState(lane), StemGestureBpm);
                message.Text = TitleCase(StemMixer.StemNames[lane]) + " · 1 bar loop · drag its end to extend it";
            }
            catch (Exception error) { ShowError(error); }
        };
        waveform.LoopEndChanged += delegate(double end)
        {
            if (autoPlayEnabled || mixer == null || AutoTransitionActive) return;
            mixer.ExtendStemLoop(lane, end, StemGestureBpm); waveform.ShowTiming(mixer.StemState(lane)); UpdateStemTimingLabels(lane, mixer.StemState(lane), StemGestureBpm);
        };
        waveform.GestureEnded += delegate { autoAttempt = null; UpdateTransport(); };
    }
    private StemTiming[] StemTimingSnapshot() { return mixer == null ? null : Enumerable.Range(0, 4).Select(lane => mixer.StemState(lane)).ToArray(); }
    private void UpdateStemTimingLabels(int lane, StemTiming state, double bpm)
    {
        int bars = bpm > 0 ? Math.Max(1, (int)Math.Round((state.LoopEnd - state.LoopStart) * bpm / 240)) : 1;
        stemLoopLabels[lane].Text = state.Looping ? "Loop: " + bars + (bars == 1 ? " bar" : " bars") : "";
        stemLoopLabels[lane].Visible = state.Looping;
        double beats = bpm > 0 ? state.Shift * bpm / 60 : 0;
        stemOffsetLabels[lane].Text = Math.Abs(beats) > .00001 ? "Offset: " + (beats > 0 ? "+" : "") + beats.ToString("0.#") + " beats" : "";
        stemOffsetLabels[lane].Visible = Math.Abs(beats) > .00001;
        stemLoopLabels[lane].ForeColor = stemOffsetLabels[lane].ForeColor = waveforms[lane].Accent;
        tip.SetToolTip(stemLoopLabels[lane], state.Looping ? "This stem repeats " + bars + (bars == 1 ? " bar. " : " bars. ") + "Drag the waveform's loop end to resize it." : "");
        tip.SetToolTip(stemOffsetLabels[lane], Math.Abs(beats) > .00001 ? Math.Abs(beats).ToString("0.#") + " beats " + (beats > 0 ? "later" : "earlier") + " than the other stems." : "");
    }
    private void UpdateStemWaveform(int lane, double position, double length, StemMixer.DisplayState display)
    {
        WaveformView waveform = waveforms[lane]; waveform.Bpm = StemGestureBpm;
        waveform.SourceDuration = display == null ? length : display.SourceDurations[lane];
        StemTiming state = display == null ? new StemTiming() : display.Timing[lane]; waveform.ShowTiming(state);
        if (autoTransition != null && playback != null && playback.Current == autoTransition.Bridge && autoTransition.OutgoingTiming != null && !autoTransition.Switched[lane] && !autoTransition.Silenced[lane]) UpdateStemTimingLabels(lane, autoTransition.OutgoingTiming[lane], autoTransition.Timing.FromBpm);
        else UpdateStemTimingLabels(lane, state, StemGestureBpm);
        waveform.Progress = length <= 0 ? 0 : position / length;
        if (state.Looping && position >= state.LoopAnchor)
        {
            long remaining; position = state.SourceFrame((long)Math.Round(position * mixer.WaveFormat.SampleRate), mixer.WaveFormat.SampleRate, out remaining) / (double)mixer.WaveFormat.SampleRate + state.Shift;
        }
        waveform.CursorProgress = length <= 0 ? 0 : Math.Max(0, Math.Min(1, position / length));
    }
}
