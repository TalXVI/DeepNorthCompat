# Build and test

Use Windows, Python 3, .NET SDK 8.0.422 and the .NET Framework 4.8 reference assemblies. The SDK version is pinned in global.json. The current projects expect the cached `Microsoft.NETFramework.ReferenceAssemblies.net48` package at version 1.0.3.

The build and offline tests reference your installed Valheim, BepInEx, Harmony and target mods. They do not redistribute those assemblies. The default locations are Steam's usual Valheim directory and Gale's Deep North - Lab profile. Set `DEEPNORTHCOMPAT_VALHEIM_PATH` and `DEEPNORTHCOMPAT_LAB_PATH` to use other locations. These variables configure both the build and test host.

```powershell
./scripts/build-and-test.ps1
python -B ./scripts/package.py
python -B ./scripts/validate-package.py
```

The 55-case offline suite uses Valheim's embedded Mono runtime with native scene boundaries replaced by test fixtures. It checks the inspected Harmony targets, optional-mod guards, bow stamina/reporting, animal drop parsing and quality-aware crafting, including batch crafting and failure rollback. It does not launch Valheim or a server. Its config parsing case reads the personal pack's accepted input settings without writing them.

The runtime patches are guarded against the hashes and signatures of the inspected upstream builds. Changes to those mods require a new compatibility review. Missing optional mods leave their corresponding patch inactive.

Packaging uses the last validated DLL hash in `package/validated-build.json`. It creates `dist/DeepNorthCompat-1.0.0.zip` with the manifest, README, changelog, MIT license, icon and `BepInEx/plugins/DeepNorthCompat/DeepNorthCompat.dll`. After changing source, run the complete suite before updating that hash. No script installs or deploys the package.

Gale 1.22.3 can track this ZIP through its local importer. Local profile duplication preserves it. Normal profile exports and published mod sets exclude local packages, so distribute the ZIP separately. A public GitHub repository does not change that behavior.
