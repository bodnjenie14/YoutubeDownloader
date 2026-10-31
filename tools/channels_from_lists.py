"""Builds channel lists from videos a profile already has, without downloading anything.

Groups the videos in a profile's lists by the channel that uploaded them and hard-links them
into <profile>/subscribers/<channel>/ with a list XML, the channel picture and the banner, so
the app can show them as channels. Channels with fewer videos than --min-videos are left out.

Usage: python channels_from_lists.py <library-root> <profile> [--min-videos 3]
"""

import argparse
import os
import re
import shutil
import sys
import time
import xml.etree.ElementTree as ET


def escape_file_name(name):
    return re.sub(r'[<>:"/\|?*\x00-\x1f]', "_", name).strip()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("root")
    parser.add_argument("profile")
    parser.add_argument("--min-videos", type=int, default=3)
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    profile_dir = os.path.join(args.root, args.profile)
    target_dir = os.path.join(profile_dir, "subscribers")

    channels = {}
    for dirpath, dirnames, filenames in os.walk(profile_dir):
        dirnames[:] = [d for d in dirnames if d != "subscribers"]
        for name in filenames:
            if not name.lower().endswith(".xml"):
                continue
            for video in ET.parse(os.path.join(dirpath, name)).getroot().iter("Video"):
                author = video.find("Author")
                file_name = video.findtext("FileName")
                channel_id = author.get("ChannelId", "") if author is not None else ""
                path = os.path.join(dirpath, file_name) if file_name else None
                if channel_id and path and os.path.isfile(path):
                    entry = channels.setdefault(channel_id, {"title": author.text or channel_id, "videos": {}})
                    entry["videos"].setdefault(video.get("Id"), (video, path))

    kept = 0
    for channel_id, entry in sorted(channels.items(), key=lambda kv: -len(kv[1]["videos"])):
        if len(entry["videos"]) < args.min_videos:
            continue
        title = entry["title"]
        folder = os.path.join(target_dir, escape_file_name(title))
        os.makedirs(folder, exist_ok=True)

        url = f"https://www.youtube.com/channel/{channel_id}"
        playlist = ET.Element("Playlist", Id=channel_id, Url=url, Count=str(len(entry["videos"])),
                              SavedAt=time.strftime("%Y-%m-%dT%H:%M:%S+00:00", time.gmtime()))
        ET.SubElement(playlist, "Title").text = title
        ET.SubElement(playlist, "Author").text = title
        videos = ET.SubElement(playlist, "Videos")
        for position, (video, path) in enumerate(entry["videos"].values(), 1):
            video.set("Position", str(position))
            videos.append(video)
            target = os.path.join(folder, video.findtext("FileName"))
            if not os.path.exists(target):
                os.link(path, target)

        ET.indent(playlist)
        ET.ElementTree(playlist).write(os.path.join(folder, escape_file_name(title) + ".xml"),
                                       encoding="utf-8", xml_declaration=True)
        for source, picture in ((os.path.join(args.root, "avatars", f"{channel_id}.jpg"), "folder.jpg"),
                                (os.path.join(args.root, "banners", f"{channel_id}.jpg"), "banner.jpg")):
            if os.path.isfile(source):
                shutil.copyfile(source, os.path.join(folder, picture))
        print(f"{title}: {len(entry['videos'])} videos")
        kept += 1

    print(f"{kept} channels, {len(channels) - kept} left out with fewer than {args.min_videos} videos")


if __name__ == "__main__":
    sys.exit(main())
