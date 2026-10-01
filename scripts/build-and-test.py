"""Build the plugin, run the offline suite and package the tested DLL for Thunderstore."""

import argparse
import hashlib
import json
import re
import shutil
import subprocess
import sys

from thunderstore import check_sources
from tooling import ROOT, child_environment, installation_paths, reference_assemblies


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build-only", action="store_true", help="Build without running the offline suite.")
    parser.add_argument("--no-package", action="store_true", help="Run the suite without packaging the DLL.")
    args = parser.parse_args()
    packaging = not (args.build_only or args.no_package)
    if packaging:
        # Fail before a long build if the upload would be rejected or mislabeled.
        check_sources(json.loads((ROOT / "package/manifest.json").read_text(encoding="utf-8")))
    dotnet = shutil.which("dotnet")
    if not dotnet:
        raise FileNotFoundError("Install the .NET SDK version pinned in global.json and put dotnet on PATH.")
    lab, game = installation_paths()
    environment = child_environment(lab, game)
    project = "tests/DeepNorthCompat.Tests/DeepNorthCompat.Tests.csproj"
    framework = "-p:TargetFrameworkRootPath=" + reference_assemblies()
    # Regenerate assets after relocation rather than reusing absolute paths from another checkout.
    subprocess.run([dotnet, "restore", project, "--ignore-failed-sources", "--nologo",
                    "-p:NuGetAudit=false", framework], cwd=ROOT, env=environment, check=True)
    subprocess.run([dotnet, "build", project, "--configuration", "Release", "--no-restore",
                    "--nologo", "-p:UseSharedCompilation=false", "-nodeReuse:false", framework],
                   cwd=ROOT, env=environment, check=True)
    if args.build_only:
        return
    passed = run_suite(environment)
    if not packaging:
        return
    approve(passed)
    for script in ("package.py", "validate-package.py"):
        subprocess.run([sys.executable, "-B", str(ROOT / "scripts" / script)], cwd=ROOT, env=environment, check=True)


def run_suite(environment):
    # Mono owns this child process and cannot affect the build process if a native load fails.
    suite = subprocess.Popen([sys.executable, "-B", str(ROOT / "scripts/test-existing-build.py")], cwd=ROOT,
                             env=environment, stdout=subprocess.PIPE, text=True, encoding="utf-8", errors="replace")
    summary = None
    for line in suite.stdout:
        print(line, end="", flush=True)
        summary = re.fullmatch(r"PASS: (\d+) test cases", line.strip()) or summary
    if suite.wait() != 0:
        raise subprocess.CalledProcessError(suite.returncode, suite.args)
    if summary is None:
        raise RuntimeError("The offline suite exited cleanly without reporting its pass count.")
    return int(summary.group(1))


def approve(passed):
    """Record the DLL that just passed the full suite as the one packaging may ship."""
    manifest = json.loads((ROOT / "package/manifest.json").read_text(encoding="utf-8"))
    built = ROOT / "src/DeepNorthCompat/bin/Release/net48/DeepNorthCompat.dll"
    approved = {"pluginGUID": "DeepNorthCompat", "pluginName": "DeepNorthCompat",
                "version": manifest["version_number"], "sha256": hashlib.sha256(built.read_bytes()).hexdigest(),
                "offlineTestsPassed": passed}
    (ROOT / "package/validated-build.json").write_bytes((json.dumps(approved, indent=2) + "\n").encode("utf-8"))


if __name__ == "__main__":
    main()
