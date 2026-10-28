"""Build the plugin, run the offline suite and package the tested DLL for Thunderstore."""

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys

from thunderstore import check_sources
from provenance import source_files, fingerprint
from tooling import ARTIFACTS, ROOT, child_environment, installation_paths, reference_assemblies
from validation import check_validation, read_summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build-only", action="store_true", help="Build without running the offline suite.")
    parser.add_argument("--no-package", action="store_true", help="Run the suite without packaging the DLL.")
    parser.add_argument("--allow-inactive-bettermap", action="store_true", help="Allow absence validation; cannot approve a package.")
    args = parser.parse_args()
    packaging = not (args.build_only or args.no_package)
    if packaging and args.allow_inactive_bettermap:
        parser.error("--allow-inactive-bettermap requires --no-package or --build-only.")
    if packaging:
        for variable in ("DEEPNORTHCOMPAT_SERVER_PATH", "DEEPNORTHCOMPAT_VENDOR_PATH"):
            if not os.environ.get(variable):
                raise RuntimeError(f"Set {variable}: packaging requires both client and dedicated-role validation.")
        # Fail before a long build if the upload would be rejected or mislabeled.
        check_sources(json.loads((ROOT / "package/manifest.json").read_text(encoding="utf-8")))
    dotnet = shutil.which("dotnet")
    if not dotnet:
        raise FileNotFoundError("Install the .NET SDK version pinned in global.json and put dotnet on PATH.")
    lab, game = installation_paths()
    environment = child_environment(lab, game)
    environment["DEEPNORTHCOMPAT_ALLOW_INACTIVE_BETTERMAP"] = "1" if args.allow_inactive_bettermap else "0"
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
    validation = run_suite(environment)
    for script in ("test-provenance.py", "test-validation.py"):
        subprocess.run([sys.executable, "-B", str(ROOT / "scripts" / script)], cwd=ROOT, check=True)
    if not packaging:
        return
    approve(validation)
    for script in ("package.py", "validate-package.py"):
        subprocess.run([sys.executable, "-B", str(ROOT / "scripts" / script)], cwd=ROOT, env=environment, check=True)


def run_suite(environment):
    # Mono owns this child process and cannot affect the build process if a native load fails.
    suite = subprocess.Popen([sys.executable, "-B", str(ROOT / "scripts/test-existing-build.py")], cwd=ROOT,
                             env=environment, stdout=subprocess.PIPE, text=True, encoding="utf-8", errors="replace")
    lines = []
    for line in suite.stdout:
        print(line, end="", flush=True)
        lines.append(line)
    if suite.wait() != 0:
        raise subprocess.CalledProcessError(suite.returncode, suite.args)
    return check_validation(read_summary(lines), allow_inactive=environment.get("DEEPNORTHCOMPAT_ALLOW_INACTIVE_BETTERMAP") == "1")


def approve(validation):
    """Record the DLL that just passed the full suite as the one packaging may ship."""
    check_validation(validation, require_server=True)
    manifest = json.loads((ROOT / "package/manifest.json").read_text(encoding="utf-8"))
    approved = {"pluginGUID": "DeepNorthCompat", "pluginName": "DeepNorthCompat",
                "version": manifest["version_number"],
                "offlineTestsPassed": validation["testCasesPassed"], "offlineValidation": validation}
    approved.update({key: hashlib.sha256((ROOT / path).read_bytes()).hexdigest()
                     for key, (path, _) in ARTIFACTS.items()})
    files = source_files()
    approved["sourceFiles"] = files
    approved["sourceSha256"] = fingerprint(files)
    (ROOT / "package/validated-build.json").write_bytes((json.dumps(approved, indent=2) + "\n").encode("utf-8"))


if __name__ == "__main__":
    main()
