#!/usr/bin/env python3
"""Offline structural and semantic validation for generated SPDX 2.3 JSON."""

from __future__ import annotations

import hashlib
import json
import re
import sys
from pathlib import Path


def require(mapping: dict, key: str, expected_type: type):
    value = mapping.get(key)
    if not isinstance(value, expected_type):
        raise ValueError(f"{key} must be {expected_type.__name__}")
    return value


def validate(path: Path, package_root: Path) -> None:
    path = path.resolve()
    package_root = package_root.resolve()
    try:
        sbom_relative = path.relative_to(package_root).as_posix()
    except ValueError as error:
        raise ValueError("SBOM must be contained by the extracted package root") from error

    document = json.loads(path.read_text(encoding="utf-8"))
    if document.get("spdxVersion") != "SPDX-2.3":
        raise ValueError("spdxVersion must be SPDX-2.3")
    if document.get("dataLicense") != "CC0-1.0":
        raise ValueError("dataLicense must be CC0-1.0")
    for key in ("SPDXID", "name", "documentNamespace"):
        require(document, key, str)
    creation = require(document, "creationInfo", dict)
    require(creation, "created", str)
    require(creation, "creators", list)

    packages = require(document, "packages", list)
    files = require(document, "files", list)
    if len(packages) != 1:
        raise ValueError("exactly one package is required")
    package = packages[0]
    for key in (
        "SPDXID",
        "name",
        "downloadLocation",
        "licenseConcluded",
        "licenseDeclared",
        "copyrightText",
    ):
        require(package, key, str)
    if package.get("filesAnalyzed") is not True:
        raise ValueError("generated package must analyze files")
    require(package, "licenseInfoFromFiles", list)
    verification = require(package, "packageVerificationCode", dict)
    expected_verification = require(
        verification, "packageVerificationCodeValue", str
    )
    excluded_files = require(
        verification, "packageVerificationCodeExcludedFiles", list
    )
    expected_exclusions = ["./" + sbom_relative]
    if excluded_files != expected_exclusions:
        raise ValueError(
            f"package verification exclusions must be exactly {expected_exclusions}"
        )

    ids = {document["SPDXID"], package["SPDXID"]}
    sha1_values: list[str] = []
    licenses: set[str] = set()
    file_names: set[str] = set()
    for file_entry in files:
        for key in (
            "SPDXID",
            "fileName",
            "licenseConcluded",
            "copyrightText",
        ):
            require(file_entry, key, str)
        license_info = require(file_entry, "licenseInfoInFiles", list)
        if not license_info or not all(isinstance(item, str) for item in license_info):
            raise ValueError(f"{file_entry['SPDXID']} has invalid licenseInfoInFiles")
        licenses.update(license_info)
        file_name = file_entry["fileName"]
        if (
            not file_name.startswith("./")
            or Path(file_name).is_absolute()
            or "\\" in file_name
            or file_name == "./"
            or ".." in file_name[2:].split("/")
        ):
            raise ValueError(
                f"{file_entry['SPDXID']} fileName must be a package-relative ./ path"
            )
        if file_name in file_names:
            raise ValueError(f"duplicate SPDX fileName: {file_name}")
        file_names.add(file_name)
        disk_path = package_root.joinpath(*file_name[2:].split("/"))
        if not disk_path.is_file() or disk_path.is_symlink():
            raise ValueError(f"{file_name} is not a regular package file")
        if file_entry["SPDXID"] in ids:
            raise ValueError(f"duplicate SPDX identifier: {file_entry['SPDXID']}")
        ids.add(file_entry["SPDXID"])
        checksums = require(file_entry, "checksums", list)
        sha1 = next(
            (
                item.get("checksumValue")
                for item in checksums
                if item.get("algorithm") == "SHA1"
            ),
            None,
        )
        if not isinstance(sha1, str) or not re.fullmatch(r"[0-9a-f]{40}", sha1):
            raise ValueError(f"{file_entry['SPDXID']} has no valid SHA1 checksum")
        sha256 = next(
            (
                item.get("checksumValue")
                for item in checksums
                if item.get("algorithm") == "SHA256"
            ),
            None,
        )
        data = disk_path.read_bytes()
        if sha1 != hashlib.sha1(data).hexdigest():
            raise ValueError(f"{file_name} SHA1 does not match extracted package content")
        if sha256 != hashlib.sha256(data).hexdigest():
            raise ValueError(f"{file_name} SHA256 does not match extracted package content")
        sha1_values.append(sha1)

    actual_regular_files = {
        "./" + item.relative_to(package_root).as_posix()
        for item in package_root.rglob("*")
        if item.is_file() and not item.is_symlink()
    }
    expected_regular_files = actual_regular_files - set(excluded_files)
    if file_names != expected_regular_files:
        missing = sorted(expected_regular_files - file_names)
        extra = sorted(file_names - expected_regular_files)
        raise ValueError(
            f"SPDX files do not match extracted regular files; "
            f"missing={missing}, extra={extra}"
        )

    calculated = hashlib.sha1("".join(sorted(sha1_values)).encode("ascii")).hexdigest()
    if calculated != expected_verification:
        raise ValueError("package verification code does not match file checksums")
    if sorted(licenses) != sorted(package["licenseInfoFromFiles"]):
        raise ValueError("licenseInfoFromFiles does not match analyzed files")
    if "OFL-1.1" in licenses:
        for field in ("licenseConcluded", "licenseDeclared"):
            if "OFL-1.1" not in package[field]:
                raise ValueError(f"package {field} omits bundled OFL-1.1 content")
    license_refs = {
        value
        for value in licenses
        if value.startswith("LicenseRef-")
    }
    extracted = {
        item.get("licenseId")
        for item in document.get("hasExtractedLicensingInfos", [])
        if isinstance(item, dict)
        and isinstance(item.get("extractedText"), str)
        and item["extractedText"].strip()
    }
    if not license_refs.issubset(extracted):
        raise ValueError(
            f"missing extracted licensing information for {sorted(license_refs - extracted)}"
        )

    relationships = require(document, "relationships", list)
    contained = {
        item.get("relatedSpdxElement")
        for item in relationships
        if item.get("spdxElementId") == package["SPDXID"]
        and item.get("relationshipType") == "CONTAINS"
    }
    file_ids = {item["SPDXID"] for item in files}
    if contained != file_ids:
        raise ValueError("package CONTAINS relationships do not match files")
    if document.get("documentDescribes") != [package["SPDXID"]]:
        raise ValueError("documentDescribes must identify the generated package")


def main() -> int:
    if len(sys.argv) != 3:
        print(
            f"usage: {Path(sys.argv[0]).name} <sbom.spdx.json> <package-root>",
            file=sys.stderr,
        )
        return 64
    try:
        validate(Path(sys.argv[1]), Path(sys.argv[2]))
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"invalid SPDX document: {error}", file=sys.stderr)
        return 1
    print(f"Validated SPDX 2.3 document: {sys.argv[1]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
