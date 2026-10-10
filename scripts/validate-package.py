"""Validate the local package without installing it or launching the game."""

import argparse
import hashlib
import json
from pathlib import Path
import zipfile

from thunderstore import check_sources
from provenance import verify_sources
from tooling import require

ROOT = Path(__file__).resolve().parents[1]
DLL_MEMBER = "BepInEx/plugins/DeepNorthCompat/DeepNorthCompat.dll"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--release-tag", help="Check a downloaded release ZIP against this tag. "
                        "Skips the local build comparison, since the release workflow does not build.")
    args = parser.parse_args()
    manifest = json.loads((ROOT / "package/manifest.json").read_text(encoding="utf-8"))
    approved = json.loads((ROOT / "package/validated-build.json").read_text(encoding="utf-8"))
    verify_sources(approved)
    check_sources(manifest)
    if args.release_tag:
        require(args.release_tag == "v" + manifest["version_number"], "Release tag differs from the manifest version")
    require(manifest["name"] == approved["pluginGUID"] == approved["pluginName"] == "DeepNorthCompat",
            "Plugin identity changed")
    require(manifest["version_number"] == approved["version"], "Manifest version differs from the approved build")
    require(manifest["website_url"] == "https://github.com/TalXVI/DeepNorthCompat", "Package website changed")
    require(manifest["dependencies"] == ["denikson-BepInExPack_Valheim-5.4.2351"], "Package dependencies changed")
    archive_path = ROOT / "dist" / (manifest["name"] + "-" + manifest["version_number"] + ".zip")
    members = ["manifest.json", "README.md", "CHANGELOG.md", "LICENSE", "icon.png", DLL_MEMBER]
    with zipfile.ZipFile(archive_path) as archive:
        require(archive.namelist() == members and archive.testzip() is None, "Package members changed or corrupt")
        require(archive.read("README.md") == (ROOT / "README.md").read_bytes(), "Packaged README is stale")
        require(archive.read("LICENSE") == (ROOT / "LICENSE").read_bytes(), "Packaged LICENSE is stale")
        require(json.loads(archive.read("manifest.json")) == manifest, "Packaged manifest is stale")
        require(hashlib.sha256(archive.read(DLL_MEMBER)).hexdigest() == approved["sha256"],
                "Packaged DLL differs from the approved hash")
        require(archive.read("icon.png") == (ROOT / "package/icon.png").read_bytes(), "Packaged icon is stale")
        require(archive.read("CHANGELOG.md") == (ROOT / "package/CHANGELOG.md").read_bytes(),
                "Packaged CHANGELOG is stale")
        for member in members:
            content = archive.read(member)
            for retired_prefix in ("DeepNorth.", "DeepNorth_", "DeepNorth-"):
                require(retired_prefix not in member and retired_prefix.encode() not in content
                        and retired_prefix.encode("utf-16le") not in content,
                        f"Retired identity {retired_prefix} found in {member}")
    if not args.release_tag:
        built = ROOT / "src/DeepNorthCompat/bin/Release/net48/DeepNorthCompat.dll"
        require(hashlib.sha256(built.read_bytes()).hexdigest() == approved["sha256"],
                "Build differs from the approved DLL")
    print(json.dumps({"result": "PASS", "package": str(archive_path), "plugin": approved,
                      "members": members, "retiredIdentitiesAbsent": True}, indent=2))


if __name__ == "__main__":
    main()
