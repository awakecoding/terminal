#!/usr/bin/env python3
import gzip
import os
import sys
import tarfile
from pathlib import Path

if len(sys.argv) != 4:
    raise SystemExit("Usage: Create-DeterministicTar.py <root> <output.tar.gz> <epoch>")

root = Path(sys.argv[1])
output = Path(sys.argv[2])
epoch = int(sys.argv[3])
executables = {
    "Devolutions.Terminal",
    "dt",
    "dt-pty-host",
    "Install-LinuxDesktopIntegration.sh",
    "devolutions-terminal-x-terminal-emulator",
    "AppRun",
    "postinst",
    "postrm",
}

with output.open("wb") as raw:
    with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=epoch, compresslevel=9) as zipped:
        with tarfile.open(fileobj=zipped, mode="w", format=tarfile.GNU_FORMAT) as archive:
            for path in [root, *sorted(root.rglob("*"), key=lambda p: p.relative_to(root).as_posix())]:
                name = "." if path == root else "./" + path.relative_to(root).as_posix()
                info = archive.gettarinfo(str(path), arcname=name)
                info.uid = 0
                info.gid = 0
                info.uname = ""
                info.gname = ""
                info.mtime = epoch
                if info.isdir():
                    info.mode = 0o755
                elif info.issym():
                    info.mode = 0o777
                elif path.name in executables:
                    info.mode = 0o755
                else:
                    info.mode = 0o644
                if info.isfile():
                    with path.open("rb") as source:
                        archive.addfile(info, source)
                else:
                    archive.addfile(info)
