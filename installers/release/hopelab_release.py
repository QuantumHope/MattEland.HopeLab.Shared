#!/usr/bin/env python3
"""Helpers the shared release workflow (.github/workflows/app-release.yml) runs against an app's hopelab-app.json.

  hopelab_release.py env <config>                              KEY=value lines for $GITHUB_ENV
  hopelab_release.py version <config> <commit count>           major.minor from the version file, patch = commit count
  hopelab_release.py stamp <config> <version> <rid> <repo> <commit> <folder>
                                                              release.json and hopelab-install.json into a published folder
  hopelab_release.py manifest <config> <version> <tag> <commit> <dist>
                                                              dist/latest.json: per-asset hashes plus the install settings
  hopelab_release.py installers <config> <repo> <shared checkout> <dist>
                                                              install.ps1 / install.sh with this app filled in
"""
import datetime
import hashlib
import json
import os
import re
import sys


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def install_settings(c):
    """What the installers need, also kept in the app folder (hopelab-install.json) so uninstalling works offline."""
    s = dict(c.get("install", {}))
    # The app's identity, so an installer fetched by repository name still finds and names the install the same way
    # setup.exe and the release's own copy of the script do.
    s["id"] = c["id"]
    s["name"] = c["name"]
    s["executable"] = c["desktop"]["executable"]
    s.setdefault("description", c.get("description") or c["name"])
    s.setdefault("categories", c.get("linux", {}).get("categories") or "Utility;")
    return s


def cmd_env(config):
    c = load(config)
    d = c["desktop"]
    w = c.get("windows", {})
    i = c.get("install", {})
    values = {
        "APP_NAME": c["name"],
        "APP_ID": c["id"],
        "APP_DESCRIPTION": c.get("description") or c["name"],
        "APP_PUBLISHER": c.get("publisher") or "QuantumHope",
        "DESKTOP_PROJECT": d["project"],
        "DESKTOP_EXE": d["executable"],
        "DESKTOP_ICON": d.get("icon") or "",
        "DESKTOP_PNG": d.get("png") or "",
        "AOT_PROJECTS": " ".join(c.get("aotProjects") or []),
        "SETUP_APP_ID": w.get("setupAppId") or "",
        "ADD_TO_PATH": "1" if i.get("addToPath") else "0",
        "STOP_ARGS": "|".join([i["stop"][0], " ".join(i["stop"][1:])]) if i.get("stop") else "",
        "UNINSTALL_ARGS": "|".join([i["beforeUninstall"][0], " ".join(i["beforeUninstall"][1:])]) if i.get("beforeUninstall") else "",
        "PROCESSES": ",".join(dict.fromkeys((i.get("processes") or []) + [d["executable"]])),
        "UPDATE_COMMAND": " ".join(i.get("updateCommand") or []),
        "TEST_SOLUTION": c.get("test", {}).get("solution") or "",
        "TEST_ARGUMENTS": c.get("test", {}).get("arguments") or "",
    }
    for k, v in values.items():
        if "\n" in v:
            raise SystemExit(f"{k} has a line break")
        print(f"{k}={v}")


def cmd_version(config, count):
    c = load(config)
    path = c.get("versionFile") or "Directory.Build.props"
    base = "0.1"
    if os.path.exists(path):
        m = re.search(r"<Version>\s*(\d+)\.(\d+)", open(path, encoding="utf-8").read())
        if m:
            base = f"{m.group(1)}.{m.group(2)}"
    print(f"{base}.{int(count)}")


def cmd_stamp(config, version, rid, repo, commit, folder):
    c = load(config)
    stamp = {
        "app": c["name"], "version": version, "rid": rid, "repository": repo, "commit": commit,
        "builtAt": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }
    with open(os.path.join(folder, "release.json"), "w", encoding="utf-8") as f:
        json.dump(stamp, f, indent=2)
    with open(os.path.join(folder, "hopelab-install.json"), "w", encoding="utf-8") as f:
        json.dump(install_settings(c), f, indent=2)


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def cmd_manifest(config, version, tag, commit, dist):
    c = load(config)
    name, app_id = c["name"], c["id"]
    # Keyed by what a copy updates from: its RID (an archive swapped in place), or RID.deb (installed with apt-get).
    # The setup.exe isn't listed: a setup install is a folder like any other and updates from the zip.
    wanted = {
        "win-x64": f"{name}-{version}-win-x64.zip",
        "linux-x64": f"{name}-{version}-linux-x64.tar.gz",
        "linux-x64.deb": f"{app_id}_{version}_amd64.deb",
    }
    assets = {}
    for key, file in wanted.items():
        path = os.path.join(dist, file)
        if not os.path.exists(path):
            raise SystemExit(f"missing {file}")
        assets[key] = {"name": file, "sha256": sha256(path), "size": os.path.getsize(path)}
    manifest = {
        "app": name, "version": version, "tag": tag, "commit": commit,
        "pubDate": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "assets": assets,
        "install": install_settings(c),
    }
    with open(os.path.join(dist, "latest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)


def cmd_installers(config, repo, shared, dist):
    c = load(config)
    for script in ("install.ps1", "install.sh"):
        text = open(os.path.join(shared, "installers", script), encoding="utf-8").read()
        text = (text.replace("__HOPELAB_REPO__", repo)
                    .replace("__HOPELAB_NAME__", c["name"])
                    .replace("__HOPELAB_ID__", c["id"]))
        with open(os.path.join(dist, script), "w", encoding="utf-8", newline="\n") as f:
            f.write(text)


if __name__ == "__main__":
    commands = {"env": cmd_env, "version": cmd_version, "stamp": cmd_stamp, "manifest": cmd_manifest, "installers": cmd_installers}
    if len(sys.argv) < 2 or sys.argv[1] not in commands:
        raise SystemExit(__doc__)
    commands[sys.argv[1]](*sys.argv[2:])
