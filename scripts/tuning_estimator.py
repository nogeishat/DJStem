"""Estimate the equal-tempered A4 reference, modulo one semitone around 440 Hz.

Audio cannot distinguish a shifted note name from a reference a semitone away.
Use spectral peaks across excerpts, rather than assuming an audible A is present.
"""
import math

import librosa
import numpy as np
from scipy.ndimage import gaussian_filter1d

TUNING_VERSION = 1


def tuning_peaks(audio, rate):
    if len(audio) < rate // 2 or np.max(np.abs(audio), initial=0) < 1e-6:
        return np.empty(0), np.empty(0)
    pitches, magnitudes = librosa.piptrack(
        y=audio, sr=rate, n_fft=16384, hop_length=2048,
        fmin=80, fmax=4000, threshold=.15)
    # Normalize each frame so a loud passage cannot drown out the other excerpts.
    floor = np.max(magnitudes, axis=0, keepdims=True)
    selected = (magnitudes > floor * .2) & (pitches > 0) & (floor > 1e-6)
    weights = np.sqrt(magnitudes / np.maximum(floor, 1e-12))[selected]
    residual = (12 * np.log2(pitches[selected] / 440) + .5) % 1 - .5
    return residual, weights


def estimate_tuning(excerpts, rate):
    samples = [tuning_peaks(audio, rate) for audio in excerpts]
    offsets = np.concatenate([sample[0] for sample in samples]) if samples else np.empty(0)
    weights = np.concatenate([sample[1] for sample in samples]) if samples else np.empty(0)
    unknown = dict(tuningVersion=TUNING_VERSION, tuningHz=None, tuningCents=None, tuningConfidence=0.0)
    if offsets.size < 12 or weights.sum() <= 0:
        return unknown
    histogram, edges = np.histogram(offsets, bins=200, range=(-.5, .5), weights=weights)
    peak = int(np.argmax(gaussian_filter1d(histogram, 5, mode="wrap")))
    center = (edges[peak] + edges[peak + 1]) / 2
    distances = (offsets - center + .5) % 1 - .5
    near = np.abs(distances) <= .12
    mass = float(weights[near].sum() / weights.sum())
    coherence = float(abs(np.sum(weights * np.exp(2j * np.pi * offsets))) / weights.sum())
    # Broad/noisy or competing tunings do not justify a single correction.
    if mass < .35 or coherence < .35:
        return unknown
    offset = float((center + np.average(distances[near], weights=weights[near]) + .5) % 1 - .5)
    confidence = float(np.clip(mass * coherence, 0, 1))
    hz = 440 * math.pow(2, offset / 12)
    return dict(tuningVersion=TUNING_VERSION, tuningHz=round(hz, 3),
                tuningCents=round(offset * 100, 3), tuningConfidence=round(confidence, 4))
