"""Test source validation across a commit and against changed or extra source."""
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from provenance import source_files, fingerprint, verify_sources
from tooling import ROOT


class ProvenanceTests(unittest.TestCase):
    def test_commit_stable_source_and_mutation_rejection(self):
        audit = ROOT / "local-audit"
        audit.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="provenance-test-", dir=audit) as directory:
            root = Path(directory)
            def git(*args):
                subprocess.run(["git", *args], cwd=root, check=True, stdout=subprocess.DEVNULL)
            git("init", "--quiet")
            git("config", "user.name", "Release fixture")
            git("config", "user.email", "fixture@example.invalid")
            git("config", "core.autocrlf", "false")
            (root / "source.cs").write_bytes(b"class Fixture {}\n")
            files = source_files(root)
            record = {"sourceFiles": files, "sourceSha256": fingerprint(files)}
            (root / "package").mkdir()
            (root / "package/validated-build.json").write_text(json.dumps(record))
            git("add", ".")
            git("commit", "--quiet", "-m", "Commit validated fixture")
            verify_sources(record, root)
            (root / "extra.cs").write_bytes(b"class Extra {}\n")
            with self.assertRaises(SystemExit):
                verify_sources(record, root)
            (root / "extra.cs").unlink()
            (root / "source.cs").write_bytes(b"class Changed {}\n")
            with self.assertRaises(SystemExit):
                verify_sources(record, root)


if __name__ == "__main__":
    unittest.main()
