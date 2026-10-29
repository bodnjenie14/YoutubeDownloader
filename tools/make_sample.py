"""Builds a small sample profile from the full library, for testing the app end to end.

Picks a few lists per section (channel lists with a banner and picture first) and a few
short downloaded videos from each, hard-links them into sample/<section>/<list>/ with a
list XML and the list's pictures, then the sample map is built with
build_map.py --profile sample (map-sample.xml). Videos are hard links, so it takes no
extra space.

Usage: python make_sample.py <library-root> [--lists 3] [--videos 6] [--max-minutes 8]
"""

import argparse
import os
import shutil
import sys
import xml.etree.ElementTree as ET

SECTIONS = ["playlists", "channels", "history", "subscribers", "shorts"]


def minutes(duration):
    total = 0.0
    for part in (duration or "0").split(":"):
        total = total * 60 + float(part)
    return total / 60


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("root")
    parser.add_argument("--lists", type=int, default=3, help="lists per section")
    parser.add_argument("--videos", type=int, default=6, help="videos per list")
    parser.add_argument("--max-minutes", type=float, default=8)
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    root = args.root
    sample_dir = os.path.join(root, "sample")
    if os.path.isdir(sample_dir):
        shutil.rmtree(sample_dir)  # only ever holds links and copies made by this script

    library = ET.parse(os.path.join(root, "map.xml")).getroot()
    total_bytes = 0
    total_videos = 0

    for section in library.iter("Section"):
        name = section.get("Name")
        if name not in SECTIONS:
            continue

        def rank(list_element):
            has_art = list_element.find("Banner") is not None, list_element.find("Picture") is not None
            return (not all(has_art), not any(has_art), -int(list_element.get("Downloaded")))

        picked = 0
        for list_element in sorted(section.findall("List"), key=rank):
            if picked >= args.lists:
                break
            source_xml = os.path.join(root, list_element.get("Xml"))
            source_dir = os.path.dirname(source_xml)
            playlist = ET.parse(source_xml).getroot()

            videos = []
            for video in playlist.iter("Video"):
                file_name = video.findtext("FileName")
                path = os.path.join(source_dir, file_name) if file_name else None
                if path and os.path.isfile(path) and minutes(video.findtext("Duration")) <= args.max_minutes:
                    videos.append((video, path))
                if len(videos) >= args.videos:
                    break
            if len(videos) < 2:
                continue

            target_dir = os.path.join(sample_dir, name, os.path.basename(source_dir))
            os.makedirs(target_dir, exist_ok=True)
            for picture in ("folder.jpg", "banner.jpg"):
                if os.path.isfile(os.path.join(source_dir, picture)):
                    shutil.copyfile(os.path.join(source_dir, picture), os.path.join(target_dir, picture))

            videos_element = playlist.find("Videos")
            for child in list(videos_element):
                videos_element.remove(child)
            for position, (video, path) in enumerate(videos, 1):
                video.set("Position", str(position))
                videos_element.append(video)
                os.link(path, os.path.join(target_dir, video.findtext("FileName")))
                total_bytes += os.path.getsize(path)
                total_videos += 1
            playlist.set("Count", str(len(videos)))

            ET.indent(playlist)
            ET.ElementTree(playlist).write(os.path.join(target_dir, os.path.basename(source_xml)),
                                           encoding="utf-8", xml_declaration=True)
            print(f"{name}: {playlist.findtext('Title')} ({len(videos)} videos)")
            picked += 1

    print(f"sample: {total_videos} videos, {total_bytes / 1e6:.0f} MB")


if __name__ == "__main__":
    sys.exit(main())
