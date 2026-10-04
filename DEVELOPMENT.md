# Build and test

Use 64-bit Python 3.10 or later, .NET SDK 8.0.422 and the .NET Framework 4.8 reference assemblies. The SDK version is pinned in global.json. The projects expect the cached `Microsoft.NETFramework.ReferenceAssemblies.net48` package at version 1.0.3. Set `DEEPNORTHCOMPAT_REFERENCE_ASSEMBLIES_PATH` to its `build` directory if it is outside the usual NuGet cache.

The build and offline tests reference your installed Valheim, BepInEx, Harmony and target mods. They do not redistribute those assemblies. Set `DEEPNORTHCOMPAT_VALHEIM_PATH` and `DEEPNORTHCOMPAT_LAB_PATH` to those installation directories. On Windows, the tools also recognize Steam's usual Valheim directory and Gale's Deep North - Lab profile. Other systems require explicit paths. These variables configure both the build and test host.

```text
python -B scripts/build-and-test.py
python -B scripts/test-existing-build.py
python -B scripts/package.py
python -B scripts/validate-package.py
```

`build-and-test.py` builds the plugin and runs the suite. If every case passes, it writes `dist/DeepNorthCompat-<version>.zip` for upload to Thunderstore. Use `--build-only` to build without running the suite, or `--no-package` to run the suite without packaging. `test-existing-build.py` reruns the tests without rebuilding.

The repository's `justfile` provides shorter aliases for these commands if [just](https://github.com/casey/just) is installed: `just build`, `just test`, `just retest`, `just package` and `just validate`. Run `just` to list them.

The Python test host loads Valheim's embedded Mono runtime through `ctypes`. It recognizes Windows, Linux and macOS library names. Set `DEEPNORTHCOMPAT_MONO_LIBRARY` to the library file if your installation uses another layout. The host and full suite have been exercised on Windows; Linux and macOS runtime execution still need confirmation.

The offline suite replaces native scene boundaries with test fixtures. It checks the inspected Harmony targets, optional-mod guards, bow stamina/reporting, animal drop parsing and quality-aware crafting, including batch crafting, upgrades, chests guarded by MultiUserChest and failure rollback. The AzuEPI fixture registers the installed vendor methods in real Harmony metadata. It checks exact removal, retained hooks, deferred registration, rejection and rollback, and runs the stance sync against stubbed animators. It does not launch Valheim or a server. Its config parsing case reads the personal pack's accepted input settings without writing them.

The runtime patches are guarded against the hashes and signatures of the inspected upstream builds. Changes to those mods require a new compatibility review. Missing optional mods leave their corresponding patch inactive.

The dedicated integration adds a second process using the dedicated server's managed assemblies and embedded Mono. Set `DEEPNORTHCOMPAT_SERVER_PATH` to that installation and `DEEPNORTHCOMPAT_SIMULATION_PATH` to a local directory containing the unchanged pinned release binaries as `fork.dll` and `tune.dll`. Both variables are required when producing a package. Client and server suites run separately so one process cannot resolve the wrong game build. [docs/dedicated-simulation.md](docs/dedicated-simulation.md) lists the pinned versions, patch targets, configuration and multiplayer launch gate. Keep downloaded or inspected vendor files outside tracked source, for example under the ignored `local-audit` directory.

The AzuEPI module also checks Harmony state, which can change without a hash change. It runs in `Start`, after deferred vendor registrations finish, and changes nothing unless both equipment postfixes are the only registrations of their methods and AzuEPI's local preview hooks are installed. [docs/azu-epi-preview.md](docs/azu-epi-preview.md) describes the fix and its rollback.

Packaging only ships a DLL that passed the full suite. After a passing run, `build-and-test.py` records the DLL hash and test count in `package/validated-build.json`, then runs `package.py` and `validate-package.py`. Running `package.py` on its own refuses any DLL whose hash differs from that file. The ZIP holds the manifest, README, changelog, MIT license and icon at its root, plus `BepInEx/plugins/DeepNorthCompat/DeepNorthCompat.dll`. Only the release workflow uploads the package; no script installs or deploys it.

Before building, the script checks the package against Thunderstore's upload rules. The name must be at most 128 characters of `A-Z a-z 0-9 _`, the description at most 250 characters and the version plain `Major.Minor.Patch`. Dependencies must look like `Team-Package-1.2.3`, the icon must be a 256x256 PNG and the README must be UTF-8. Thunderstore never lets you edit an uploaded version, so the script also requires the manifest version to match `<Version>` in the csproj, the `BepInPlugin` version in `Plugin.cs` and the top `##` heading of `package/CHANGELOG.md`.

To release an update, bump the version in all four places and in the plugin identity test in `tests/DeepNorthCompat.Tests/Program.cs`, then add its changelog section above the previous one. Run `build-and-test.py`, commit the result including `package/validated-build.json`, push `main` and run `python -B scripts/release.py` (`just release`). Keep the manifest `name` unchanged, or Thunderstore treats the upload as a new package.

`release.py` refuses to run unless the working tree is clean, `main` matches `origin/main` and the version's tag doesn't exist yet. It reruns the full suite and checks that the tested DLL matches the committed `validated-build.json`. Then it uses the GitHub CLI to create the `v<version>` release with the ZIP attached and the version's changelog section as notes. Publishing that release runs `.github/workflows/publish.yml`. The workflow checks the ZIP against the tagged sources and approved hash with `validate-package.py --release-tag`, then uploads it to Thunderstore with `publish-thunderstore.py`. GitHub's runners cannot build or test the plugin, so the workflow only ever publishes a ZIP that passed the suite on your machine.

The upload needs a Thunderstore service account token for the Talent team, stored as the repository secret `TCLI_AUTH_TOKEN`. The script lists the package under the valheim community with the categories set in `publish-thunderstore.py`, and it stops if that version is already on Thunderstore.

Automatic Git version suffixes and Source Link generation are disabled so repository metadata cannot change the validated DLL hash.

Install the published `Talent/DeepNorthCompat` package in Gale so normal profile exports and published mod sets include it. Gale's local importer is useful for pre-release validation, but those local records are excluded from the published mod manifest. The dedicated simulation fork is server-only; clients need DeepNorthCompat for the owner-skill bridge, without a local simulation DLL.
