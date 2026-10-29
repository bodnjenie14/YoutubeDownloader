"""Builds map.xml and map.csv at the root of an offline library.

It is meant to be served as-is by a file server: every path is relative to the
root with forward slashes, and each video is listed once (with its size) so a
client can download it once however many lists it is in.

The library root holds one folder per section (playlists, channels, history,
subscribers), each with one folder per list containing the list's XML and videos.
Channel pictures, if present, live in avatars/ (see the CLI's avatars command).

map.xml lists every list by section, and every video once with the lists it
appears in, so a player can link videos, lists, channels and pictures together.
map.csv is the list overview as a spreadsheet.

A profile builds a separate map for another child's tablet from that child's own section
folder (e.g. mantis/, filled with derive_list.py and the CLI): map-<profile>.xml/.csv.

Usage: python build_map.py <library-root> [--profile NAME]
"""

import argparse
import csv
import os
import sys
import time
import xml.etree.ElementTree as ET

SECTIONS = ["playlists", "channels", "history", "subscribers", "shorts"]


def relative(path, root):
    return os.path.relpath(path, root).replace(os.sep, "/")


def file_key(path):
    stat = os.stat(path)
    return (stat.st_dev, stat.st_ino)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("root")
    parser.add_argument("--profile", help="build map-<profile>.xml from the <profile>/ section only")
    args = parser.parse_args()
    root = args.root
    sections = SECTIONS
    if args.profile:
        # A profile folder either holds lists directly (one section) or the usual section folders
        nested = [f"{args.profile}/{s}" for s in SECTIONS if os.path.isdir(os.path.join(root, args.profile, s))]
        sections = nested or [args.profile]
    map_name = f"map-{args.profile}" if args.profile else "map"
    avatars_dir = os.path.join(root, "avatars")

    library = ET.Element("Library", Profile=args.profile or "", Root=os.path.abspath(root), BuiltAt=time.strftime("%Y-%m-%dT%H:%M:%S"))
    sections_element = ET.SubElement(library, "Sections")
    videos = {}
    rows = []
    stored_files = set()

    for section in sections:
        section_dir = os.path.join(root, section)
        if not os.path.isdir(section_dir):
            continue

        section_element = ET.SubElement(sections_element, "Section", Name=section.split("/")[-1], Folder=section)

        xml_paths = []
        for dirpath, _, filenames in os.walk(section_dir):
            xml_paths += [os.path.join(dirpath, f) for f in filenames if f.lower().endswith(".xml")]

        for xml_path in sorted(xml_paths, key=str.lower):
            try:
                playlist = ET.parse(xml_path).getroot()
            except ET.ParseError:
                continue
            if playlist.tag != "Playlist":
                continue

            folder = os.path.dirname(xml_path)
            list_id = playlist.get("Id", "")
            title = playlist.findtext("Title") or os.path.splitext(os.path.basename(xml_path))[0]
            entries = list(playlist.iter("Video"))

            downloaded = 0
            size = 0
            for entry in entries:
                file_name = entry.findtext("FileName")
                path = os.path.join(folder, file_name) if file_name else None
                exists = bool(path and os.path.isfile(path) and os.path.getsize(path) > 0)
                if exists:
                    downloaded += 1
                    size += os.path.getsize(path)
                    stored_files.add(file_key(path))

                video_id = entry.get("Id")
                author = entry.find("Author")
                video = videos.setdefault(video_id, {
                    "title": entry.findtext("Title") or "",
                    "author": author.text if author is not None and author.text else "",
                    "channel_id": author.get("ChannelId", "") if author is not None else "",
                    "duration": entry.findtext("Duration") or "",
                    "file": None,
                    "lists": [],
                })
                if not video["channel_id"] and author is not None:
                    video["channel_id"] = author.get("ChannelId", "")
                if exists and video["file"] is None:
                    video["file"] = relative(path, root)
                video["lists"].append((relative(xml_path, root), entry.get("Position", ""), exists))

            picture = None
            if os.path.isfile(os.path.join(folder, "folder.jpg")):
                picture = relative(os.path.join(folder, "folder.jpg"), root)
            elif os.path.isfile(os.path.join(avatars_dir, f"{list_id}.jpg")):
                picture = relative(os.path.join(avatars_dir, f"{list_id}.jpg"), root)

            list_element = ET.SubElement(
                section_element, "List",
                Id=list_id, Url=playlist.get("Url", ""),
                Folder=relative(folder, root), Xml=relative(xml_path, root),
                Count=str(len(entries)), Downloaded=str(downloaded), SizeMB=str(round(size / 1e6)),
            )
            ET.SubElement(list_element, "Title").text = title
            ET.SubElement(list_element, "Author").text = playlist.findtext("Author") or ""
            if picture:
                ET.SubElement(list_element, "Picture").text = picture
            # Channel banner (see banners.py)
            if os.path.isfile(os.path.join(folder, "banner.jpg")):
                ET.SubElement(list_element, "Banner").text = relative(os.path.join(folder, "banner.jpg"), root)

            rows.append([section, title, len(entries), downloaded, round(size / 1e6),
                         relative(folder, root), relative(xml_path, root), list_id, playlist.get("Url", "")])

    videos_element = ET.SubElement(library, "Videos", Count=str(len(videos)))
    for video_id, video in sorted(videos.items(), key=lambda kv: kv[1]["title"].lower()):
        attributes = {"Id": video_id, "ChannelId": video["channel_id"], "Duration": video["duration"]}
        if video["file"]:
            attributes["File"] = video["file"]
            attributes["Bytes"] = str(os.path.getsize(os.path.join(root, video["file"])))
        # Video picture (see thumbnails.py)
        if os.path.isfile(os.path.join(root, "thumbs", f"{video_id}.jpg")):
            attributes["Thumbnail"] = f"thumbs/{video_id}.jpg"
        avatar = os.path.join(avatars_dir, f"{video['channel_id']}.jpg")
        if video["channel_id"] and os.path.isfile(avatar):
            attributes["ChannelPicture"] = relative(avatar, root)

        video_element = ET.SubElement(videos_element, "Video", **attributes)
        ET.SubElement(video_element, "Title").text = video["title"]
        ET.SubElement(video_element, "Author").text = video["author"]
        for xml_rel, position, exists in video["lists"]:
            ET.SubElement(video_element, "In", List=xml_rel, Position=position, Downloaded=str(exists).lower())

    library.set("Videos", str(len(videos)))
    library.set("Downloaded", str(sum(1 for v in videos.values() if v["file"])))
    # What a client needs to fetch everything once (each video stored once, however many lists it is in)
    library.set("Bytes", str(sum(os.path.getsize(os.path.join(root, v["file"])) for v in videos.values() if v["file"])))

    ET.indent(library)
    ET.ElementTree(library).write(os.path.join(root, f"{map_name}.xml"), encoding="utf-8", xml_declaration=True)

    with open(os.path.join(root, f"{map_name}.csv"), "w", newline="", encoding="utf-8-sig") as f:
        writer = csv.writer(f)
        writer.writerow(["Section", "List", "Videos", "Downloaded", "Size (MB)", "Folder", "XML", "YouTube ID", "Link"])
        writer.writerows(rows)

    print(f"{len(rows)} lists, {len(videos)} unique videos "
          f"({sum(1 for v in videos.values() if v['file'])} downloaded, {len(stored_files)} files stored) "
          f"-> {os.path.join(root, map_name + '.xml')}, {map_name}.csv")


if __name__ == "__main__":
    main()
