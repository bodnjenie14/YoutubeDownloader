"""Re-encodes a profile's videos at a stronger setting, to fit a smaller device.

The profile's files are usually hard links to videos in the main library. Each one is
encoded to a new file and swapped in for every path inside the profile, so the main
library's copy is left as it was. A file is only replaced after the new one is checked: it
must be smaller and have the same duration. Finished videos are listed in
<profile>/_recompressed.txt, so the script can be re-run safely. Run it before a device
downloads the profile: the device matches videos by size.

Usage: python recompress_profile.py <library-root> <profile> [--cq 38] [--workers 3]
"""

import argparse
import json
import os
import subprocess
import sys
import threading
from concurrent.futures import ThreadPoolExecutor


def duration(path):
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "json", path],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    try:
        return float(json.loads(result.stdout or "{}")["format"]["duration"])
    except (KeyError, TypeError, ValueError):
        return None


def encode(paths, cq):
    """Returns (bytes_before, bytes_after), or None if the original was kept."""
    source = paths[0]
    temp_path = source + ".cq.tmp.mp4"
    before = os.path.getsize(source)
    length = duration(source)
    result = subprocess.run(
        ["ffmpeg", "-v", "error", "-y", "-i", source, "-map", "0", "-c", "copy",
         "-c:v:0", "hevc_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", str(cq), "-b:v", "0",
         "-tag:v:0", "hvc1", "-movflags", "+faststart", temp_path],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    try:
        new_length = duration(temp_path) if result.returncode == 0 else None
        if new_length is None or length is None or abs(new_length - length) > 2:
            print(f"KEEP  encode failed: {source}: {result.stderr.strip()[:200]}", flush=True)
            return None
        after = os.path.getsize(temp_path)
        if after >= before:
            return None
        os.replace(temp_path, source)
        for other in paths[1:]:
            link_temp = other + ".cq.tmp.mp4"
            os.link(source, link_temp)
            os.replace(link_temp, other)
        return before, after
    finally:
        if os.path.exists(temp_path):
            os.remove(temp_path)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("root")
    parser.add_argument("profile")
    parser.add_argument("--cq", type=int, default=38)
    parser.add_argument("--workers", type=int, default=3)
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    profile_dir = os.path.join(args.root, args.profile)
    done_path = os.path.join(profile_dir, "_recompressed.txt")
    done = set()
    if os.path.isfile(done_path):
        done = set(open(done_path, encoding="utf-8").read().splitlines())

    # Every path of each stored file, so links inside the profile stay together
    groups = {}
    for dirpath, _, filenames in os.walk(profile_dir):
        for name in filenames:
            if name.lower().endswith(".mp4") and not name.endswith(".tmp.mp4"):
                path = os.path.join(dirpath, name)
                stat = os.stat(path)
                groups.setdefault((stat.st_dev, stat.st_ino), []).append(path)

    todo = [paths for paths in groups.values() if not any(os.path.basename(p) in done for p in paths)]
    print(f"{len(groups)} files, {len(todo)} to encode", flush=True)

    lock = threading.Lock()
    stats = {"done": 0, "before": 0, "after": 0, "kept": 0}

    def process(paths):
        result = encode(paths, args.cq)
        with lock:
            stats["done"] += 1
            if result:
                stats["before"] += result[0]
                stats["after"] += result[1]
            else:
                stats["kept"] += 1
            with open(done_path, "a", encoding="utf-8") as f:
                f.writelines(os.path.basename(p) + "\n" for p in paths)
            if stats["done"] % 25 == 0 or stats["done"] == len(todo):
                print(f"[{stats['done']}/{len(todo)}] {stats['before'] / 1e9:.2f} -> {stats['after'] / 1e9:.2f} GB, "
                      f"{stats['kept']} kept as they were", flush=True)

    with ThreadPoolExecutor(args.workers) as pool:
        list(pool.map(process, todo))


if __name__ == "__main__":
    sys.exit(main())
