"""Headless adapter for the pinned Ultimate Vocal Remover separation engine."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[1]
UVR_ROOT = ROOT / "tools" / "uvr"
MODEL_ROOT = ROOT / "tools" / "stem-models"
UVR_COMMIT = "5517e0cf0d1acd16a1618eeedec596957523f9e1"
MDX_FILENAME = "UVR-MDX-NET-Inst_HQ_5.onnx"
MODEL_NAMES = {"drums": "Demucs: v4 | htdemucs", "bass": "Demucs: v4 | htdemucs", "other": "Demucs: v4 | htdemucs", "vocals": "MDX-Net: UVR-MDX-NET Inst HQ 5"}
DLL_HANDLES = []


def event(stage, progress, message):
    print("STEMKIT_STEMS:" + json.dumps({"stage": stage, "progress": max(0, min(100, int(progress))), "message": message}), flush=True)


def model_data(method, model_path, constants, device, parameters=None):
    """Supply the GUI's ModelData contract without creating its Tk application."""
    values = {
        "is_pitch_change": False, "semitone_shift": 0, "is_match_frequency_pitch": True,
        "overlap": 0.25, "overlap_mdx": constants.DEFAULT, "overlap_mdx23": 8,
        "is_mdx_combine_stems": False, "is_mdx_c": False, "mdx_c_configs": None,
        "mdxnet_stem_select": constants.ALL_STEMS, "mixer_path": None,
        "model_samplerate": 44100, "model_capacity": (32, 128), "is_vr_51_model": False,
        "is_pre_proc_model": False, "is_secondary_model_activated": False, "is_secondary_model": False,
        "process_method": method, "model_path": str(model_path), "model_name": model_path.stem,
        "model_basename": model_path.stem, "wav_type_set": "PCM_24", "mp3_bit_set": "320k",
        "save_format": constants.WAV, "is_gpu_conversion": 0 if device == "cuda" else -1,
        "is_normalization": False, "is_primary_stem_only": False, "is_secondary_stem_only": False,
        "is_primary_model_primary_stem_only": False, "is_primary_model_secondary_stem_only": False,
        "is_ensemble_mode": False, "secondary_model": None, "primary_model_primary_stem": None,
        "primary_stem_native": constants.VOCAL_STEM, "primary_stem": constants.VOCAL_STEM,
        "secondary_stem": constants.INST_STEM, "is_invert_spec": False, "is_deverb_vocals": False,
        "is_mixer_mode": False, "secondary_model_scale": None, "is_demucs_pre_proc_model_inst_mix": False,
        "ensemble_primary_stem": constants.VOCAL_STEM, "ensemble_secondary_stem": constants.INST_STEM,
        "is_multi_stem_ensemble": False, "DENOISER_MODEL": None, "DEVERBER_MODEL": None,
        "vocal_split_model": None, "is_vocal_split_model": False, "is_save_inst_vocal_splitter": False,
        "is_inst_only_voc_splitter": False, "is_karaoke": False, "is_bv_model": False,
        "bv_model_rebalance": 0, "is_sec_bv_rebalance": False, "deverb_vocal_opt": constants.VOCAL_STEM,
        "is_save_vocal_only": False, "device_set": constants.DEFAULT, "is_use_opencl": False,
        "is_mdx_ckpt": False, "is_denoise": False, "is_denoise_model": False, "is_mdx_c_seg_def": False,
        "mdx_batch_size": 1, "compensate": 1, "mdx_segment_size": 256,
        "mdx_dim_f_set": None, "mdx_dim_t_set": None, "mdx_n_fft_scale_set": None,
        "chunks": constants.AUTO_SELECT, "margin": 44100,
        "demucs_stems": constants.ALL_STEMS, "secondary_model_4_stem": [None] * 4,
        "secondary_model_4_stem_scale": [None] * 4, "is_chunk_demucs": False,
        "segment": constants.DEFAULT, "demucs_version": constants.DEMUCS_V4,
        "demucs_source_list": constants.DEMUCS_4_SOURCE, "demucs_source_map": constants.DEMUCS_4_SOURCE_MAPPER,
        "is_demucs_combine_stems": True, "demucs_stem_count": 4, "pre_proc_model": None,
        "shifts": 2, "is_split_mode": True,
    }
    if parameters:
        values.update({key: parameters[key] for key in ("compensate", "mdx_dim_f_set", "mdx_dim_t_set", "mdx_n_fft_scale_set")})
        values.update(primary_stem=constants.INST_STEM, primary_stem_native=constants.INST_STEM,
                      secondary_stem=constants.VOCAL_STEM, is_secondary_stem_only=True,
                      mdx_segment_size=2 ** parameters["mdx_dim_t_set"])
    return SimpleNamespace(**values)


def process_data(audio, destination, stage):
    def progress(base=0, value=0):
        fraction = max(0, min(1, float(base) + float(value)))
        percent = fraction * 60 if stage == "demucs" else 60 + fraction * 38
        event(stage, percent, "Separating drums, bass and other…" if stage == "demucs" else "Separating vocals with Inst HQ 5…")

    return {
        "audio_file": str(audio), "audio_file_base": "audio", "export_path": str(destination),
        "set_progress_bar": progress, "write_to_console": lambda *args, **kwargs: None,
        "cached_source_callback": lambda *args, **kwargs: (None, None),
        "cached_model_source_holder": lambda *args, **kwargs: None,
        "is_4_stem_ensemble": False, "is_ensemble_master": False,
        "list_all_models": [], "process_iteration": lambda *args, **kwargs: None,
    }


def dependencies():
    sys.path.insert(0, str(UVR_ROOT))
    import torch
    # Load Torch's CUDA/cuDNN libraries before ONNX Runtime on Windows.
    if os.name == "nt":
        torch_lib = Path(torch.__file__).parent / "lib"
        DLL_HANDLES.append(os.add_dll_directory(str(torch_lib)))
        os.environ["PATH"] = str(torch_lib) + os.pathsep + os.environ.get("PATH", "")
    import onnxruntime
    import soundfile
    import separate
    from gui_data import constants
    torch.set_num_threads(min(4, os.cpu_count() or 1))
    return torch, onnxruntime, soundfile, separate, constants


def separate_song(audio, destination, requested_device):
    torch, ort, sf, engine, constants = dependencies()
    device = "cuda" if requested_device != "cpu" and torch.cuda.is_available() else "cpu"
    if requested_device == "cuda" and device != "cuda":
        raise RuntimeError("CUDA was explicitly requested, but is unavailable.")
    destination.mkdir(parents=True, exist_ok=True)
    work = destination / ".work"
    work.mkdir(exist_ok=True)
    params = json.loads((MODEL_ROOT / "mdx" / "config.json").read_text(encoding="utf-8"))
    demucs_path = MODEL_ROOT / "demucs" / "htdemucs.yaml"
    mdx_path = MODEL_ROOT / "mdx" / MDX_FILENAME
    device_used = {}
    for stage, klass, model_path, parameters in [
        ("demucs", engine.SeperateDemucs, demucs_path, None),
        ("mdx", engine.SeperateMDX, mdx_path, params),
    ]:
        folder = work / stage
        folder.mkdir(exist_ok=True)
        stage_device = device
        runner = None
        event(stage, 0 if stage == "demucs" else 60, "Loading htdemucs…" if stage == "demucs" else "Loading UVR-MDX-NET Inst HQ 5…")
        try:
            runner = klass(model_data(constants.DEMUCS_ARCH_TYPE if stage == "demucs" else constants.MDX_ARCH_TYPE, model_path, constants, stage_device, parameters), process_data(audio, folder, stage))
            runner.seperate()
        except Exception as error:
            detail = str(error).lower()
            if stage_device != "cuda" or not any(marker in detail for marker in ("out of memory", "cudamalloc", "cudnn_status_alloc_failed")):
                raise
            runner = None
            engine.clear_gpu_cache()
            stage_device = "cpu"
            event(stage, 0 if stage == "demucs" else 60, "GPU memory is full; continuing on CPU…")
            runner = klass(model_data(constants.DEMUCS_ARCH_TYPE if stage == "demucs" else constants.MDX_ARCH_TYPE, model_path, constants, stage_device, parameters), process_data(audio, folder, stage))
            runner.seperate()
        device_used[stage] = stage_device
        del runner
        engine.clear_gpu_cache()

    files = {stem: work / ("mdx" if stem == "vocals" else "demucs") / ("audio_(" + stem.title() + ").wav") for stem in MODEL_NAMES}
    frames = None
    for stem, file in files.items():
        if not file.exists():
            raise RuntimeError("UVR did not create the " + stem + " stem.")
        info = sf.info(str(file))
        if info.samplerate != 44100 or info.channels != 2 or info.frames <= 0:
            raise RuntimeError("Invalid " + stem + " WAV output.")
        if frames is not None and info.frames != frames:
            raise RuntimeError("Stem lengths do not match.")
        frames = info.frames
    event("saving", 99, "Saving four stems…")
    for stem, file in files.items():
        shutil.copyfile(file, destination / (stem + ".wav"))
    manifest = {
        "pipelineVersion": 1, "uvrCommit": UVR_COMMIT, "models": MODEL_NAMES,
        "inputFile": audio.name, "inputSha256": hashlib.sha256(audio.read_bytes()).hexdigest(),
        "sampleRate": 44100, "channels": 2, "frames": frames, "subtype": "PCM_24",
        "devices": device_used, "torch": torch.__version__, "onnxruntime": ort.__version__,
        "settings": {"demucsShifts": 2, "demucsOverlap": 0.25, "mdxSegment": 256, "mdxDenoise": False, "normalize": False},
    }
    temporary = destination / "separation.json.tmp"
    temporary.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    temporary.replace(destination / "separation.json")
    # Only delete this adapter's own work folder, after verifying containment.
    if work.resolve().parent != destination.resolve():
        raise RuntimeError("Refusing to clean an unexpected work folder.")
    shutil.rmtree(work)
    event("completed", 100, "Drums, bass, other and vocals saved")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--device", choices=["auto", "cpu", "cuda"], default="auto")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    if args.check:
        torch, ort, _, _, _ = dependencies()
        print(json.dumps({"uvrCommit": UVR_COMMIT, "cuda": torch.cuda.is_available(), "gpu": torch.cuda.get_device_name(0) if torch.cuda.is_available() else None, "providers": ort.get_available_providers()}))
        return
    if not args.input or not args.output or not args.input.is_file():
        parser.error("Provide an existing --input MP3 and an --output folder.")
    separate_song(args.input.resolve(), args.output.resolve(), args.device)


if __name__ == "__main__":
    main()
