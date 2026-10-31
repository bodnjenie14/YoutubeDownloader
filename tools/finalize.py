"""Shrinks an offline library made by YoutubeDownloader.Cli.

For every video (matched by the video ID in the playlist XMLs), one copy is
re-encoded to HEVC (NVIDIA NVENC) with 48 kbps mono AAC, and every other copy
of the same video is replaced with a hard link to it, so each video is stored
once but still appears in every folder.

A file is only replaced after the new one is checked: it must be smaller and
have the same duration. Files that are already HEVC are left alone, so the
script can be re-run safely.

Usage: python finalize.py <library-root> [--workers N] [--dry-run]
"""

import argparse
import json
import os
import subprocess
import sys
import threading
import time
import xml.etree.ElementTree as ET
from concurrent.futures import ThreadPoolExecutor

FFMPEG_ARGS = [
    "-map", "0", "-c", "copy",
    "-c:v:0", "hevc_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "36", "-b:v", "0",
    "-tag:v:0", "hvc1",
    "-c:a", "aac", "-b:a", "48k", "-ac", "1",
    "-movflags", "+faststart",
]

# Profiles encoded at their own quality (see recompress_profile.py): never linked to other copies
SEPARATE_PROFILES = {"mantis"}

log_lock = threading.Lock()


def log(message, log_file):
    line = f"{time.strftime('%H:%M:%S')} {message}"
    with log_lock:
        print(line, flush=True)
        with open(log_file, "a", encoding="utf-8") as f:
            f.write(line + "\n")


def probe(path):
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "format=duration:stream=index,codec_type,codec_name",
         "-of", "json", path],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    if result.returncode != 0:
        return None
    data = json.loads(result.stdout or "{}")
    streams = data.get("streams", [])
    video = next((s for s in streams if s.get("codec_type") == "video"), None)
    try:
        duration = float(data.get("format", {}).get("duration"))
    except (TypeError, ValueError):
        return None
    return {"duration": duration, "video_codec": video and video.get("codec_name")}


def collect(root):
    """(Separate profile or "", video ID) -> list of existing, non-empty file paths."""
    by_id = {}
    for dirpath, _, filenames in os.walk(root):
        top_folder = os.path.relpath(dirpath, root).split(os.sep)[0]
        group = top_folder if top_folder in SEPARATE_PROFILES else ""
        for name in filenames:
            if not name.lower().endswith(".xml"):
                continue
            try:
                tree = ET.parse(os.path.join(dirpath, name))
            except ET.ParseError:
                continue
            for video in tree.getroot().iter("Video"):
                video_id = video.get("Id")
                file_name = video.findtext("FileName")
                if not video_id or not file_name:
                    continue
                path = os.path.join(dirpath, file_name)
                if os.path.isfile(path) and os.path.getsize(path) > 0:
                    paths = by_id.setdefault((group, video_id), [])
                    if not any(os.path.normcase(p) == os.path.normcase(path) for p in paths):
                        paths.append(path)
    return by_id


def same_file(a, b):
    try:
        return os.path.samefile(a, b)
    except OSError:
        return False


def compress(path, log_file):
    """Returns (bytes_before, bytes_after) or None if the original was kept."""
    info = probe(path)
    if info is None:
        log(f"SKIP  unreadable: {path}", log_file)
        return None
    if info["video_codec"] in ("hevc", None):
        return None

    temp_path = path + ".hevc.tmp.mp4"
    before = os.path.getsize(path)
    result = subprocess.run(
        ["ffmpeg", "-v", "error", "-y", "-i", path, *FFMPEG_ARGS, temp_path],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    try:
        new_info = probe(temp_path) if result.returncode == 0 else None
        if new_info is None:
            log(f"KEEP  encode failed: {path}: {result.stderr.strip()[:200]}", log_file)
            return None
        if abs(new_info["duration"] - info["duration"]) > 2:
            log(f"KEEP  duration mismatch ({info['duration']:.0f}s -> {new_info['duration']:.0f}s): {path}", log_file)
            return None
        after = os.path.getsize(temp_path)
        if after >= before:
            log(f"KEEP  not smaller: {path}", log_file)
            return None
        os.replace(temp_path, path)
        return before, after
    finally:
        if os.path.exists(temp_path):
            os.remove(temp_path)


def link_copies(paths, log_file):
    """Replaces every other copy with a hard link to paths[0]. Returns bytes freed."""
    canonical = paths[0]
    freed = 0
    for path in paths[1:]:
        if same_file(canonical, path):
            continue
        size = os.path.getsize(path)
        temp_link = path + ".link.tmp"
        try:
            os.link(canonical, temp_link)
            os.replace(temp_link, path)
            freed += size
        except OSError as error:
            log(f"LINK  failed for {path}: {error}", log_file)
            if os.path.exists(temp_link):
                os.remove(temp_link)
    return freed


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("root")
    parser.add_argument("--workers", type=int, default=3)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    log_file = os.path.join(args.root, "_finalize-log.txt")
    by_id = collect(args.root)

    copies = sum(len(p) for p in by_id.values())
    total_bytes = sum(os.path.getsize(p) for paths in by_id.values() for p in paths)
    log(f"{len(by_id)} videos, {copies} files, {total_bytes / 1e9:.1f} GB", log_file)

    if args.dry_run:
        dupes = {k: v for k, v in by_id.items() if len(v) > 1}
        dupe_bytes = sum(os.path.getsize(p) for v in dupes.values() for p in v[1:])
        log(f"dry run: {len(dupes)} videos have copies, {dupe_bytes / 1e9:.1f} GB in extra copies", log_file)
        return

    stats = {"done": 0, "before": 0, "after": 0, "freed": 0}
    stats_lock = threading.Lock()

    def process(item):
        _, paths = item
        # Prefer a copy that is already HEVC so it isn't encoded twice
        paths = sorted(paths, key=lambda p: (probe(p) or {}).get("video_codec") != "hevc")
        result = compress(paths[0], log_file)
        freed = link_copies(paths, log_file)
        with stats_lock:
            stats["done"] += 1
            if result:
                stats["before"] += result[0]
                stats["after"] += result[1]
            stats["freed"] += freed
            if stats["done"] % 25 == 0 or stats["done"] == len(by_id):
                saved = stats["before"] - stats["after"] + stats["freed"]
                log(f"[{stats['done']}/{len(by_id)}] saved {saved / 1e9:.1f} GB so far "
                    f"(compression {(stats['before'] - stats['after']) / 1e9:.1f} GB, "
                    f"duplicates {stats['freed'] / 1e9:.1f} GB)", log_file)

    with ThreadPoolExecutor(args.workers) as pool:
        list(pool.map(process, by_id.items()))

    after_bytes = sum(os.path.getsize(p) for paths in collect(args.root).values() for p in set(paths[:1]))
    log(f"finished: unique storage now about {after_bytes / 1e9:.1f} GB", log_file)


if __name__ == "__main__":
    sys.exit(main())
