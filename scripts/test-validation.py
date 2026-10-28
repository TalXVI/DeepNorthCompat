"""Test that incomplete offline evidence cannot authorize a release artifact."""

import copy
from contextlib import redirect_stdout
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

from tooling import ROOT


def load_script(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / (name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class ValidationTests(unittest.TestCase):
    def test_count_only_result_cannot_write_build_approval(self):
        build = load_script("build-and-test")
        with tempfile.TemporaryDirectory(prefix="validation-test-", dir=ROOT / "local-audit") as directory:
            root = Path(directory)
            (root / "package").mkdir()
            (root / "package/manifest.json").write_text(json.dumps({"version_number": "1.2.3"}))
            built = root / "src/DeepNorthCompat/bin/Release/net48/DeepNorthCompat.dll"
            built.parent.mkdir(parents=True)
            built.write_bytes(b"owned candidate fixture")
            approval = root / "package/validated-build.json"
            approval.write_bytes(b"previous approval")
            with patch.object(build, "ROOT", root):
                with self.assertRaisesRegex(ValueError, "coverage"):
                    build.approve(149)
            self.assertEqual(approval.read_bytes(), b"previous approval")

    def test_clean_count_only_child_is_rejected(self):
        build = load_script("build-and-test")

        class Child:
            stdout = io.StringIO("PASS: 149 test cases\n")
            returncode = 0
            args = []

            def wait(self):
                return self.returncode

        def start(command, **options):
            self.assertEqual(command, [sys.executable, "-B", str(ROOT / "scripts/test-existing-build.py")])
            self.assertEqual(options["cwd"], ROOT)
            return Child()

        with patch.object(build.subprocess, "Popen", side_effect=start), redirect_stdout(io.StringIO()):
            with self.assertRaisesRegex(ValueError, "coverage"):
                build.run_suite({})

    def test_approval_requires_both_roles_and_complete_target_evidence(self):
        build = load_script("build-and-test")
        groups = {name: "exercised" for name in ("Cartography", "Session", "Persistence", "Portal", "Vehicle")}
        client = {"integration": "BetterMap", "role": "client", "status": "complete", "casesCollected": 35,
                  "casesPassed": 35, "groups": groups, "targets": {"Valheim": "a" * 64, "BetterMap": "b" * 64, "Cartography": "c" * 64}}
        server = {"integration": "BetterMap", "role": "server", "status": "complete", "casesCollected": 9,
                  "casesPassed": 9, "groups": {name: "exercised" if name == "Session" else "inactive" for name in groups},
                  "targets": {"Valheim": "d" * 64, "BetterMap": "b" * 64}}
        valid = {"schemaVersion": 1, "testCasesPassed": 239,
                 "roles": {"client": {"testCasesPassed": 184, "integrations": {"BetterMap": client}},
                           "server": {"testCasesPassed": 55, "integrations": {"BetterMap": server}}}}
        invalid = []
        missing = copy.deepcopy(valid); del missing["roles"]["server"]; invalid.append(missing)
        partial = copy.deepcopy(valid); partial["roles"]["client"]["integrations"]["BetterMap"]["casesPassed"] = 34; invalid.append(partial)
        inactive = copy.deepcopy(valid); inactive["roles"]["client"]["integrations"]["BetterMap"]["status"] = "inactive"; invalid.append(inactive)
        target = copy.deepcopy(valid); del target["roles"]["server"]["integrations"]["BetterMap"]["targets"]["BetterMap"]; invalid.append(target)
        group = copy.deepcopy(valid); group["roles"]["client"]["integrations"]["BetterMap"]["groups"]["Persistence"] = "inactive"; invalid.append(group)
        count = copy.deepcopy(valid); count["testCasesPassed"] = 238; invalid.append(count)
        with tempfile.TemporaryDirectory(prefix="validation-test-", dir=ROOT / "local-audit") as directory:
            root = Path(directory)
            (root / "package").mkdir()
            (root / "package/manifest.json").write_text(json.dumps({"version_number": "1.2.3"}))
            built = root / "src/DeepNorthCompat/bin/Release/net48/DeepNorthCompat.dll"
            built.parent.mkdir(parents=True); built.write_bytes(b"owned candidate fixture")
            preloader = root / "src/DeepNorthCompat.Preloader/bin/Release/net48/DeepNorthCompat.Preloader.dll"
            preloader.parent.mkdir(parents=True)
            preloader.write_bytes(b"owned preloader fixture")
            approval = root / "package/validated-build.json"
            with patch.object(build, "ROOT", root):
                for evidence in invalid:
                    with self.subTest(evidence=evidence):
                        approval.write_bytes(b"previous approval")
                        with self.assertRaisesRegex(ValueError, "coverage"):
                            build.approve(evidence)
                        self.assertEqual(approval.read_bytes(), b"previous approval")
                build.approve(valid)
            written = json.loads(approval.read_text())
            self.assertEqual(written["offlineValidation"], valid)
            self.assertEqual(written["offlineTestsPassed"], 239)

    def test_package_and_release_validation_reject_old_count_only_approval(self):
        with tempfile.TemporaryDirectory(prefix="validation-test-", dir=ROOT / "local-audit") as directory:
            root = Path(directory); (root / "package").mkdir()
            (root / "package/manifest.json").write_text("{}")
            (root / "package/validated-build.json").write_text(json.dumps({"offlineTestsPassed": 149}))
            for name in ("package", "validate-package"):
                with self.subTest(script=name):
                    module = load_script(name)
                    with patch.object(module, "ROOT", root), patch.object(sys, "argv", [name]):
                        if name == "package":
                            module.PACKAGE = root / "package"
                        with self.assertRaisesRegex(ValueError, "coverage"):
                            module.main()
                    self.assertFalse((root / "dist").exists())
                    self.assertFalse((root / "package/BepInEx").exists())


if __name__ == "__main__":
    unittest.main()
