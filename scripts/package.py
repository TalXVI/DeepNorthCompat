"""Create a deterministic local Thunderstore ZIP from the approved DLL.

This script does not build, install, launch Gale, or contact a remote service.
"""

import hashlib
import json
import shutil
from pathlib import Path
import zipfile

from thunderstore import check_sources
from provenance import verify_sources
from tooling import require

ROOT = Path(__file__).resolve().parents[1]
PACKAGE = ROOT / "package"
DLL_MEMBER = "BepInEx/plugins/DeepNorthCompat/DeepNorthCompat.dll"


def main():
    manifest = json.loads((PACKAGE / "manifest.json").read_text(encoding="utf-8"))
    approved = json.loads((PACKAGE / "validated-build.json").read_text(encoding="utf-8"))
    check_sources(manifest)
    verify_sources(approved)
    require(manifest["version_number"] == approved["version"], "Manifest version differs from the approved build")
    require(manifest["dependencies"] == ["denikson-BepInExPack_Valheim-5.4.2351"], "Package dependencies changed")
    built = ROOT / "src/DeepNorthCompat/bin/Release/net48/DeepNorthCompat.dll"
    require(approved["pluginGUID"] == manifest["name"] == "DeepNorthCompat", "Plugin identity changed")
    require(hashlib.sha256(built.read_bytes()).hexdigest() == approved["sha256"],
            "Build differs from the approved DLL. Revalidate before changing the package hash.")
    payload = PACKAGE / DLL_MEMBER
    payload.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(built, payload)
    shutil.copyfile(ROOT / "README.md", PACKAGE / "README.md")
    shutil.copyfile(ROOT / "LICENSE", PACKAGE / "LICENSE")
    members = ["manifest.json", "README.md", "CHANGELOG.md", "LICENSE", "icon.png", DLL_MEMBER]
    output = ROOT / "dist" / (manifest["name"] + "-" + manifest["version_number"] + ".zip")
    output.parent.mkdir(exist_ok=True)
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for member in members:
            info = zipfile.ZipInfo(member, date_time=(2026, 9, 30, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, (PACKAGE / member).read_bytes(), compresslevel=9)
    with zipfile.ZipFile(output) as archive:
        require(archive.testzip() is None, "Package archive is corrupt")
        require(archive.namelist() == members, "Package members changed")
        require(archive.read(DLL_MEMBER) == built.read_bytes(), "Packaged DLL differs from the build")
    print(json.dumps({"package": str(output), "sha256": hashlib.sha256(output.read_bytes()).hexdigest(),
                      "dllSHA256": approved["sha256"], "members": members}, indent=2))


if __name__ == "__main__":
    main()
