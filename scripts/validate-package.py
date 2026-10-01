"""Validate the local package without installing it or launching the game."""

import hashlib
import json
from pathlib import Path
import zipfile

ROOT = Path(__file__).resolve().parents[1]
DLL_MEMBER = "BepInEx/plugins/DeepNorthCompat/DeepNorthCompat.dll"


def main():
    manifest = json.loads((ROOT / "package/manifest.json").read_text(encoding="utf-8"))
    approved = json.loads((ROOT / "package/validated-build.json").read_text(encoding="utf-8"))
    assert manifest["name"] == approved["pluginGUID"] == approved["pluginName"] == "DeepNorthCompat"
    assert manifest["version_number"] == approved["version"] == "1.0.0"
    assert manifest["author"] == "TalXVI"
    assert manifest["website_url"] == "https://github.com/TalXVI/DeepNorthCompat"
    assert manifest["dependencies"] == ["denikson-BepInExPack_Valheim-5.4.2351"]
    archive_path = ROOT / "dist/DeepNorthCompat-1.0.0.zip"
    members = ["manifest.json", "README.md", "CHANGELOG.md", "LICENSE", "icon.png", DLL_MEMBER]
    with zipfile.ZipFile(archive_path) as archive:
        assert archive.namelist() == members and archive.testzip() is None
        assert archive.read("README.md") == (ROOT / "README.md").read_bytes()
        assert archive.read("LICENSE") == (ROOT / "LICENSE").read_bytes()
        assert json.loads(archive.read("manifest.json")) == manifest
        assert hashlib.sha256(archive.read(DLL_MEMBER)).hexdigest() == approved["sha256"]
        png = archive.read("icon.png")
        assert png[:8] == b"\x89PNG\r\n\x1a\n" and png[16:24] == b"\0\0\x01\0\0\0\x01\0"
        for member in members:
            content = archive.read(member)
            for retired_prefix in ("DeepNorth.", "DeepNorth_", "DeepNorth-"):
                assert retired_prefix not in member
                assert retired_prefix.encode() not in content
                assert retired_prefix.encode("utf-16le") not in content
    built = ROOT / "src/DeepNorthCompat/bin/Release/net48/DeepNorthCompat.dll"
    assert hashlib.sha256(built.read_bytes()).hexdigest() == approved["sha256"]
    print(json.dumps({"result": "PASS", "package": str(archive_path), "plugin": approved,
                      "members": members, "retiredIdentitiesAbsent": True}, indent=2))


if __name__ == "__main__":
    main()
