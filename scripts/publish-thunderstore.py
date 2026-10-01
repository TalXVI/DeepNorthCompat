"""Upload the validated dist/ ZIP to Thunderstore.

Follows the upload protocol of thunderstore-cli: initiate a multipart upload, PUT each part to its
presigned URL, finish the upload, then submit it with the listing metadata. Reads the service account
token from TCLI_AUTH_TOKEN.
"""

import base64
import hashlib
import json
import os
import urllib.error
import urllib.request

from tooling import ROOT, require

SITE = "https://thunderstore.io/"
TEAM = "Talent"
COMMUNITY = "valheim"
CATEGORIES = ["mods", "server-side", "client-side", "utility", "deep-north-update"]
# Thunderstore refuses urllib's default User-Agent.
USER_AGENT = "DeepNorthCompat-release (+https://github.com/TalXVI/DeepNorthCompat)"


def request(method, url, token=None, body=None, data=None, headers=None):
    headers = {"User-Agent": USER_AGENT, **(headers or {})}
    if token:
        headers["Authorization"] = "Bearer " + token
    if body is not None:
        data = json.dumps(body).encode("utf-8")
        headers["Content-Type"] = "application/json"
    try:
        with urllib.request.urlopen(urllib.request.Request(url, data=data, method=method, headers=headers)) as response:
            return response.headers, response.read()
    except urllib.error.HTTPError as error:
        raise SystemExit(f"FAIL: {method} {url.split('?')[0]} returned {error.code}: "
                         + error.read().decode("utf-8", "replace")) from None


def published(name, version):
    try:
        urllib.request.urlopen(urllib.request.Request(f"{SITE}api/experimental/package/{TEAM}/{name}/{version}/",
                                                      headers={"User-Agent": USER_AGENT}))
        return True
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return False
        raise


def main():
    token = os.environ.get("TCLI_AUTH_TOKEN")
    require(token, "Set TCLI_AUTH_TOKEN to the Thunderstore service account token")
    manifest = json.loads((ROOT / "package/manifest.json").read_text(encoding="utf-8"))
    name, version = manifest["name"], manifest["version_number"]
    # Thunderstore versions are immutable, so a rerun must not try to replace one.
    require(not published(name, version), f"{TEAM}-{name}-{version} is already on Thunderstore")
    archive = ROOT / "dist" / f"{name}-{version}.zip"
    content = archive.read_bytes()

    _, body = request("POST", SITE + "api/experimental/usermedia/initiate-upload/", token,
                      {"filename": archive.name, "file_size_bytes": len(content)})
    upload = json.loads(body)
    uuid = upload["user_media"]["uuid"]
    parts = []
    for part in upload["upload_urls"]:
        chunk = content[part["offset"]:part["offset"] + part["length"]]
        md5 = base64.b64encode(hashlib.md5(chunk).digest()).decode("ascii")
        headers, _ = request("PUT", part["url"], data=chunk, headers={"Content-MD5": md5})
        require(headers.get("ETag"), f"Upload part {part['part_number']} returned no ETag")
        parts.append({"ETag": headers["ETag"], "PartNumber": part["part_number"]})
    request("POST", f"{SITE}api/experimental/usermedia/{uuid}/finish-upload/", token, {"parts": parts})

    _, body = request("POST", SITE + "api/experimental/submission/submit/", token, {
        "author_name": TEAM, "communities": [COMMUNITY], "categories": [],
        "community_categories": {COMMUNITY: CATEGORIES}, "has_nsfw_content": False, "upload_uuid": uuid})
    print("Published", json.loads(body)["package_version"]["download_url"])


if __name__ == "__main__":
    main()
