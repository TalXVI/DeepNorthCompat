"""Build the plugin and run the offline suite without a shell dependency."""

import argparse
import shutil
import subprocess
import sys

from tooling import ROOT, child_environment, installation_paths, reference_assemblies


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build-only", action="store_true", help="Build without running the offline suite.")
    args = parser.parse_args()
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
    if not args.build_only:
        # Mono owns this child process and cannot affect the build process if a native load fails.
        subprocess.run([sys.executable, "-B", str(ROOT / "scripts/test-existing-build.py")],
                       cwd=ROOT, env=environment, check=True)


if __name__ == "__main__":
    main()
