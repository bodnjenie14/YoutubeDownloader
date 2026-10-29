"""Downloads playlists the CLI can't read itself (private ones: YoutubeExplode no longer opens
them even when signed in). yt-dlp reads the playlist with a browser cookies.txt, then the CLI
downloads its videos as a list of links, and the list's XML gets the real playlist ID and URL.

Lines in the playlists file look like "Name > https://www.youtube.com/playlist?list=...".

Usage: python ytdlp_lists.py <playlists.txt> <section-folder> --cli <exe> --cookies <cookies.txt>
       [--work <dir>] [--max-minutes 60] [--dry-run] [--yt-dlp <exe>]
"""

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET


def find_yt_dlp():
    links = os.path.join(os.environ.get("LOCALAPPDATA", ""), "Microsoft", "WinGet", "Links", "yt-dlp.exe")
    return shutil.which("yt-dlp") or (links if os.path.isfile(links) else None)


def escape_file_name(name):
    # Same characters the CLI replaces in folder names
    return re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", name).strip()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("playlists")
    parser.add_argument("section")
    parser.add_argument("--cli", required=True)
    parser.add_argument("--cookies", required=True)
    parser.add_argument("--work", default=os.path.join(os.environ.get("TEMP", "."), "ytdlp-lists"))
    parser.add_argument("--max-minutes", type=int, default=60)
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--yt-dlp", default=find_yt_dlp())
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    os.makedirs(args.work, exist_ok=True)

    for line in open(args.playlists, encoding="utf-8"):
        match = re.search(r"https?://\S+", line)
        if not match:
            continue
        url = match.group(0)

        result = subprocess.run(
            [args.yt_dlp, "--cookies", args.cookies, "--flat-playlist", "-J", url],
            capture_output=True, text=True, encoding="utf-8", errors="replace",
        )
        if result.returncode != 0:
            print(f"===== {line.strip()}: yt-dlp failed: {result.stderr.strip().splitlines()[-1:]}", flush=True)
            continue

        playlist = json.loads(result.stdout)
        title = playlist.get("title") or line.split(" > ")[0].strip()
        ids = [e["id"] for e in playlist.get("entries", []) if e.get("id")]
        print(f"===== {title}: {len(ids)} videos", flush=True)

        # The CLI names the folder and list after the .txt file
        links_path = os.path.join(args.work, escape_file_name(title) + ".txt")
        with open(links_path, "w", encoding="utf-8") as f:
            f.writelines(f"https://www.youtube.com/watch?v={i}\n" for i in ids)

        command = [args.cli, links_path, args.section, "--max-minutes", str(args.max_minutes)]
        if args.dry_run:
            command.append("--dry-run")
        cli = subprocess.run(command, stdin=subprocess.DEVNULL, capture_output=True,
                             text=True, encoding="utf-8", errors="replace")
        print(cli.stdout.rstrip(), flush=True)

        if args.dry_run:
            continue

        xml_path = os.path.join(args.section, escape_file_name(title), escape_file_name(title) + ".xml")
        if os.path.isfile(xml_path):
            tree = ET.parse(xml_path)
            root = tree.getroot()
            root.set("Id", playlist.get("id") or "")
            root.set("Url", playlist.get("webpage_url") or url)
            root.find("Title").text = title
            root.find("Author").text = playlist.get("channel") or playlist.get("uploader") or ""
            tree.write(xml_path, encoding="utf-8", xml_declaration=True)


if __name__ == "__main__":
    sys.exit(main())
