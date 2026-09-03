#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
dotnet_root="$(cd -- "$script_dir/.." && pwd)"
metadata="$dotnet_root/linux/package.env"
desktop="$dotnet_root/linux/com.devolutions.Terminal.desktop"
metainfo="$dotnet_root/linux/com.devolutions.Terminal.metainfo.xml"
spdx_validator="$script_dir/Validate-Spdx.py"
spdx_generator="$script_dir/Generate-LinuxPackageMetadata.py"

for script in \
    "$script_dir/Build-LinuxPackage.sh" \
    "$script_dir/Stage-LinuxPackage.sh" \
    "$script_dir/Test-LinuxArm64Runtime.sh" \
    "$script_dir/Test-LinuxPackage.sh" \
    "$script_dir/Test-LinuxDesktopIntegration.sh" \
    "$dotnet_root/linux/Install-LinuxDesktopIntegration.sh"; do
    bash -n "$script"
done
python3 -c 'import ast, pathlib, sys; ast.parse(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))' \
    "$spdx_validator"
python3 -c 'import ast, pathlib, sys; ast.parse(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))' \
    "$spdx_generator"

# shellcheck source=../linux/package.env
source "$metadata"
test "$PACKAGE_NAME" = devolutions-terminal
test "$APP_ID" = com.devolutions.Terminal
test "$INSTALL_DIR" = /opt/devolutions-terminal
test "$LICENSE_ID" = "MIT AND OFL-1.1"
test "$SBOM_LICENSE_ID" = "MIT AND OFL-1.1"
test "$LICENSE_ID" = "$SBOM_LICENSE_ID"
test "$DESKTOP_SCHEME" = x-scheme-handler/dterm
if grep -Eq '(^|_)VERSION=' "$metadata"; then
    echo "Linux package metadata must not duplicate the release version." >&2
    exit 1
fi
if grep -Eq '<release[[:space:]][^>]*version=' "$metainfo"; then
    echo "AppStream metadata must not carry a second, stale package version." >&2
    exit 1
fi
grep -Fx "Icon=$APP_ID" "$desktop" >/dev/null
grep -Fx "Exec=\"$INSTALL_DIR/Devolutions.Terminal\" %u" "$desktop" >/dev/null
grep -Fx "TryExec=$INSTALL_DIR/Devolutions.Terminal" "$desktop" >/dev/null
grep -Fx "MimeType=$DESKTOP_SCHEME;" "$desktop" >/dev/null
grep -F "<id>$APP_ID</id>" "$metainfo" >/dev/null
grep -F "<project_license>$LICENSE_ID</project_license>" "$metainfo" >/dev/null

python3 - "$metainfo" <<'PY'
import sys
import xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
assert root.tag == "component"
assert root.attrib["type"] == "desktop-application"
assert root.findtext("launchable") == "com.devolutions.Terminal.desktop"
PY

if "$script_dir/Build-LinuxPackage.sh" invalid-rid >/dev/null 2>&1; then
    echo "Builder accepted an invalid RID." >&2
    exit 1
fi
if "$script_dir/Build-LinuxPackage.sh" linux-x64 0.1.0 \
    "$dotnet_root/artifacts/invalid-format-test" invalid >/dev/null 2>&1; then
    echo "Builder accepted an invalid package format." >&2
    exit 1
fi

bash "$script_dir/Test-LinuxDesktopIntegration.sh" \
    "$dotnet_root/artifacts/linux-desktop-package-metadata-test"
echo "Linux packaging scripts and canonical metadata validation passed."
