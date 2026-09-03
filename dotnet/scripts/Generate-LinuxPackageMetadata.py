#!/usr/bin/env python3
"""Generate deterministic inventory and SPDX metadata for a staged Linux artifact."""

from __future__ import annotations

import datetime
import hashlib
import json
import os
import sys
from pathlib import Path


def file_license(path: str) -> str:
    if path.endswith("/appimage-runtime") or path.endswith(
        "/APPIMAGE-RUNTIME-LICENSE.txt"
    ):
        return "LicenseRef-AppImage-Runtime"
    if path.endswith("/THIRD-PARTY-NOTICES-NOTO-EMOJI.txt"):
        return "OFL-1.1"
    if path.endswith("/Devolutions.Terminal"):
        return "MIT AND OFL-1.1"
    if path.endswith("/LICENSE"):
        return "MIT"
    return "NOASSERTION"


def main() -> int:
    if len(sys.argv) != 8:
        print(
            "usage: Generate-LinuxPackageMetadata.py "
            "<root> <name> <version> <rid> <license> <artifact-kind> <epoch>",
            file=sys.stderr,
        )
        return 64

    root = Path(sys.argv[1]).resolve()
    name, version, rid, license_id, artifact_kind, epoch_text = sys.argv[2:]
    epoch = int(epoch_text)
    doc = root / "usr/share/doc" / name
    inventory_path = doc / "inventory.json"
    sbom_path = doc / "sbom.spdx.json"
    excluded_metadata = {inventory_path, sbom_path}
    executables = {
        "Devolutions.Terminal",
        "dt",
        "dt-pty-host",
        "Install-LinuxDesktopIntegration.sh",
        "devolutions-terminal-x-terminal-emulator",
        "AppRun",
    }

    inventory_files = []
    for path in sorted(root.rglob("*"), key=lambda item: item.relative_to(root).as_posix()):
        if path in excluded_metadata or path.is_dir():
            continue
        relative = path.relative_to(root).as_posix()
        if path.is_symlink():
            data = os.readlink(path).encode()
            kind = "symlink"
            mode = "0777"
        else:
            data = path.read_bytes()
            kind = "file"
            mode = "0755" if path.name in executables else "0644"
        inventory_files.append(
            {
                "path": "/" + relative,
                "type": kind,
                "mode": mode,
                "size": len(data),
                "sha256": hashlib.sha256(data).hexdigest(),
            }
        )

    inventory = {
        "schemaVersion": 1,
        "name": name,
        "version": version,
        "rid": rid,
        "artifactKind": artifact_kind,
        "files": inventory_files,
    }
    inventory_path.write_text(
        json.dumps(inventory, indent=2, sort_keys=True) + "\n", encoding="utf-8"
    )

    regular_files = [
        path
        for path in sorted(root.rglob("*"), key=lambda item: item.relative_to(root).as_posix())
        if path.is_file() and not path.is_symlink() and path != sbom_path
    ]
    spdx_files = []
    for index, path in enumerate(regular_files, 1):
        relative = path.relative_to(root).as_posix()
        data = path.read_bytes()
        sha1 = hashlib.sha1(data).hexdigest()
        sha256 = hashlib.sha256(data).hexdigest()
        concluded = file_license("./" + relative)
        spdx_files.append(
            {
                "SPDXID": f"SPDXRef-File-{index}",
                "fileName": "./" + relative,
                "checksums": [
                    {"algorithm": "SHA1", "checksumValue": sha1},
                    {"algorithm": "SHA256", "checksumValue": sha256},
                ],
                "licenseConcluded": concluded,
                "licenseInfoInFiles": [concluded],
                "copyrightText": "NOASSERTION",
            }
        )

    verification_input = "".join(
        sorted(
            checksum["checksumValue"]
            for item in spdx_files
            for checksum in item["checksums"]
            if checksum["algorithm"] == "SHA1"
        )
    )
    verification_code = hashlib.sha1(verification_input.encode("ascii")).hexdigest()
    excluded_file = "./" + sbom_path.relative_to(root).as_posix()
    safe_kind = "".join(
        character if character.isalnum() or character in ".-" else "-"
        for character in artifact_kind
    )
    sbom = {
        "SPDXID": "SPDXRef-DOCUMENT",
        "spdxVersion": "SPDX-2.3",
        "dataLicense": "CC0-1.0",
        "name": f"{name}-{version}-{rid}-{safe_kind}",
        "documentNamespace": (
            f"https://github.com/Devolutions/devolutions-terminal/sbom/"
            f"{version}/{rid}/{safe_kind}"
        ),
        "documentDescribes": ["SPDXRef-Package"],
        "creationInfo": {
            "created": datetime.datetime.fromtimestamp(
                epoch, datetime.timezone.utc
            ).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "creators": ["Tool: Generate-LinuxPackageMetadata.py"],
        },
        "packages": [
            {
                "SPDXID": "SPDXRef-Package",
                "name": name,
                "versionInfo": version,
                "downloadLocation": "NOASSERTION",
                "filesAnalyzed": True,
                "packageVerificationCode": {
                    "packageVerificationCodeValue": verification_code,
                    "packageVerificationCodeExcludedFiles": [excluded_file],
                },
                "licenseConcluded": license_id,
                "licenseDeclared": license_id,
                "licenseInfoFromFiles": sorted(
                    {
                        license_value
                        for item in spdx_files
                        for license_value in item["licenseInfoInFiles"]
                    }
                ),
                "copyrightText": "NOASSERTION",
            }
        ],
        "files": spdx_files,
        "relationships": [
            {
                "spdxElementId": "SPDXRef-Package",
                "relationshipType": "CONTAINS",
                "relatedSpdxElement": item["SPDXID"],
            }
            for item in spdx_files
        ],
    }
    if "LicenseRef-AppImage-Runtime" in license_id:
        runtime_notice = doc / "APPIMAGE-RUNTIME-LICENSE.txt"
        sbom["hasExtractedLicensingInfos"] = [
            {
                "licenseId": "LicenseRef-AppImage-Runtime",
                "name": "AppImage type-2 runtime aggregate licensing",
                "extractedText": runtime_notice.read_text(encoding="utf-8"),
                "seeAlsos": [
                    "https://github.com/AppImage/type2-runtime",
                ],
            }
        ]
    sbom_path.write_text(
        json.dumps(sbom, indent=2, sort_keys=True) + "\n", encoding="utf-8"
    )
    os.chmod(inventory_path, 0o644)
    os.chmod(sbom_path, 0o644)
    os.utime(inventory_path, (epoch, epoch))
    os.utime(sbom_path, (epoch, epoch))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
