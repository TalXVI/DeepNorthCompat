"""Bind validated DLLs to source bytes without a self-referential commit field."""
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


def verify_committed_sources(record, root=ROOT):
    files = {name: hashlib.sha256(subprocess.check_output(
        ["git", "show", "HEAD:" + name], cwd=root)).hexdigest()
        for name in record["sourceFiles"]}
    require(files == record["sourceFiles"],
            "Committed source bytes differ from the validated files. Check line endings before releasing.")
