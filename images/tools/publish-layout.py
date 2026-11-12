#!/usr/bin/env python3
"""Lays the packs out as the hosts serve them (milestone 0, work item 4; decision 202 A).

Input: a directory with one pack per image (packs/<image>/manifest.json, chunks, sources...).
Output: site/images/<image>/<version>/<the files of the pack> and site/images/index.json
({"format": 1, "images": [{"image", "version", "manifest": "images/<image>/<version>/manifest.json",
"manifestSha256", "size"}]}). Every pack is checked with pack-image.py first. With
--max-file-bytes (GitHub Pages: 100 MiB) a pack with a larger file is refused, and with
--max-total-bytes the whole site is.

    python -I images/tools/publish-layout.py --packs packs --site site [--max-file-bytes N] [--max-total-bytes N]
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
PACK_TOOL = HERE / "pack-image.py"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--packs", type=Path, required=True)
    parser.add_argument("--site", type=Path, required=True)
    parser.add_argument("--max-file-bytes", type=int, default=0, help="refuse a file larger than this (0: no limit)")
    parser.add_argument("--max-total-bytes", type=int, default=0, help="refuse a site larger than this (0: no limit)")
    args = parser.parse_args()

    images = []
    total = 0
    for pack in sorted(path for path in args.packs.iterdir() if (path / "manifest.json").is_file()):
        subprocess.run([sys.executable, "-I", str(PACK_TOOL), "check", str(pack)], check=True)
        manifest = json.loads((pack / "manifest.json").read_text(encoding="utf-8"))
        target = args.site / "images" / manifest["image"] / manifest["version"]
        if target.exists():
            raise SystemExit(f"{target} already exists: an image version is published once")
        target.mkdir(parents=True)
        for file in sorted(path for path in pack.iterdir() if path.is_file()):
            size = file.stat().st_size
            if args.max_file_bytes and size > args.max_file_bytes:
                raise SystemExit(f"{file} is {size} bytes, more than the {args.max_file_bytes} this host accepts")
            shutil.copyfile(file, target / file.name)
            total += size
        images.append({
            "image": manifest["image"],
            "version": manifest["version"],
            "manifest": f"images/{manifest['image']}/{manifest['version']}/manifest.json",
            "manifestSha256": (pack / "manifest.sha256").read_text(encoding="utf-8").split()[0],
            "size": manifest["size"],
        })
        print(f"  {manifest['image']} {manifest['version']}: {manifest['size']} bytes -> {target}")
    if not images:
        raise SystemExit(f"no pack found under {args.packs}")
    if args.max_total_bytes and total > args.max_total_bytes:
        raise SystemExit(f"the site is {total} bytes, more than the {args.max_total_bytes} this host accepts")
    index = {"format": 1, "images": images}
    (args.site / "images" / "index.json").write_text(json.dumps(index, indent=2) + "\n", encoding="utf-8", newline="\n")
    (args.site / ".nojekyll").write_text("", encoding="utf-8")
    print(f"{len(images)} image(s), {total} bytes")
    return 0


if __name__ == "__main__":
    sys.exit(main())
