"""Thunderstore upload rules for the package sources.

Rules follow https://wiki.thunderstore.io/mods/creating-a-package and
https://wiki.thunderstore.io/mods/updating-a-package.
"""

import re

from tooling import ROOT, require

PACKAGE = ROOT / "package"
VERSION = re.compile(r"\d+\.\d+\.\d+")
DEPENDENCY = re.compile(r"[A-Za-z0-9_]+-[A-Za-z0-9_]+-\d+\.\d+\.\d+")


def check_sources(manifest):
    """Reject package sources Thunderstore would refuse or that disagree on the version."""
    name, version = manifest["name"], manifest["version_number"]
    require(re.fullmatch(r"[A-Za-z0-9_]{1,128}", name), "Package name must be 1-128 characters of A-Z a-z 0-9 _")
    require(isinstance(manifest["description"], str) and len(manifest["description"]) <= 250,
            "Package description must be at most 250 characters")
    require(VERSION.fullmatch(version), "version_number must be Major.Minor.Patch without a suffix")
    require(isinstance(manifest["website_url"], str), "website_url must be a string, even if empty")
    require(all(isinstance(entry, str) and DEPENDENCY.fullmatch(entry) for entry in manifest["dependencies"]),
            "Dependencies must look like Team-Package-1.2.3")
    png = (PACKAGE / "icon.png").read_bytes()
    require(png[:8] == b"\x89PNG\r\n\x1a\n" and png[16:24] == b"\0\0\x01\0\0\0\x01\0", "Icon is not a 256x256 PNG")
    try:
        (ROOT / "README.md").read_bytes().decode("utf-8")
    except UnicodeDecodeError:
        require(False, "README.md must be UTF-8")
    # Uploaded versions are immutable, so the DLL and changelog must describe this exact version.
    project = (ROOT / "src/DeepNorthCompat/DeepNorthCompat.csproj").read_text(encoding="utf-8")
    require(f"<Version>{version}</Version>" in project, f"DeepNorthCompat.csproj <Version> is not {version}")
    plugin = (ROOT / "src/DeepNorthCompat/Plugin.cs").read_text(encoding="utf-8")
    require(f'"DeepNorthCompat", "{version}")]' in plugin, f"Plugin.cs BepInPlugin version is not {version}")
    changelog = (PACKAGE / "CHANGELOG.md").read_text(encoding="utf-8")
    latest = re.search(r"^## (\S+)", changelog, re.MULTILINE)
    require(latest and latest.group(1) == version, f"CHANGELOG.md does not start with a ## {version} section")
