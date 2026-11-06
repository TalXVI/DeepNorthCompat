"""Test source validation across a commit and against changed or extra source."""
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock
import release
from provenance import source_files, fingerprint, verify_sources, verify_committed_sources
from tooling import ROOT


class ProvenanceTests(unittest.TestCase):
    def test_rejects_working_bytes_normalized_by_git(self):
        with tempfile.TemporaryDirectory(prefix="provenance-line-endings-") as directory:
            root = Path(directory)

            def git(*args):
                subprocess.run(["git", *args], cwd=root, check=True, stdout=subprocess.DEVNULL)

            git("init", "--quiet", "--initial-branch", "main")
            git("config", "user.name", "Release fixture")
            git("config", "user.email", "fixture@example.invalid")
            git("config", "core.autocrlf", "false")
            (root / ".gitattributes").write_bytes(b"* text eol=lf\n")
            source = root / "source.cs"
            source.write_bytes(b"class Fixture {}\r\n")
            (root / "package").mkdir()
            (root / "package/manifest.json").write_bytes(b'{"version_number":"9.9.9"}\n')
            files = source_files(root)
            record = {"sourceFiles": files, "sourceSha256": fingerprint(files)}
            (root / "package/validated-build.json").write_bytes(json.dumps(record).encode())
            git("add", ".")
            git("commit", "--quiet", "-m", "Commit normalized fixture")
            git("update-ref", "refs/remotes/origin/main", "HEAD")

            verify_sources(record, root)
            with self.assertRaises(SystemExit):
                verify_committed_sources(record, root)

            local_git = release.git
            run = subprocess.run

            def release_git(*args):
                if args in (("fetch", "--quiet", "origin", "main"),
                            ("ls-remote", "--tags", "origin", "refs/tags/v9.9.9")):
                    return ""
                return local_git(*args)

            def git_only(command, *args, **kwargs):
                if command[0] != "git":
                    raise AssertionError("Build or publication started before source validation")
                return run(command, *args, **kwargs)

            with mock.patch.object(release, "ROOT", root), \
                    mock.patch.object(release, "git", side_effect=release_git), \
                    mock.patch.object(release.shutil, "which", return_value="gh"), \
                    mock.patch.object(release.subprocess, "run", side_effect=git_only):
                with self.assertRaisesRegex(SystemExit, "Committed source bytes differ"):
                    release.main()

            source.write_bytes(b"class Fixture {}\n")
            files = source_files(root)
            verify_committed_sources({"sourceFiles": files, "sourceSha256": fingerprint(files)}, root)

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
