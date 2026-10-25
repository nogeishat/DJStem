"""Exercise real beat/key analysis with deterministic audio, without library writes."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
import os
import shutil
import subprocess

import numpy as np
import soundfile as sf

spec = importlib.util.spec_from_file_location("music_analysis", Path(__file__).with_name("analyze-music.py"))
music = importlib.util.module_from_spec(spec)
spec.loader.exec_module(music)


class AnalysisTests(unittest.TestCase):
    def test_tuning_references_and_unpitched_audio(self):
        time = np.arange(music.RATE * 6) / music.RATE
        for reference in (432, 440, 442, 448):
            # A4 need not occur: a C/E/G chord still identifies the reference.
            audio = sum(.15 * np.sin(2 * np.pi * reference * 2 ** (semitones / 12) * time)
                        for semitones in (-21, -9, -5, -2, 3))
            result = music.estimate_tuning([audio], music.RATE)
            self.assertAlmostEqual(result['tuningHz'], reference, delta=.3)
            self.assertGreater(result['tuningConfidence'], .5)
        silence = music.estimate_tuning([np.zeros_like(time)], music.RATE)
        self.assertIsNone(silence['tuningHz'])
        noise = music.estimate_tuning([np.random.default_rng(12).normal(size=len(time))], music.RATE)
        self.assertIsNone(noise['tuningHz'])

    def test_profiles_and_silence(self):
        for minor, profile in ((False, music.MAJOR), (True, music.MINOR)):
            for tonic in range(12):
                detected, mode, confidence = music.tonal_key(np.roll(profile, tonic))
                self.assertEqual((detected, mode), (tonic, minor))
                self.assertGreater(confidence, 0)
        self.assertEqual(music.tonal_key(np.zeros(12))[0], -1)
        self.assertEqual(music.tonal_key(np.full(12, np.nan))[0], -1)

    def test_audio_tempo_and_key(self):
        rate, seconds = 44100, 16
        time = np.arange(rate * seconds) / rate
        rhythm = np.zeros(len(time))
        random = np.random.default_rng(9)
        for onset in np.arange(.13, seconds - .05, .5):
            start = round(onset * rate)
            pulse = random.normal(size=round(.025 * rate)) * np.hanning(round(.025 * rate))
            rhythm[start:start + len(pulse)] += pulse * .3
        bass = .2 * np.sin(2 * np.pi * 130.8128 * time)
        harmony = sum(.15 * np.sin(2 * np.pi * frequency * time) for frequency in (261.6256, 329.6276, 391.9954))
        with tempfile.TemporaryDirectory(prefix="stemkit-analysis-test-") as temporary:
            folder = Path(temporary)
            for stem, samples in (("drums", rhythm), ("bass", bass), ("other", harmony)):
                sf.write(folder / (stem + ".wav"), np.column_stack((samples, samples)), rate, subtype="PCM_24")
            result = music.analyze(folder)
            self.assertAlmostEqual(result["bpm"], 120, delta=.3)
            self.assertEqual((result["tonic"], result["minor"]), (0, False))
            self.assertGreater(result["beatConfidence"], .5)
            self.assertGreaterEqual(result["beatOffset"], 0)
            self.assertLess(result["beatOffset"], .5)
            sf.write(folder / "drums.wav", np.zeros((len(time), 2)), rate, subtype="PCM_24")
            with self.assertRaisesRegex(ValueError, "rhythm"):
                music.analyze(folder)

    def test_downloaded_mp3_and_silence(self):
        ffmpeg = os.environ.get("STEMKIT_FFMPEG") or shutil.which("ffmpeg")
        if not ffmpeg:
            self.skipTest("FFmpeg is needed to generate a real MP3 fixture")
        rate, seconds = 22050, 16
        time = np.arange(rate * seconds) / rate
        rhythm = np.zeros(len(time))
        random = np.random.default_rng(9)
        for onset in np.arange(.13, seconds - .05, .5):
            start = round(onset * rate)
            pulse = random.normal(size=round(.025 * rate)) * np.hanning(round(.025 * rate))
            rhythm[start:start + len(pulse)] += pulse * .6
        harmony = sum(.12 * np.sin(2 * np.pi * frequency * time) for frequency in (130.8128, 261.6256, 329.6276, 391.9954))
        with tempfile.TemporaryDirectory(prefix="stemkit-mp3-analysis-") as temporary:
            folder = Path(temporary)
            wave, mp3 = folder / "mixed.wav", folder / "downloaded.mp3"
            samples = rhythm + harmony
            sf.write(wave, np.column_stack((samples, samples)), rate)
            subprocess.run([ffmpeg, "-v", "error", "-y", "-i", str(wave), "-c:a", "libmp3lame", "-q:a", "0", str(mp3)], check=True, capture_output=True)
            result = music.analyze_audio(mp3)
            self.assertAlmostEqual(result["bpm"], 120, delta=.3)
            self.assertEqual(result["key"], "C major")
            self.assertEqual(result["source"], "mp3")
            self.assertAlmostEqual(result["duration"], seconds, delta=.2)
            self.assertGreater(result["beatConfidence"], .5)
            sf.write(wave, np.zeros((len(time), 2)), rate)
            self.assertAlmostEqual(result['tuningHz'], 440, delta=.3)
            tuning = music.analyze_tuning(mp3)
            self.assertAlmostEqual(tuning['tuningHz'], 440, delta=.3)
            result = music.analyze_audio(wave)
            self.assertIsNone(result["bpm"])
            self.assertIsNone(result["key"])
            self.assertEqual(result["beatConfidence"], 0)
            self.assertIsNone(result['tuningHz'])


if __name__ == "__main__":
    unittest.main(verbosity=2)
