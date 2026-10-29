"""Downloads the banner (the wide "channel art" at the top of a channel page) for every
channel folder in an offline library.

Channel and subscriber folders use the channel ID as their list ID. Each banner is saved
as banners/<channelId>.jpg and copied into the channel's folder as banner.jpg. Banners
already downloaded are skipped. Banner URLs come from yt-dlp, which must be installed.

Usage: python banners.py <library-root> [--yt-dlp <exe>] [--width 1707]
"""

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import urllib.request
import xml.etree.ElementTree as ET
from concurrent.futures import ThreadPoolExecutor

SECTIONS = ["playlists", "channels", "history", "subscribers", "shorts"]


def find_yt_dlp():
    links = os.path.join(os.environ.get("LOCALAPPDATA", ""), "Microsoft", "WinGet", "Links", "yt-dlp.exe")
    return shutil.which("yt-dlp") or (links if os.path.isfile(links) else None)


def channel_folders(root):
    folders = {}
    for section in SECTIONS:
        for dirpath, _, filenames in os.walk(os.path.join(root, section)):
            for name in filenames:
                if not name.lower().endswith(".xml"):
                    continue
                try:
                    list_id = ET.parse(os.path.join(dirpath, name)).getroot().get("Id", "")
                except ET.ParseError:
                    continue
                if list_id.startswith("UC") and len(list_id) == 24:
                    folders.setdefault(list_id, []).append(dirpath)
    return folders


def banner_url(yt_dlp, channel_id, width):
    result = subprocess.run(
        [yt_dlp, "-J", "--flat-playlist", "--playlist-items", "0",
         f"https://www.youtube.com/channel/{channel_id}"],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120,
    )
    if result.returncode != 0:
        return None
    # Numbered entries wider than they are tall are the banner sizes; the avatar is square
    sizes = [t for t in json.loads(result.stdout).get("thumbnails", [])
             if t.get("width") and t.get("height") and t["width"] > 2 * t["height"]]
    if not sizes:
        return None
    return min(sizes, key=lambda t: abs(t["width"] - width))["url"]


def save_jpeg(url, path, ffmpeg):
    data = urllib.request.urlopen(url, timeout=60).read()
    if data[:3] == b"\xff\xd8\xff":
        with open(path, "wb") as f:
            f.write(data)
        return
    # Not a JPEG (usually WebP): convert it so every picture in the library is a .jpg
    with tempfile.NamedTemporaryFile(delete=False, suffix=".img") as f:
        f.write(data)
    try:
        subprocess.run([ffmpeg, "-y", "-loglevel", "error", "-i", f.name, path], check=True)
    finally:
        os.remove(f.name)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("root")
    parser.add_argument("--yt-dlp", default=find_yt_dlp())
    parser.add_argument("--ffmpeg", default=shutil.which("ffmpeg") or "ffmpeg")
    parser.add_argument("--width", type=int, default=1707)
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    if not args.yt_dlp:
        print("yt-dlp not found")
        return 1

    banners_dir = os.path.join(args.root, "banners")
    os.makedirs(banners_dir, exist_ok=True)
    folders = channel_folders(args.root)

    def fetch(channel_id):
        path = os.path.join(banners_dir, f"{channel_id}.jpg")
        if os.path.isfile(path) and os.path.getsize(path) > 0:
            return "had"
        try:
            url = banner_url(args.yt_dlp, channel_id, args.width)
            if url is None:
                return "none"
            save_jpeg(url, path, args.ffmpeg)
            return "new"
        except Exception as ex:
            print(f"Skipping {channel_id}: {str(ex).splitlines()[0] if str(ex) else type(ex).__name__}")
            return "failed"

    with ThreadPoolExecutor(3) as pool:
        results = dict(zip(folders, pool.map(fetch, folders)))

    copied = 0
    for channel_id, dirs in folders.items():
        path = os.path.join(banners_dir, f"{channel_id}.jpg")
        if os.path.isfile(path):
            for d in dirs:
                shutil.copyfile(path, os.path.join(d, "banner.jpg"))
                copied += 1

    counts = {k: list(results.values()).count(k) for k in ("new", "had", "none", "failed")}
    print(f"{len(folders)} channels: {counts['new']} new banners, {counts['had']} already there, "
          f"{counts['none']} without a banner, {counts['failed']} failed; banner.jpg in {copied} folders")


if __name__ == "__main__":
    sys.exit(main())
