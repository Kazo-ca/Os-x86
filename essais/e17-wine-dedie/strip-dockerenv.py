#!/usr/bin/env python3
"""Retire .dockerenv d'un tar `docker export` (Windows bsdtar ne supporte pas --delete)."""
import shutil
import sys
import tarfile

src = sys.argv[1]
dst = src + ".tmp"
with tarfile.open(src, "r") as tin, tarfile.open(dst, "w") as tout:
    for m in tin.getmembers():
        if m.name.lstrip("./") == "dockerenv":  # lstrip retire aussi le point initial
            continue
        tout.addfile(m, tin.extractfile(m) if m.isfile() else None)
shutil.move(dst, src)
