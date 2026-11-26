#!/usr/bin/env python3
"""Deterministic sampled video analysis for AI interpretation."""

from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path
from typing import Any

SCHEMA_VERSION = "1.0"


def dependencies():
    try:
        import imageio.v2 as imageio
        import numpy as np
    except ImportError as exc:
        raise RuntimeError("Missing dependencies. With approval, install imageio imageio-ffmpeg numpy in a workspace venv.") from exc
    return imageio, np


def finite_number(value: Any, default: float = 0.0) -> float:
    try:
        number = float(value)
        return number if math.isfinite(number) else default
    except (TypeError, ValueError):
        return default


def video_metadata(reader) -> dict:
    meta = reader.get_meta_data()
    fps = finite_number(meta.get("fps"))
    duration = finite_number(meta.get("duration"))
    size = meta.get("size") or (None, None)
    frame_count = meta.get("nframes")
    if not duration and fps and isinstance(frame_count, (int, float)) and math.isfinite(frame_count):
        duration = float(frame_count) / fps
    return {
        "duration_seconds": round(duration, 6),
        "fps": round(fps, 6),
        "width": size[0],
        "height": size[1],
        "codec": meta.get("codec"),
    }


def sample_video(path: Path, scope: str, tail_seconds: float, interval: float) -> tuple[dict, list[dict]]:
    imageio, np = dependencies()
    reader = imageio.get_reader(str(path), format="ffmpeg")
    try:
        meta = video_metadata(reader)
        fps = meta["fps"]
        if fps <= 0:
            raise RuntimeError("Video FPS could not be determined")
        duration = meta["duration_seconds"]
        start = max(0.0, duration - tail_seconds) if scope == "tail" and duration > 0 else 0.0
        start_index = max(0, int(start * fps))
        step = max(1, int(round(interval * fps)))
        samples: list[dict] = []
        previous = None
        for index, frame in enumerate(reader):
            if index < start_index or (index - start_index) % step:
                continue
            rgb = np.asarray(frame, dtype=np.float32)[..., :3]
            luminance = float(rgb.mean()) / 255.0
            diff = None if previous is None else float(np.abs(rgb - previous).mean()) / 255.0
            samples.append({
                "frame": index,
                "time_seconds": round(index / fps, 6),
                "luminance": round(luminance, 6),
                "difference": None if diff is None else round(diff, 6),
            })
            previous = rgb
        return meta, samples
    finally:
        reader.close()


def stats(samples: list[dict], key: str) -> dict | None:
    values = [item[key] for item in samples if item[key] is not None]
    if not values:
        return None
    values.sort()
    return {
        "min": round(values[0], 6),
        "max": round(values[-1], 6),
        "mean": round(sum(values) / len(values), 6),
        "median": round(values[len(values) // 2], 6),
    }


def segments(samples: list[dict], predicate, min_seconds: float, interval: float) -> list[dict]:
    found, current = [], []
    for sample in samples:
        if predicate(sample):
            current.append(sample)
        elif current:
            if len(current) * interval >= min_seconds:
                found.append({"start_seconds": current[0]["time_seconds"], "end_seconds": current[-1]["time_seconds"], "samples": len(current)})
            current = []
    if current and len(current) * interval >= min_seconds:
        found.append({"start_seconds": current[0]["time_seconds"], "end_seconds": current[-1]["time_seconds"], "samples": len(current)})
    return found


def transcribe(path: Path, model_name: str, language: str | None) -> dict:
    try:
        from faster_whisper import WhisperModel
    except ImportError as exc:
        raise RuntimeError("Missing STT dependency. With approval, install faster-whisper in a workspace venv.") from exc
    model = WhisperModel(model_name, device="auto", compute_type="default")
    items, info = model.transcribe(str(path), language=language or None)
    return {
        "language": getattr(info, "language", language),
        "segments": [
            {"start_seconds": round(float(item.start), 3), "end_seconds": round(float(item.end), 3), "text": item.text.strip()}
            for item in items
        ],
    }


def analyze(args: argparse.Namespace) -> dict:
    path = Path(args.input).expanduser().resolve()
    if not path.is_file():
        raise FileNotFoundError(f"Input video not found: {path}")
    base = {
        "schema_version": SCHEMA_VERSION,
        "input": str(path),
        "mode": args.mode,
        "parameters": {
            "scope": args.scope,
            "tail_seconds": args.tail_seconds,
            "sample_interval": args.sample_interval,
            "black_threshold": args.black_threshold,
            "freeze_threshold": args.freeze_threshold,
            "min_segment_seconds": args.min_segment_seconds,
        },
    }
    if args.mode == "transcribe":
        base["transcription"] = transcribe(path, args.stt_model, args.stt_language)
        return base
    meta, samples = sample_video(path, args.scope, args.tail_seconds, args.sample_interval)
    base["metadata"] = meta
    base["sample_count"] = len(samples)
    if not samples:
        raise RuntimeError("No frames were sampled; verify the video and scope")
    if args.mode == "summary":
        base["luminance"] = stats(samples, "luminance")
        base["difference"] = stats(samples, "difference")
        base["first_sample"] = samples[0]
        base["last_sample"] = samples[-1]
    elif args.mode == "black-segments":
        base["segments"] = segments(samples, lambda s: s["luminance"] <= args.black_threshold, args.min_segment_seconds, args.sample_interval)
    else:
        base["segments"] = segments(samples, lambda s: s["difference"] is not None and s["difference"] <= args.freeze_threshold, args.min_segment_seconds, args.sample_interval)
    return base


def parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description="Analyze a local video with deterministic sampling")
    p.add_argument("--input", required=True)
    p.add_argument("--mode", choices=("summary", "black-segments", "freeze-segments", "transcribe"), default="summary")
    p.add_argument("--scope", choices=("full", "tail"), default="full")
    p.add_argument("--tail-seconds", type=float, default=3.0)
    p.add_argument("--sample-interval", type=float, default=0.25)
    p.add_argument("--black-threshold", type=float, default=0.03)
    p.add_argument("--freeze-threshold", type=float, default=0.002)
    p.add_argument("--min-segment-seconds", type=float, default=0.5)
    p.add_argument("--stt-model", default="small")
    p.add_argument("--stt-language")
    p.add_argument("--output")
    p.add_argument("--dry-run", action="store_true")
    p.add_argument("--force", action="store_true")
    return p


def main(argv: list[str] | None = None) -> int:
    args = parser().parse_args(argv)
    if args.tail_seconds <= 0 or args.sample_interval <= 0 or args.min_segment_seconds <= 0:
        raise ValueError("Time values must be positive")
    if not 0 <= args.black_threshold <= 1 or not 0 <= args.freeze_threshold <= 1:
        raise ValueError("Thresholds must be between 0 and 1")
    input_path = Path(args.input).expanduser().resolve()
    if args.dry_run:
        if not input_path.is_file():
            raise FileNotFoundError(f"Input video not found: {input_path}")
        target = Path(args.output).expanduser().resolve() if args.output else "stdout"
        print(f"[dry-run] input={input_path} mode={args.mode} output={target}")
        return 0
    report = analyze(args)
    body = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
    if not args.output:
        print(body, end="")
        return 0
    output = Path(args.output).expanduser().resolve()
    if output.exists() and not args.force:
        raise FileExistsError(f"Output already exists; use --force to replace it: {output}")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(body, encoding="utf-8", newline="\n")
    print(f"Wrote analysis: {output}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"Error: {exc}", file=sys.stderr)
        raise SystemExit(1)
