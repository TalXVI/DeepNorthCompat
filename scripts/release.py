"""Test the pushed main commit and create its GitHub release, which publishes it to Thunderstore."""

import json
import re
import shutil
import subprocess
import sys

from tooling import ROOT, require


def git(*args):
    return subprocess.run(["git", *args], cwd=ROOT, check=True, capture_output=True, text=True).stdout.strip()


def main():
    gh = shutil.which("gh")
    require(gh, "Install the GitHub CLI (gh) and sign in with gh auth login")
    manifest = json.loads((ROOT / "package/manifest.json").read_text(encoding="utf-8"))
    version = manifest["version_number"]
    tag = "v" + version
    require(not git("status", "--porcelain"), "Commit or stash every change before releasing")
    require(git("branch", "--show-current") == "main", "Release from main")
    git("fetch", "--quiet", "origin", "main")
    commit = git("rev-parse", "HEAD")
    require(commit == git("rev-parse", "origin/main"), "Push main so it matches origin/main before releasing")
    require(not git("ls-remote", "--tags", "origin", "refs/tags/" + tag), f"{tag} already exists on GitHub")

    subprocess.run([sys.executable, "-B", str(ROOT / "scripts/build-and-test.py")], cwd=ROOT, check=True)
    # A passing run rewrites validated-build.json; the release workflow trusts the committed copy.
    require(not git("status", "--porcelain"),
            "The tested DLL differs from the committed package/validated-build.json; commit it, push and rerun")

    changelog = (ROOT / "package/CHANGELOG.md").read_text(encoding="utf-8")
    notes = re.search(rf"^## {re.escape(version)}\n(.*?)(?=^## |\Z)", changelog, re.MULTILINE | re.DOTALL)
    archive = ROOT / "dist" / f"{manifest['name']}-{version}.zip"
    subprocess.run([gh, "release", "create", tag, str(archive), "--target", commit,
                    "--title", f"{manifest['name']} {version}", "--notes", notes.group(1).strip()],
                   cwd=ROOT, check=True)


if __name__ == "__main__":
    main()
