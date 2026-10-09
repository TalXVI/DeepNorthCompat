"""Bind a validated DLL to source bytes without a self-referential commit field."""
import hashlib
import json
import subprocess
from tooling import ROOT, require


def source_files(root=ROOT):
    names = subprocess.check_output(
        ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"], cwd=root
    ).decode().split("\0")
    return {name: hashlib.sha256((root / name).read_bytes()).hexdigest()
            for name in sorted(set(names))
            if name and name != "package/validated-build.json" and (root / name).is_file()}


def fingerprint(files):
    return hashlib.sha256(json.dumps(files, sort_keys=True).encode()).hexdigest()


def verify_sources(record, root=ROOT):
    files = source_files(root)
    require(files == record["sourceFiles"] and fingerprint(files) == record["sourceSha256"],
            "Source changed after validation. Rerun the full suite before packaging or releasing.")
