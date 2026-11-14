#!/usr/bin/env bash
# Builds <id>_<version>_amd64.deb for a HopeLab app from its published linux-x64 folder and hopelab-app.json (the
# shared release workflow runs this; it works by hand on any Debian-family machine with dpkg-deb and python3).
#
#   installers/linux/build-deb.sh <published folder> <version> <output folder> <hopelab-app.json>
#
# Layout: the app in /opt/<id> (release.json stamped "package": "deb", so the app updates itself by installing the next
# .deb with apt-get), its commands on PATH through /usr/bin links, a menu entry and an icon.
set -euo pipefail

src="${1:?published folder}"
version="${2:?version}"
out="${3:?output folder}"
config="${4:?hopelab-app.json}"
root="$(mktemp -d)"
trap 'rm -rf "$root"' EXIT

eval "$(python3 - "$config" <<'PY'
import json, shlex, sys
c = json.load(open(sys.argv[1]))
i, d, l = c.get("install", {}), c["desktop"], c.get("linux", {})
def q(v): return shlex.quote(v or "")
print("ID=" + q(c["id"]))
print("NAME=" + q(c["name"]))
print("EXE=" + q(d["executable"]))
print("DESCRIPTION=" + q(c.get("description") or c["name"]))
print("PUBLISHER=" + q(c.get("publisher") or "QuantumHope"))
print("COMMANDS=" + q("\n".join(f"{k} {v}" for k, v in (i.get("commands") or {}).items())))
print("EXTRA_DEPENDS=" + q(l.get("depends")))
print("RECOMMENDS=" + q(l.get("recommends")))
print("CATEGORIES=" + q(l.get("categories") or "Utility;"))
# Shell-quoted twice: once for this eval, once more so they can be pasted into the package scripts as-is.
print("FIRST_RUN_Q=" + q(shlex.quote(i.get("firstRun") or "")))
print("UNINSTALL_NOTE_Q=" + q(shlex.quote(i.get("uninstallNote") or "")))
print("REPO_URL=" + q(c.get("repositoryUrl") or ""))
PY
)"

opt="$root/opt/$ID"
mkdir -p "$opt" "$root/usr/bin" "$root/usr/share/applications" "$root/usr/share/icons/hicolor/256x256/apps" "$root/DEBIAN"
cp -a "$src/." "$opt/"

# The package's own stamp: same release, but updated through apt.
python3 - "$opt/release.json" <<'PY'
import json, sys
path = sys.argv[1]
with open(path) as f:
    stamp = json.load(f)
stamp["package"] = "deb"
with open(path, "w") as f:
    json.dump(stamp, f, indent=2)
PY

while read -r cmd exe; do [ -n "$cmd" ] && ln -s "/opt/$ID/$exe" "$root/usr/bin/$cmd"; done <<< "$COMMANDS"
[ -f "$src/$EXE.png" ] && cp "$src/$EXE.png" "$root/usr/share/icons/hicolor/256x256/apps/$ID.png"
cat > "$root/usr/share/applications/$ID.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=$NAME
Comment=$DESCRIPTION
Exec=/opt/$ID/$EXE
Icon=$ID
Terminal=false
Categories=$CATEGORIES
StartupWMClass=$EXE
EOF

# The .NET runtime's own needs (self-contained apps still load ICU, OpenSSL and zlib from the system), then the app's.
depends="libc6 (>= 2.35), libstdc++6, libgcc-s1, zlib1g, libssl3 | libssl3t64, libfontconfig1, libicu70 | libicu71 | libicu72 | libicu73 | libicu74 | libicu75 | libicu76"
[ -n "$EXTRA_DEPENDS" ] && depends="$depends, $EXTRA_DEPENDS"
recommends="policykit-1 | pkexec${RECOMMENDS:+, $RECOMMENDS}"
# Readable by everyone, writable by root only, whatever the published folder's own modes were (cp -a copies the
# source folder's mode onto /opt/<id>, and a mktemp folder is 700). Execute bits on files are kept as published.
chmod -R u+rwX,go+rX,go-w "$root/opt" "$root/usr"
chmod 0755 "$root/opt" "$opt"

size_kb=$(du -sk "$root" | cut -f1)
{
  echo "Package: $ID"
  echo "Version: $version"
  echo "Section: utils"
  echo "Priority: optional"
  echo "Architecture: amd64"
  echo "Maintainer: $PUBLISHER <noreply@github.com>"
  echo "Installed-Size: $size_kb"
  echo "Depends: $depends"
  echo "Recommends: $recommends"
  [ -n "$REPO_URL" ] && echo "Homepage: $REPO_URL"
  echo "Description: $DESCRIPTION"
  echo " $NAME, self-contained. It updates itself from its GitHub releases by installing"
  echo " the next package, which asks for your password."
} > "$root/DEBIAN/control"

cat > "$root/DEBIAN/postinst" <<EOF
#!/bin/sh
set -e
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t /usr/share/icons/hicolor || true
if [ "\$1" = configure ]; then
  echo "$NAME is in /opt/$ID. Open it from the app menu."
  echo $FIRST_RUN_Q
  for home in /home/*; do
    if [ -d "\$home/.local/share/$ID/app" ]; then
      echo "Note: \$home has $NAME from install.sh too; remove one (install.sh --uninstall) so its commands mean one copy."
    fi
  done
fi
exit 0
EOF

cat > "$root/DEBIAN/postrm" <<EOF
#!/bin/sh
set -e
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
if [ "\$1" = remove ]; then
  echo "$NAME removed. Your own data is untouched."
  echo $UNINSTALL_NOTE_Q
fi
exit 0
EOF
chmod 0755 "$root/DEBIAN/postinst" "$root/DEBIAN/postrm"

mkdir -p "$out"
dpkg-deb --root-owner-group -Zxz --build "$root" "$out/${ID}_${version}_amd64.deb"
dpkg-deb --info "$out/${ID}_${version}_amd64.deb" | sed -n '1,14p'
