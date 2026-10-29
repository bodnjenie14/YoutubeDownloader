"""Downloads more playlists until the library's estimated compressed size hits a budget.

Playlists are taken in file order ("Name > link" lines), with the ones named in
--last moved to the end. Each is downloaded with the CLI and --max-minutes. Before
each playlist, the size after compression is estimated as:

    size when started + (bytes added since) * compression ratio

and filling stops once that reaches the budget minus the margin. The compression
ratio comes from the last finalize run (_finalize-log.txt) unless --ratio is given.
Run finalize.py afterwards to compress what was added.

Usage: python fill_budget.py <library-root> <playlists.txt> --cli <exe> --settings <Settings.dat>
       [--budget-gb 75] [--margin-gb 3] [--max-minutes 60] [--ratio R] [--last name ...]
"""

import argparse
import os
import re
import subprocess
import sys
import time


SECTIONS = ["playlists", "channels", "history", "subscribers", "shorts"]


def unique_bytes(root):
    seen = set()
    total = 0
    walks = (os.walk(os.path.join(root, s)) for s in SECTIONS)
    for dirpath, _, filenames in (entry for walk in walks for entry in walk):
        for name in filenames:
            if not name.lower().endswith(".mp4"):
                continue
            stat = os.stat(os.path.join(dirpath, name))
            key = (stat.st_dev, stat.st_ino)
            if key not in seen:
                seen.add(key)
                total += stat.st_size
    return total


def ratio_from_finalize_log(root):
    """(raw bytes of unique videos - bytes saved by compression) / raw bytes of unique videos."""
    path = os.path.join(root, "_finalize-log.txt")
    if not os.path.isfile(path):
        return None
    text = open(path, encoding="utf-8").read()
    # Each run starts with a totals line. Later runs mostly skip videos compressed earlier,
    # which makes their ratio look close to 1, so use the run that compressed the most
    ratios = []
    for run in re.split(r"(?m)^(?=\S+ \d+ videos, \d+ files, )", text):
        totals = re.findall(r"\d+ videos, \d+ files, ([\d.]+) GB", run)
        progress = re.findall(r"compression ([\d.]+) GB, duplicates ([\d.]+) GB", run)
        if not totals or not progress or float(progress[-1][0]) == 0:
            continue
        total, (compression, duplicates) = float(totals[-1]), progress[-1]
        raw_unique = total - float(duplicates)
        if raw_unique > 0:
            ratios.append((raw_unique - float(compression)) / raw_unique)
    return min(ratios, default=None)


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser()
    parser.add_argument("root")
    parser.add_argument("playlists")
    parser.add_argument("--cli", required=True)
    parser.add_argument("--settings", required=True)
    parser.add_argument("--section", default="playlists")
    parser.add_argument("--budget-gb", type=float, default=75)
    parser.add_argument("--margin-gb", type=float, default=3)
    parser.add_argument("--max-minutes", type=int, default=60)
    parser.add_argument("--ratio", type=float)
    parser.add_argument("--last", nargs="*", default=[])
    args = parser.parse_args()

    ratio = args.ratio or ratio_from_finalize_log(args.root) or 0.65
    limit = (args.budget_gb - args.margin_gb) * 1e9

    entries = []
    for line in open(args.playlists, encoding="utf-8"):
        match = re.search(r"https?://\S+", line)
        if match:
            name = line.split(" > ")[0].strip()
            entries.append((name, match.group(0)))
    entries.sort(key=lambda e: any(last.lower() in e[0].lower() for last in args.last))

    start = unique_bytes(args.root)
    print(f"{time.strftime('%H:%M:%S')} start {start / 1e9:.1f} GB, ratio {ratio:.2f}, "
          f"stop at ~{limit / 1e9:.0f} GB, {len(entries)} playlists", flush=True)

    for i, (name, url) in enumerate(entries, 1):
        added = unique_bytes(args.root) - start
        estimate = start + added * ratio
        if estimate >= limit:
            print(f"{time.strftime('%H:%M:%S')} budget reached (~{estimate / 1e9:.1f} GB), stopping before '{name}'",
                  flush=True)
            break

        print(f"{time.strftime('%H:%M:%S')} [{i}/{len(entries)}] {name} (estimate so far ~{estimate / 1e9:.1f} GB)",
              flush=True)
        result = subprocess.run(
            [args.cli, url, os.path.join(args.root, args.section),
             "--settings", args.settings, "--max-minutes", str(args.max_minutes)],
            stdin=subprocess.DEVNULL, capture_output=True, text=True, encoding="utf-8", errors="replace",
        )
        summary = [l for l in result.stdout.splitlines() if "Finished" in l or "skipping" in l]
        for line in summary or result.stdout.strip().splitlines()[-1:] or ["(no output)"]:
            print(f"    {line}", flush=True)

    end = unique_bytes(args.root)
    print(f"{time.strftime('%H:%M:%S')} done: {end / 1e9:.1f} GB on disk, "
          f"~{(start + (end - start) * ratio) / 1e9:.1f} GB after compressing the new videos", flush=True)


if __name__ == "__main__":
    sys.exit(main())
