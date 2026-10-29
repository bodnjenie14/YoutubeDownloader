"""Gives every downloaded video in an offline library a thumbnail picture.

1. Saves thumbs/<videoId>.jpg (16:9, 640x360) for every video with a file in map.xml, from
   YouTube's own thumbnail for that video. The app can show these without opening videos.
2. Embeds that picture as cover art in every .mp4 that has none (streams are copied, not
   re-encoded). Hard-linked copies of the same file are re-linked to the new file.

Run build_map.py first (it reads map.xml), and again afterwards to add Thumbnail paths.

Usage: python thumbnails.py <library-root> [--workers 8] [--no-embed]
"""

import argparse
import json
import os
import shutil
import subprocess
import sys
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
from collections import defaultdict
from concurrent.futures import ThreadPoolExecutor

SECTIONS = ["playlists", "channels", "history", "subscribers", "shorts", "mantis"]
# Best first; hq720 is 16:9 without black bars but doesn't exist for every video
SOURCES = ["hq720.jpg", "maxresdefault.jpg", "sddefault.jpg", "hqdefault.jpg", "mqdefault.jpg"]


def download_thumbnail(video_id, path, ffmpeg):
    for name in SOURCES:
        try:
            data = urllib.request.urlopen(f"https://i.ytimg.com/vi/{video_id}/{name}", timeout=30).read()
        except urllib.error.HTTPError:
            continue
        if len(data) < 2000:  # YouTube's grey placeholder
            continue
        source = path + ".src"
        with open(source, "wb") as f:
            f.write(data)
        try:
            # Crop the 4:3 sizes' black bars to 16:9, then scale to 640x360
            subprocess.run(
                [ffmpeg, "-y", "-loglevel", "error", "-i", source,
                 "-vf", "crop=iw:min(ih\\,iw*9/16),scale=640:360", "-q:v", "4", path],
                check=True,
            )
        finally:
            os.remove(source)
        return True
    return False


def has_cover_art(path, ffprobe):
    result = subprocess.run(
        [ffprobe, "-v", "error", "-show_streams", "-of", "json", path],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    streams = json.loads(result.stdout or "{}").get("streams", [])
    return any(s.get("disposition", {}).get("attached_pic") for s in streams), \
        sum(1 for s in streams if s.get("codec_type") == "video")


def embed_cover_art(path, thumb, video_stream_count, ffmpeg, ffprobe, links):
    temp = path + ".art.tmp.mp4"
    subprocess.run(
        [ffmpeg, "-y", "-loglevel", "error", "-i", path, "-i", thumb,
         "-map", "0", "-map", "1", "-c", "copy",
         f"-disposition:v:{video_stream_count}", "attached_pic",
         "-movflags", "+faststart", temp],
        check=True,
    )
    ok, _ = has_cover_art(temp, ffprobe)
    if not ok or os.path.getsize(temp) < os.path.getsize(path):
        os.remove(temp)
        return False

    # Replace the file, then point every hard-linked copy at the new one
    os.replace(temp, path)
    for other in links:
        if other != path:
            link_temp = other + ".art.tmp.mp4"
            os.link(path, link_temp)
            os.replace(link_temp, other)
    return True


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("root")
    parser.add_argument("--workers", type=int, default=8)
    parser.add_argument("--no-embed", action="store_true")
    parser.add_argument("--map", default="map.xml", help="map file to read, e.g. map-mantis.xml")
    parser.add_argument("--ffmpeg", default=shutil.which("ffmpeg") or "ffmpeg")
    parser.add_argument("--ffprobe", default=shutil.which("ffprobe") or "ffprobe")
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    root = args.root
    thumbs_dir = os.path.join(root, "thumbs")
    os.makedirs(thumbs_dir, exist_ok=True)

    library = ET.parse(os.path.join(root, args.map)).getroot()
    videos = [(v.get("Id"), os.path.join(root, v.get("File"))) for v in library.iter("Video") if v.get("File")]

    # Every path of each stored file, so hard links can be kept together
    paths_by_file = defaultdict(list)
    for section in SECTIONS:
        for dirpath, _, filenames in os.walk(os.path.join(root, section)):
            for name in filenames:
                if name.lower().endswith(".mp4") and not name.endswith(".tmp.mp4"):
                    path = os.path.join(dirpath, name)
                    stat = os.stat(path)
                    paths_by_file[(stat.st_dev, stat.st_ino)].append(path)

    def process(item):
        video_id, path = item
        thumb = os.path.join(thumbs_dir, f"{video_id}.jpg")
        result = "had"
        try:
            if not (os.path.isfile(thumb) and os.path.getsize(thumb) > 0):
                result = "new" if download_thumbnail(video_id, thumb, args.ffmpeg) else "none"
            if args.no_embed or not os.path.isfile(thumb):
                return result, False
            ok, video_stream_count = has_cover_art(path, args.ffprobe)
            if ok:
                return result, False
            stat = os.stat(path)
            links = paths_by_file.get((stat.st_dev, stat.st_ino), [path])
            return result, embed_cover_art(path, thumb, video_stream_count, args.ffmpeg, args.ffprobe, links)
        except Exception as ex:
            print(f"FAILED {video_id} {path}: {str(ex).splitlines()[0] if str(ex) else type(ex).__name__}", flush=True)
            return "failed", False

    done = 0
    results = []
    with ThreadPoolExecutor(args.workers) as pool:
        for result in pool.map(process, videos):
            results.append(result)
            done += 1
            if done % 500 == 0:
                print(f"{done}/{len(videos)}", flush=True)

    thumb_results = [r[0] for r in results]
    print(f"{len(videos)} videos: thumbnails {thumb_results.count('new')} new, {thumb_results.count('had')} already there, "
          f"{thumb_results.count('none')} unavailable, {thumb_results.count('failed')} failed; "
          f"cover art embedded in {sum(1 for r in results if r[1])} files")


if __name__ == "__main__":
    sys.exit(main())
