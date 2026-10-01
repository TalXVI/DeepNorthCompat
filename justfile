set windows-shell := ["powershell.exe", "-NoLogo", "-NoProfile", "-Command"]

python := if os_family() == "windows" { "python" } else { "python3" }

# List the available recipes.
[private]
default:
    @just --list

# Build the plugin and test project without running the suite.
build:
    {{python}} -B scripts/build-and-test.py --build-only

# Build and run the offline suite.
test:
    {{python}} -B scripts/build-and-test.py

# Rerun the offline suite against the existing build.
retest:
    {{python}} -B scripts/test-existing-build.py

# Create the Thunderstore ZIP in dist/ from the approved DLL.
package:
    {{python}} -B scripts/package.py

# Check the dist/ ZIP and build against the approved hash.
validate:
    {{python}} -B scripts/validate-package.py
