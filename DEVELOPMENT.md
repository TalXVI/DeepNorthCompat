# Build and test

Use 64-bit Python 3.10 or later, .NET SDK 8.0.422 and the .NET Framework 4.8 reference assemblies. The SDK version is pinned in global.json. The projects expect the cached `Microsoft.NETFramework.ReferenceAssemblies.net48` package at version 1.0.3. Set `DEEPNORTHCOMPAT_REFERENCE_ASSEMBLIES_PATH` to its `build` directory if it is outside the usual NuGet cache.

The build and offline tests reference your installed Valheim, BepInEx, Harmony and target mods. They do not redistribute those assemblies. Set `DEEPNORTHCOMPAT_VALHEIM_PATH` and `DEEPNORTHCOMPAT_LAB_PATH` to those installation directories. On Windows, the tools also recognize Steam's usual Valheim directory and Gale's Deep North - Lab profile. Other systems require explicit paths. These variables configure both the build and test host.

```text
python -B scripts/build-and-test.py
python -B scripts/test-existing-build.py
python -B scripts/package.py
python -B scripts/validate-package.py
```

`build-and-test.py` builds and runs the suite. Use `--build-only` to build without running it. `test-existing-build.py` reruns the tests without rebuilding.

The repository's `justfile` provides shorter aliases for these commands if [just](https://github.com/casey/just) is installed: `just build`, `just test`, `just retest`, `just package` and `just validate`. Run `just` to list them.

The Python test host loads Valheim's embedded Mono runtime through `ctypes`. It recognizes Windows, Linux and macOS library names. Set `DEEPNORTHCOMPAT_MONO_LIBRARY` to the library file if your installation uses another layout. The host and full suite have been exercised on Windows; Linux and macOS runtime execution still need confirmation.

The offline suite replaces native scene boundaries with test fixtures. It checks the inspected Harmony targets, optional-mod guards, bow stamina/reporting, animal drop parsing and quality-aware crafting, including batch crafting, upgrades, chests guarded by MultiUserChest and failure rollback. It does not launch Valheim or a server. Its config parsing case reads the personal pack's accepted input settings without writing them.

The runtime patches are guarded against the hashes and signatures of the inspected upstream builds. Changes to those mods require a new compatibility review. Missing optional mods leave their corresponding patch inactive.

Packaging uses the last validated DLL hash in `package/validated-build.json`. It creates `dist/DeepNorthCompat-<version>.zip` with the manifest, README, changelog, MIT license, icon and `BepInEx/plugins/DeepNorthCompat/DeepNorthCompat.dll`. After changing source, run the complete suite before updating that hash. No script installs or deploys the package.

Automatic Git version suffixes and Source Link generation are disabled so repository metadata cannot change the validated DLL hash.

Gale 1.22.3 can track this ZIP through its local importer. Local profile duplication preserves it. Normal profile exports and published mod sets exclude local packages, so distribute the ZIP separately. A public GitHub repository does not change that behavior.
