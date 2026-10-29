"""Makes a new list from videos already in the library, without copying them.

Videos are taken from one or more list XMLs (optionally only titles matching a pattern),
hard-linked into <parent>/<title>/ and written to <title>.xml in the same format as the
other lists. Use it to build lists for another profile, e.g. a second child's tablet.

Usage: python derive_list.py <parent-folder> <title> <source.xml> [<source.xml> ...]
       [--match REGEX] [--id ID]
"""

import argparse
import os
import re
import sys
import time
import xml.etree.ElementTree as ET


def escape_file_name(name):
    return re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", name).strip()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("parent")
    parser.add_argument("title")
    parser.add_argument("sources", nargs="+")
    parser.add_argument("--match", help="only videos whose title or channel matches (case-insensitive)")
    parser.add_argument("--id", help="list ID (default: the title)")
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    folder = os.path.join(args.parent, escape_file_name(args.title))
    os.makedirs(folder, exist_ok=True)
    pattern = re.compile(args.match, re.I) if args.match else None

    videos = []
    seen = set()
    for source in args.sources:
        source_dir = os.path.dirname(source)
        for video in ET.parse(source).getroot().iter("Video"):
            title = video.findtext("Title") or ""
            text = f"{title} {video.findtext('Author') or ''}"
            if video.get("Id") in seen or (pattern and not pattern.search(text)):
                continue
            seen.add(video.get("Id"))

            file_name = video.findtext("FileName")
            source_path = os.path.join(source_dir, file_name) if file_name else None
            if source_path and os.path.isfile(source_path):
                target = os.path.join(folder, file_name)
                if not os.path.exists(target):
                    os.link(source_path, target)
            else:
                # Listed without a file, like any video that isn't downloaded
                file_name_element = video.find("FileName")
                if file_name_element is not None:
                    video.remove(file_name_element)
            videos.append(video)

    root = ET.Element("Playlist", Id=args.id or args.title, Url=" ".join(args.sources),
                      Count=str(len(videos)), SavedAt=time.strftime("%Y-%m-%dT%H:%M:%S"))
    ET.SubElement(root, "Title").text = args.title
    ET.SubElement(root, "Author").text = ""
    videos_element = ET.SubElement(root, "Videos")
    for position, video in enumerate(videos, 1):
        video.set("Position", str(position))
        videos_element.append(video)

    ET.indent(root)
    ET.ElementTree(root).write(os.path.join(folder, escape_file_name(args.title) + ".xml"),
                               encoding="utf-8", xml_declaration=True)
    print(f"'{args.title}': {len(videos)} videos -> {folder}")


if __name__ == "__main__":
    sys.exit(main())
