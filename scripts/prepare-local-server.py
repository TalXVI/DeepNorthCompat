"""Stage the active profile in a disposable dedicated-server copy; optionally run a bounded smoke test."""

import argparse
import configparser
import json
import os
from pathlib import Path
import secrets
import shutil
import signal
import subprocess
import time
import zipfile

from tooling import ROOT, configured_directory, installation_paths, require


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--smoke", action="store_true", help="Start privately, wait for startup, save, then stop.")
    parser.add_argument("--seconds", type=int, default=180, help="Startup deadline, 30–600 seconds.")
    parser.add_argument("--port", type=int, default=26920, help="A separate local UDP port.")
    parser.add_argument("--extra-package", type=Path, action="append", default=[], help="Overlay a candidate integration ZIP in the disposable copy only.")
    args = parser.parse_args()
    require(30 <= args.seconds <= 600, "Startup deadline must be 30–600 seconds")
    require(1024 <= args.port <= 65533, "Invalid local port")
    lab, _ = installation_paths()
    server = configured_directory("DEEPNORTHCOMPAT_SERVER_PATH")
    runtime = ROOT / "local-audit/dedicated-runtime"
    require(runtime.resolve().is_relative_to((ROOT / "local-audit").resolve()), "Runtime must stay in ignored local-audit")
    require(runtime.resolve() != server and runtime.resolve() != lab, "Runtime cannot be an installation or profile")
    runtime.mkdir(parents=True, exist_ok=True)
    marker = runtime / ".deepnorthcompat-runtime.json"
    existing = list(runtime.iterdir())
    require(not existing or marker.is_file(), "Refusing to overwrite an unowned runtime directory")
    marker.write_text(json.dumps({"purpose": "disposable local validation", "version": 1}), encoding="utf-8")
    for source in server.iterdir():
        if source.is_file() and source.suffix.lower() in {".exe", ".dll", ".so"}:
            shutil.copy2(source, runtime / source.name)
        elif source.is_dir() and source.name in {"valheim_server_Data", "MonoBleedingEdge", "D3D12", "linux64"}:
            if not (runtime / source.name).exists():
                shutil.copytree(source, runtime / source.name)
    if (server / "steam_appid.txt").is_file():
        shutil.copy2(server / "steam_appid.txt", runtime / "steam_appid.txt")
    executable = next((runtime / name for name in ("valheim_server.exe", "valheim_server.x86_64", "valheim_server") if (runtime / name).is_file()), None)
    if executable is None and (server / "valheim_server.x86_64").is_file():
        shutil.copy2(server / "valheim_server.x86_64", runtime / "valheim_server.x86_64")
        executable = runtime / "valheim_server.x86_64"
    require(executable is not None, "Dedicated-server executable not found")
    # Restage only this tool's owned mod tree. Saves stay in the separate private directory.
    bepinex = runtime / "BepInEx"
    if bepinex.exists():
        require(bepinex.resolve().is_relative_to(runtime.resolve()), "Unsafe BepInEx destination")
        shutil.rmtree(bepinex)
    for name in ("core", "patchers", "plugins", "config"):
        source = lab / "BepInEx" / name
        if source.is_dir():
            shutil.copytree(source, bepinex / name)
    allowed = {"DeepNorthCompat": "DeepNorthCompat.dll", "Valheim_Serverside_Simulations": "Valheim_Serverside.dll", "ValheimTune": "ValheimTune.dll"}
    for package in args.extra_package:
        with zipfile.ZipFile(package) as archive:
            manifest = json.loads(archive.read("manifest.json"))
            name = manifest["name"]
            require(name in allowed, "Only the three integration candidates may be overlaid")
            dlls = [entry for entry in archive.namelist() if entry.lower().endswith(".dll")]
            require(len(dlls) == 1 and Path(dlls[0]).name == allowed[name], "Candidate package DLL does not match its identity")
            for old in (bepinex / "plugins").rglob(allowed[name]):
                old.unlink()
            target = bepinex / "plugins" / name / allowed[name]
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(archive.read(dlls[0]))
    for name in ("winhttp.dll", "doorstop_config.ini", ".doorstop_version"):
        if (lab / name).is_file():
            shutil.copy2(lab / name, runtime / name)
    if (lab / "doorstop_libs").is_dir():
        shutil.copytree(lab / "doorstop_libs", runtime / "doorstop_libs", dirs_exist_ok=True)
    settings = bepinex / "config/BepInEx.cfg"
    cfg = configparser.ConfigParser(interpolation=None); cfg.optionxform = str
    cfg.read(settings, encoding="utf-8-sig")
    cfg["Logging.Console"]["Enabled"] = "false"
    cfg["Logging.Disk"]["AppendLog"] = "false"
    with settings.open("w", encoding="utf-8", newline="\n") as stream:
        cfg.write(stream)
    require(len(list((bepinex / "plugins").rglob("DeepNorthCompat.dll"))) == 1, "Runtime needs exactly one installed compatibility DLL")
    print(f"Prepared disposable runtime: {runtime}", flush=True)
    if not args.smoke:
        return
    environment = dict(os.environ)
    # The official dedicated launch script uses the game App ID, rather than
    # the tool ID in steam_appid.txt (which can also have a terminating NUL).
    environment["SteamAppId"] = "892970"
    private = runtime / "private-state"; private.mkdir(exist_ok=True)
    for variable, subdir in (("APPDATA", "roaming"), ("LOCALAPPDATA", "local"), ("USERPROFILE", "home"), ("HOME", "home")):
        directory = private / subdir; directory.mkdir(exist_ok=True); environment[variable] = str(directory)
    if os.name != "nt":
        native = runtime / "doorstop_libs/libdoorstop_x64.so"
        require(native.is_file(), "Linux Doorstop library missing")
        # Doorstop 4 names; the 3.x DOORSTOP_ENABLE/DOORSTOP_INVOKE_DLL_PATH are ignored.
        libraries = [str(runtime / "linux64"), str(native.parent)] + [p for p in environment.get("LD_LIBRARY_PATH", "").split(":") if p]
        environment.update(LD_LIBRARY_PATH=":".join(libraries), LD_PRELOAD=str(native), DOORSTOP_ENABLED="1",
                           DOORSTOP_TARGET_ASSEMBLY=str(bepinex / "core/BepInEx.Preloader.dll"))
    command = [str(executable), "-batchmode", "-nographics", "-name", "DeepNorthCompat local test",
               "-world", "DeepNorthCompatSmoke", "-password", secrets.token_hex(12), "-port", str(args.port),
               "-public", "0", "-savedir", str(private / "saves"), "-logFile", str(runtime / "unity.log")]
    options = {"creationflags": subprocess.CREATE_NO_WINDOW} if os.name == "nt" else {"start_new_session": True}
    log = bepinex / "LogOutput.log"
    required = ("Simulation: APPLIED", "OwnerSkills: APPLIED", "Sarkastic.eu Dedicated Simulation installed", "[ValheimTune] 0.7.8 loaded",
                "Removed ValheimCommunityPatch's spawn queue from ZNetScene.CreateObjectsSorted",
                "Removed ValheimCommunityPatch's zone-diff unload from ZNetScene.RemoveObjects")
    ready = False
    with (runtime / "stdout.log").open("w", encoding="utf-8") as output:
        process = subprocess.Popen(command, cwd=runtime, env=environment, stdin=subprocess.PIPE, stdout=output, stderr=subprocess.STDOUT, text=True, **options)
        print(f"Started temporary dedicated server PID {process.pid}; private world, no crossplay, no remote deployment.", flush=True)
        try:
            deadline = time.monotonic() + args.seconds
            while process.poll() is None and time.monotonic() < deadline:
                text = log.read_text(encoding="utf-8", errors="replace") if log.exists() else ""
                unity = (runtime / "unity.log").read_text(encoding="utf-8", errors="replace") if (runtime / "unity.log").exists() else ""
                if all(message in text for message in required) and "Game server connected" in text + unity:
                    ready = True
                    print("Required compatibility modules applied and Steam server initialized.", flush=True)
                    process.stdin.write("save\nstop\n"); process.stdin.flush()
                    break
                time.sleep(0.5)
            if not ready and process.poll() is None:
                print("Startup gate not reached before deadline; stopping the test server.", flush=True)
                process.stdin.write("stop\n"); process.stdin.flush()
            try:
                process.wait(timeout=30)
            except subprocess.TimeoutExpired:
                if os.name == "nt":
                    process.terminate()
                else:
                    os.killpg(process.pid, signal.SIGTERM)
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill(); process.wait(timeout=10)
        finally:
            if process.poll() is None:
                process.kill(); process.wait(timeout=10)
            if process.stdin:
                process.stdin.close()
            print(f"Temporary server stopped; exit {process.returncode}.", flush=True)
    require(ready and process.returncode == 0, f"Local startup validation failed; inspect {log}")


if __name__ == "__main__":
    main()
