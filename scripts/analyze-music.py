"""Local tempo/beat-phase and tonal-key estimates from MP3s or separated stems."""
import argparse
import json
import math
import os
from pathlib import Path

import librosa
import numpy as np
import soundfile as sf
from scipy.signal import resample_poly
from scipy.ndimage import median_filter
from tuning_estimator import estimate_tuning

VERSION = 2
RATE = 22050
HOP = 256
MAJOR = np.array([6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88])
MINOR = np.array([6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17])


def tonal_component(audio, harmonic=True):
    """Median HPSS, without importing effects' unrelated clustering dependencies.

    Time/frequency medians and squared soft masks use the same 31-bin defaults
    as librosa.decompose.hpss; retain phase and the exact input length.
    """
    if len(audio) == 0:
        return audio
    spectrum = librosa.stft(audio)
    magnitude = np.abs(spectrum)
    sustained = median_filter(magnitude, size=(1, 31), mode="reflect") ** 2
    transient = median_filter(magnitude, size=(31, 1), mode="reflect") ** 2
    total = sustained + transient
    mask = np.divide(sustained if harmonic else transient, total,
                     out=np.full_like(total, .5), where=total > 0)
    return librosa.istft(spectrum * mask, length=len(audio))


def mono(file, start=0, seconds=120):
    with sf.SoundFile(file) as stream:
        stream.seek(min(len(stream), round(start * stream.samplerate)))
        audio = stream.read(round(seconds * stream.samplerate), dtype="float32", always_2d=True).mean(axis=1)
        divisor = math.gcd(stream.samplerate, RATE)
        return resample_poly(audio, RATE // divisor, stream.samplerate // divisor).astype(np.float32)


def tonal_key(chroma):
    if not np.isfinite(chroma).all() or np.max(chroma) < 1e-8 or np.std(chroma) < 1e-8:
        return -1, False, 0.0
    scores = []
    for minor, profile in ((False, MAJOR), (True, MINOR)):
        for tonic in range(12):
            score = float(np.corrcoef(chroma, np.roll(profile, tonic))[0, 1])
            scores.append((score, tonic, minor))
    scores.sort(reverse=True)
    score, tonic, minor = scores[0]
    # A margin is more useful than labeling every estimate as certain.
    confidence = max(0.0, min(1.0, (score - scores[1][0]) * 5.0))
    return tonic, minor, confidence


def tempo_grid(rhythm):
    if rhythm.size < RATE * 4 or np.max(np.abs(rhythm)) < 1e-6:
        raise ValueError("No usable rhythm to estimate tempo.")
    envelope = librosa.onset.onset_strength(y=rhythm, sr=RATE, hop_length=HOP)
    tempo, beats = librosa.beat.beat_track(onset_envelope=envelope, sr=RATE, hop_length=HOP, trim=True)
    times = librosa.frames_to_time(beats, sr=RATE, hop_length=HOP)
    if len(times) < 5:
        raise ValueError("Too few beats to estimate tempo reliably.")
    periods = np.diff(times)
    period = float(np.median(periods))
    indices = np.concatenate(([0], np.cumsum(np.maximum(1, np.rint(periods / period).astype(int)))))
    # Regress many quantized beats rather than using one FFT-hop-sized interval.
    # This avoids a small tempo rounding error accumulating into seconds of drift.
    period, intercept = np.polyfit(indices, times, 1)
    period = float(period)
    bpm = 60.0 / period
    # Refine against the full onset envelope to avoid cumulative timing drift.
    weights = np.maximum(0, envelope - np.percentile(envelope, 40)) * np.hanning(len(envelope))
    transform_size = 1 << int(np.ceil(np.log2(len(weights) * 128)))
    spectrum = np.fft.rfft(weights, n=transform_size)
    frequencies = np.fft.rfftfreq(transform_size, HOP / RATE)
    candidates = np.flatnonzero((frequencies * 60 > bpm * .95) & (frequencies * 60 < bpm * 1.05))
    peak = int(candidates[np.argmax(np.abs(spectrum[candidates]))])
    magnitudes = np.abs(spectrum[peak - 1:peak + 2])
    denominator = magnitudes[0] - 2 * magnitudes[1] + magnitudes[2]
    correction = 0 if abs(denominator) < 1e-10 else .5 * (magnitudes[0] - magnitudes[2]) / denominator
    bpm = float((peak + np.clip(correction, -.5, .5)) * RATE / HOP / transform_size * 60)
    period = 60 / bpm
    # Circular mean places the grid consistently despite quiet intros/late first beats.
    onset_times = np.arange(len(weights)) * HOP / RATE
    phase_vector = np.sum(weights * np.exp(2j * np.pi * onset_times / period)) / max(1e-8, np.sum(weights))
    beat_offset = float((np.angle(phase_vector) / (2 * np.pi) * period) % period)
    beat_confidence = float(min(1, abs(phase_vector) * 3))
    return bpm, beat_offset, beat_confidence


def analyze(folder):
    folder = Path(folder)
    bpm, beat_offset, beat_confidence = tempo_grid(mono(folder / "drums.wav"))
    duration = sf.info(folder / "other.wav").duration
    chroma = np.zeros(12)
    for fraction in (0.15, 0.45, 0.75):
        start = max(0, min(duration - 20, duration * fraction))
        harmonic = mono(folder / "other.wav", start, 20) + mono(folder / "bass.wav", start, 20)
        if harmonic.size and np.max(np.abs(harmonic)) > 1e-6:
            # Suppress percussive leakage before pitch-class aggregation.
            harmonic = tonal_component(harmonic)
            chroma += librosa.feature.chroma_stft(y=harmonic, sr=RATE, n_fft=4096, hop_length=1024).sum(axis=1)
    tonic, minor, key_confidence = tonal_key(chroma)
    return dict(version=VERSION, bpm=bpm, beatOffset=beat_offset, beatConfidence=beat_confidence,
                tonic=tonic, minor=minor, keyConfidence=key_confidence)


def analyze_audio(file):
    """Read the downloaded audio itself; no stem files or model inference needed."""
    file = Path(file)
    duration = sf.info(file).duration
    rhythm = mono(file)
    # Percussive content gives the beat detector fewer sustained vocal/chord attacks.
    if rhythm.size >= RATE * 4 and np.max(np.abs(rhythm)) > 1e-6:
        rhythm = tonal_component(rhythm, harmonic=False)
    try:
        bpm, beat_offset, beat_confidence = tempo_grid(rhythm)
        if not math.isfinite(bpm) or not 30 <= bpm <= 300:
            bpm, beat_offset, beat_confidence = None, 0.0, 0.0
    except ValueError:
        # Silence/rhythmless audio still downloads; do not invent a musical tempo.
        bpm, beat_offset, beat_confidence = None, 0.0, 0.0
    chroma = np.zeros(12)
    excerpts = []
    # Avoid counting the same excerpt repeatedly in short songs.
    starts = sorted(set(max(0, min(duration - 20, duration * fraction)) for fraction in (.15, .45, .75)))
    for start in starts:
        harmonic = mono(file, start, 20)
        if harmonic.size and np.max(np.abs(harmonic)) > 1e-6:
            harmonic = tonal_component(harmonic)
            excerpts.append(harmonic)
            chroma += librosa.feature.chroma_stft(y=harmonic, sr=RATE, n_fft=4096, hop_length=1024).sum(axis=1)
    tonic, minor, key_confidence = tonal_key(chroma)
    notes = ("C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B")
    key = None if tonic < 0 else notes[tonic] + (" minor" if minor else " major")
    return dict(version=VERSION, source="mp3", duration=round(duration, 3), bpm=bpm, key=key,
                beatOffset=beat_offset, beatConfidence=beat_confidence,
                tonic=tonic, minor=minor, keyConfidence=key_confidence, **estimate_tuning(excerpts, RATE))


def analyze_tuning(file):
    duration = sf.info(file).duration
    starts = sorted(set(max(0, min(duration - 20, duration * fraction)) for fraction in (.15, .45, .75)))
    excerpts = [tonal_component(mono(file, start, 20)) for start in starts]
    return dict(source="mp3", **estimate_tuning(excerpts, RATE))


def main():
    parser = argparse.ArgumentParser()
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--folder")
    source.add_argument("--input")
    parser.add_argument("--output", required=True)
    parser.add_argument("--tuning-only", action="store_true")
    args = parser.parse_args()
    result = (analyze_tuning(args.input) if args.tuning_only else analyze_audio(args.input)) if args.input else analyze(args.folder)
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(".tmp-" + str(os.getpid()))
    try:
        temporary.write_text(json.dumps(result, allow_nan=False), encoding="utf-8")
        os.replace(temporary, output)
    finally:
        temporary.unlink(missing_ok=True)
    print(json.dumps(result, allow_nan=False))


if __name__ == "__main__":
    main()
