"""Validate offline role, integration, and target evidence before approving a DLL."""

import json
import re


def require(condition, message):
    if not condition:
        raise ValueError("Offline coverage: " + message)


def read_summary(lines):
    """Read the single aggregate record from a completed suite's output."""
    summaries = [json.loads(line[len("VALIDATION: "):]) for line in lines if line.startswith("VALIDATION: ")]
    require(len(summaries) == 1, "expected one structured validation summary.")
    return summaries[0]


def check_coverage(record, role, allow_inactive=False):
    require(isinstance(record, dict), f"missing BetterMap evidence for {role}.")
    require(record.get("integration") == "BetterMap" and record.get("role") == role, f"wrong integration or role for {role}.")
    collected, passed = record.get("casesCollected"), record.get("casesPassed")
    require(type(collected) is int and type(passed) is int and collected == passed and passed > 0,
            f"incomplete BetterMap cases for {role}.")
    status = record.get("status")
    require(status == "complete" or allow_inactive and status == "inactive", f"BetterMap is {status} for {role}.")
    groups = {"Cartography", "Session", "Persistence", "Portal", "Vehicle"}
    expected = {name: "inactive" if status == "inactive" or role == "server" and name != "Session"
                else "exercised" for name in groups}
    require(record.get("groups") == expected, f"missing or unexpected BetterMap group coverage for {role}.")
    targets = record.get("targets")
    names = {"Valheim"} if status == "inactive" else {"Valheim", "BetterMap"} | ({"Cartography"} if role == "client" else set())
    require(isinstance(targets, dict) and set(targets) == names, f"missing or unexpected targets for {role}.")
    require(all(isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value) for value in targets.values()),
            f"invalid target hashes for {role}.")
    if status == "inactive":
        require(collected == 1, f"inactive {role} evidence must cover only the absence case.")


def check_validation(summary, require_server=False, allow_inactive=False):
    require(isinstance(summary, dict) and summary.get("schemaVersion") == 1, "missing structured validation evidence.")
    roles = summary.get("roles")
    require(isinstance(roles, dict) and "client" in roles and set(roles) <= {"client", "server"}, "missing client role or unknown roles.")
    if require_server:
        require("server" in roles, "packaging requires client and server roles.")
    total = 0
    for role, evidence in roles.items():
        require(isinstance(evidence, dict), f"invalid {role} evidence.")
        passed = evidence.get("testCasesPassed")
        require(type(passed) is int and passed > 0, f"invalid pass count for {role}.")
        integrations = evidence.get("integrations")
        require(isinstance(integrations, dict) and "BetterMap" in integrations, f"missing BetterMap coverage for {role}.")
        check_coverage(integrations["BetterMap"], role, allow_inactive)
        require(passed >= integrations["BetterMap"]["casesPassed"], f"BetterMap count exceeds the {role} total.")
        total += passed
    require(type(summary.get("testCasesPassed")) is int and summary["testCasesPassed"] == total, "aggregate pass count differs from role counts.")
    return summary


def check_approval(approved):
    summary = check_validation(approved.get("offlineValidation"), require_server=True)
    require(approved.get("offlineTestsPassed") == summary["testCasesPassed"], "approval count differs from validation evidence.")
    require(isinstance(approved.get("preloaderSha256"), str) and re.fullmatch(r"[0-9a-f]{64}", approved["preloaderSha256"]),
            "missing preloader DLL identity.")
