#!/usr/bin/env bash
# Install or update a HopeLab app for this user on Linux (x64; tested target: Pop!_OS 22.04+), from its GitHub releases.
#
#   app        ~/.local/share/<id>/app        (--install-dir to change)
#   commands   ~/.local/bin/<command>          (the app's own list, from its release)
#   menu       ~/.local/share/applications/<id>.desktop
#
# Running it again updates in place. The folder never moves, so anything holding an absolute path into it (Claude Code
# hooks, a systemd --user unit) keeps working. After the first install, the app updates itself.
#
# This script is public; the app's releases may not be. For an app with a private repository it uses, in order:
# <APP>_GITHUB_TOKEN / GH_TOKEN / GITHUB_TOKEN, the token the app saved on this machine, the GitHub CLI's sign-in, and
# finally asks (hidden input). A token it had to ask for, or took from the environment, is saved for the app's own
# updates (in the app's data folder, mode 600) unless --no-save-token.
#
#   curl -fsSL https://raw.githubusercontent.com/QuantumHope/MattEland.HopeLab.Shared/main/installers/install.sh | bash -s -- --repo QuantumHope/OneRing
#
# The copy attached to each release already knows its app: `bash install.sh`.
#
# Options:
#   --repo owner/name   the app's repository (not needed for a release's own copy)
#   --version 0.1.412   install that release instead of the latest
#   --no-launch         don't open the app at the end
#   --no-save-token     don't keep the token for the app's updates
#   --install-dir DIR   install somewhere other than ~/.local/share/<id>/app
#   --uninstall         remove the app, its commands and menu entry (keeps its data folder)
set -euo pipefail

# Filled in when the script is attached to a release; the generic copy leaves them as they are.
REPO="__HOPELAB_REPO__"
ID="__HOPELAB_ID__"
NAME="__HOPELAB_NAME__"

VERSION=""
LAUNCH=1
UNINSTALL=0
SAVE_TOKEN=1
APP=""

while [ $# -gt 0 ]; do
  case "$1" in
    --repo) REPO="$2"; ID="__"; NAME="__"; shift 2 ;;   # another app: its name and id come from the repo
    --version) VERSION="${2#v}"; shift 2 ;;
    --no-launch) LAUNCH=0; shift ;;
    --no-save-token) SAVE_TOKEN=0; shift ;;
    --install-dir) APP="$2"; shift 2 ;;
    --uninstall) UNINSTALL=1; shift ;;
    -h|--help) sed -n '2,30p' "$0" 2>/dev/null || true; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

say() { printf '\033[1m%s\033[0m\n' "$*"; }
die() { printf 'install.sh: %s\n' "$*" >&2; exit 1; }

case "$REPO" in __*|"") die "which app? Pass --repo owner/name (QuantumHope/OneRing, QuantumHope/Hymnal)." ;; esac
case "$NAME" in __*) NAME="${REPO#*/}" ;; esac
case "$ID" in __*) ID="$(printf '%s' "$NAME" | tr '[:upper:]' '[:lower:]')" ;; esac
APP="${APP:-$HOME/.local/share/$ID/app}"
BIN="$HOME/.local/bin"
DESKTOP_FILE="$HOME/.local/share/applications/$ID.desktop"
command -v python3 >/dev/null 2>&1 || die "python3 is needed to read the release manifest (sudo apt install python3)."

# ---- the app's install settings (from a release's latest.json, or the copy kept in the app folder) ----
# Sets: EXE, COMMANDS ("name exe" lines), PROCESSES, STOP, BEFORE_UNINSTALL, UNIT, DATA_DIR, TOKEN_VAR, FIRST_RUN,
# UNINSTALL_NOTE, DESCRIPTION, CATEGORIES.
load_settings() { # load_settings <json file> <key: "install" in latest.json, or "" for hopelab-install.json>
  eval "$(python3 - "$1" "$2" "$HOME" <<'PY'
import json, os, shlex, sys
doc = json.load(open(sys.argv[1]))
s = doc.get(sys.argv[2], {}) if sys.argv[2] else doc
home = sys.argv[3]
def q(v): return shlex.quote(v or "")
data = s.get("dataDirectory") or ""
override = os.environ.get(s.get("dataDirectoryVariable") or "", "") if s.get("dataDirectoryVariable") else ""
data_dir = override or (os.path.join(home, data) if data else "")
print("EXE=" + q(s.get("executable") or doc.get("executable") or ""))
print("COMMANDS=" + q("\n".join(f"{k} {v}" for k, v in (s.get("commands") or {}).items())))
print("PROCESSES=" + q(" ".join(s.get("processes") or [])))
print("STOP=" + q(" ".join(shlex.quote(a) for a in (s.get("stop") or []))))
print("BEFORE_UNINSTALL=" + q(" ".join(shlex.quote(a) for a in (s.get("beforeUninstall") or []))))
print("UNIT=" + q(s.get("systemdUserUnit")))
print("DATA_DIR=" + q(data_dir))
print("TOKEN_VAR=" + q(s.get("tokenVariable")))
print("FIRST_RUN=" + q(s.get("firstRun")))
print("UNINSTALL_NOTE=" + q(s.get("uninstallNote")))
print("DESCRIPTION=" + q(s.get("description")))
print("CATEGORIES=" + q(s.get("categories") or "Utility;"))
PY
)"
}

# ---- the app's processes, by the executable they really run ----
# A command line can be the install path, a ~/.local/bin symlink or a relative path; /proc/<pid>/exe is always the real
# file, so that is what identifies a process as this install's.
app_pids() { # app_pids <executable name>
  [ -d "$APP" ] || return 0
  local target pid exe
  target="$(readlink -f "$APP")/$1"
  for pid in /proc/[0-9]*; do
    exe="$(readlink "$pid/exe" 2>/dev/null)" || continue
    [ "$exe" = "$target" ] && echo "${pid#/proc/}"
  done
  return 0
}

# ---- stop what runs from the install folder, and remember what to bring back ----
via_systemd=0
was_running=""
app_was_running=0
stop_app() {
  if [ -n "$UNIT" ] && command -v systemctl >/dev/null 2>&1 && systemctl --user is-active --quiet "$UNIT" 2>/dev/null; then
    via_systemd=1
    systemctl --user stop "$UNIT" || true
  fi
  # The app itself keeps running its old files until it is reopened (Linux frees them then); just remember it.
  [ -n "$(app_pids "$EXE")" ] && app_was_running=1
  local p pids
  for p in $PROCESSES; do
    [ "$p" = "$EXE" ] && continue
    [ -n "$(app_pids "$p")" ] || continue
    was_running="$was_running $p"
    if [ -n "$STOP" ]; then (cd "$APP" && eval "./$STOP" >/dev/null 2>&1) || true; STOP=""; fi   # a clean stop, once
    for _ in 1 2 3 4 5 6 7 8 9 10; do [ -z "$(app_pids "$p")" ] && break; sleep 0.5; done
    pids="$(app_pids "$p")"
    [ -n "$pids" ] && kill $pids 2>/dev/null || true
  done
}

if [ "$UNINSTALL" = 1 ]; then
  [ -f "$APP/hopelab-install.json" ] && load_settings "$APP/hopelab-install.json" "" || { EXE="$NAME"; COMMANDS=""; PROCESSES=""; STOP=""; BEFORE_UNINSTALL=""; UNIT=""; DATA_DIR=""; UNINSTALL_NOTE=""; }
  say "Removing $NAME from $APP"
  if [ -n "$BEFORE_UNINSTALL" ]; then (cd "$APP" && eval "./$BEFORE_UNINSTALL" >/dev/null 2>&1) || true; fi
  stop_app
  pids="$(app_pids "$EXE")"; [ -n "$pids" ] && kill $pids 2>/dev/null || true
  while read -r cmd _; do [ -n "$cmd" ] && rm -f "$BIN/$cmd"; done <<< "$COMMANDS"
  rm -f "$DESKTOP_FILE"
  rm -rf "$APP"
  command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$(dirname "$DESKTOP_FILE")" >/dev/null 2>&1 || true
  echo "Removed.${DATA_DIR:+ Your data in $DATA_DIR is untouched.}"
  [ -n "$UNINSTALL_NOTE" ] && echo "$UNINSTALL_NOTE"
  exit 0
fi

[ "$(uname -s)" = "Linux" ] || die "this installer is for Linux. On Windows use install.ps1."
case "$(uname -m)" in x86_64|amd64) RID="linux-x64" ;; *) die "no $NAME build for $(uname -m) yet (only x86_64)." ;; esac

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# ---- a token, for a private repository ----
# The app's own variable is only known from its manifest, which may itself need the token, so a saved file is found by
# looking in the folder of a previous install's settings.
[ -f "$APP/hopelab-install.json" ] && load_settings "$APP/hopelab-install.json" "" || { DATA_DIR=""; TOKEN_VAR=""; }
app_var="$(printf '%s' "${ID}" | tr '[:lower:]-' '[:upper:]_')_GITHUB_TOKEN"
token="${!app_var:-${GH_TOKEN:-${GITHUB_TOKEN:-}}}"
token_from="environment"
if [ -z "$token" ] && [ -n "$DATA_DIR" ] && [ -s "$DATA_DIR/github-token" ]; then token="$(tr -d '[:space:]' < "$DATA_DIR/github-token")"; token_from="saved"; fi
if [ -z "$token" ] && [ -s "$HOME/.$ID/github-token" ]; then token="$(tr -d '[:space:]' < "$HOME/.$ID/github-token")"; token_from="saved"; fi
if [ -z "$token" ] && command -v gh >/dev/null 2>&1; then
  # gh before 2.17 has no `auth token`; its status line shows the token instead.
  token="$(gh auth token 2>/dev/null || gh auth status --show-token 2>&1 | sed -n 's/.*Token: \([^ *][^ ]*\)$/\1/p' | head -n1 || true)"
  token_from="gh"
fi

api() { # api <url> <accept> <output>
  local auth=()
  [ -n "$token" ] && auth=(-H "Authorization: Bearer $token")
  # curl drops the Authorization header when GitHub redirects an asset to its storage host, as it should.
  curl -fsSL "${auth[@]}" -H "Accept: $2" -H "X-GitHub-Api-Version: 2022-11-28" -o "$3" "$1"
}

if [ -n "$VERSION" ]; then release_url="https://api.github.com/repos/$REPO/releases/tags/v$VERSION"
else release_url="https://api.github.com/repos/$REPO/releases/latest"; fi

say "Finding the ${VERSION:+v$VERSION}${VERSION:-latest} $NAME release"
if ! api "$release_url" "application/vnd.github+json" "$WORK/release.json" 2>/dev/null; then
  if [ -z "$token" ] && [ -r /dev/tty ]; then
    echo "$REPO's releases aren't public. Paste a GitHub token that can read them (a fine-grained token with"
    echo "read-only Contents access to $REPO is enough). It isn't shown as you type."
    read -rs -p "GitHub token: " token < /dev/tty || true
    echo
    token="$(printf '%s' "$token" | tr -d '[:space:]')"
    token_from="prompt"
    api "$release_url" "application/vnd.github+json" "$WORK/release.json" ||
      die "couldn't read $release_url with that token. Check it can read $REPO, and that the release exists."
  elif [ -z "$token" ]; then
    die "couldn't read $REPO's releases. If the repository is private, run 'gh auth login' or set GH_TOKEN, and try again."
  else
    die "couldn't read $release_url with the token found ($token_from). Check it can read $REPO, and that the release exists."
  fi
fi

asset_url() { # asset_url <name>  -> API url of that asset
  python3 -c 'import json,sys; r=json.load(open(sys.argv[1])); print(next((a["url"] for a in r["assets"] if a["name"]==sys.argv[2]), ""))' "$WORK/release.json" "$1"
}

manifest_url="$(asset_url latest.json)"
[ -n "$manifest_url" ] || die "the release has no latest.json."
api "$manifest_url" "application/octet-stream" "$WORK/latest.json"
load_settings "$WORK/latest.json" install
NAME="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("app") or sys.argv[2])' "$WORK/latest.json" "$NAME")"

read -r ver archive sha < <(python3 -c '
import json, sys
m = json.load(open(sys.argv[1])); a = m["assets"][sys.argv[2]]
print(m["version"], a["name"], a["sha256"])' "$WORK/latest.json" "$RID") || die "the release has no $RID build."

archive_url="$(asset_url "$archive")"
[ -n "$archive_url" ] || die "the release lists $archive but doesn't have it."
say "Downloading $NAME $ver ($archive)"
api "$archive_url" "application/octet-stream" "$WORK/$archive"
echo "$sha  $WORK/$archive" | sha256sum -c --quiet - || die "$archive didn't match its checksum; nothing was installed."

mkdir -p "$WORK/new"
tar -xzf "$WORK/$archive" -C "$WORK/new"
[ -x "$WORK/new/$EXE" ] || die "$archive is missing $EXE."

# ---- swap the folder in place ----
installed_before=0
[ -f "$APP/release.json" ] && installed_before=1
stop_app
mkdir -p "$(dirname "$APP")"
rm -rf "$APP.new" "$APP.old"
mv "$WORK/new" "$APP.new"
[ -d "$APP" ] && mv "$APP" "$APP.old"
mv "$APP.new" "$APP"
rm -rf "$APP.old"   # a running copy keeps its files open until it exits; Linux frees them then

# ---- commands and menu entry ----
mkdir -p "$BIN" "$(dirname "$DESKTOP_FILE")"
while read -r cmd exe; do [ -n "$cmd" ] && ln -sfn "$APP/$exe" "$BIN/$cmd"; done <<< "$COMMANDS"
cat > "$DESKTOP_FILE" <<EOF
[Desktop Entry]
Type=Application
Name=$NAME
Comment=$DESCRIPTION
Exec="$APP/$EXE"
Icon=$APP/$EXE.png
Terminal=false
Categories=$CATEGORIES
StartupWMClass=$EXE
EOF
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$(dirname "$DESKTOP_FILE")" >/dev/null 2>&1 || true

# ---- bring back what was running beside the app ----
if [ "$via_systemd" = 1 ]; then
  systemctl --user start "$UNIT" || echo "$UNIT didn't restart: systemctl --user start $UNIT"
else
  for p in $was_running; do (cd "$APP" && nohup setsid "./$p" >/dev/null 2>&1 &) ; done
fi

if [ "$installed_before" = 1 ]; then say "Updated $NAME to $ver in $APP"; else say "Installed $NAME $ver in $APP"; fi
if [ -n "$token" ] && [ "$SAVE_TOKEN" = 1 ] && [ -n "$DATA_DIR" ] && { [ "$token_from" = prompt ] || [ "$token_from" = environment ]; }; then
  mkdir -p "$DATA_DIR"
  # mktemp creates the file 0600 before the token is written; the rename then replaces any older (looser) file.
  tmp="$(mktemp "$DATA_DIR/.github-token.XXXXXX")"
  printf '%s\n' "$token" > "$tmp"
  mv -f "$tmp" "$DATA_DIR/github-token"
  echo "Saved the GitHub token to $DATA_DIR/github-token (readable only by you) so $NAME can update itself."
fi
if [ -n "$COMMANDS" ]; then
  case ":$PATH:" in *":$BIN:"*) ;; *) echo "Add $BIN to your PATH to use $NAME's commands in a terminal (Pop!_OS adds it at your next login)." ;; esac
fi
if [ "$app_was_running" = 1 ]; then
  echo "$NAME is open: quit it and open it again to run $ver."
elif [ "$LAUNCH" = 1 ] && [ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]; then
  echo "Opening $NAME.${FIRST_RUN:+ $FIRST_RUN}"
  nohup setsid "$APP/$EXE" >/dev/null 2>&1 &
else
  echo "Open $NAME from the app menu.${FIRST_RUN:+ $FIRST_RUN}"
fi
