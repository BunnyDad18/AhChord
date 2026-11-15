#!/usr/bin/env bash
# MT-01/MT-02: build the shell APK and publish it to the running AhChord app.
#
# RETIRING (ST-126/MT-07): the APK is a CI artifact — release.yml builds and signs it onto
# the GitHub Release. This script is now only the one-time volume bridge for shells that
# predate MT-07, and the bridge should copy the CI release asset, not a local build.
#
# Publishing = copy the apk + a version sidecar into the app's /data/apk volume:
#   GET /api/app/download serves the newest apk (Settings → About, device browsers),
#   GET /api/app/version  serves its versionCode/versionName (the shell polls it at
#                         startup and offers an in-app update when it advances).
#
# Bump versionCode (and versionName) in android/app/build.gradle, then:
#   ./Mobile/publish-apk.sh
#
# Runs where BOTH the Android toolchain and the app container live. On the build-host
# box (no Android SDK) build on the workstation and publish with docker cp from the
# server — split steps in docs/tasks/MT-05.md.
set -euo pipefail
cd "$(dirname "$0")/android"

# The signing identity lives at ../keystore/debug.keystore — gitignored, NEVER committed
# (debug key only — phones keep installs by signature; losing it means uninstall + data
# loss, so keep a private backup outside the repo, see docs/tasks/MT-05.md).
# SHA256 02:0B:43:1B:EF:48:CC:AF:52:4D:BC:92:81:C5:A4:A9:CA:58:20:5C:5D:98:04:1A:02:BC:41:75:B2:19:3A:13
if [ ! -f "$HOME/.android/debug.keystore" ] && [ -f ../keystore/debug.keystore ]; then
  mkdir -p "$HOME/.android"
  cp ../keystore/debug.keystore "$HOME/.android/debug.keystore"
  echo "restored ~/.android/debug.keystore from the local copy (Mobile/keystore/)"
fi

# Toolchain defaults (Mobile/README): JDK 21 + SDK 35 under ~/Android — the user's
# interactive shell exports ANDROID_HOME, non-login shells (sg, cron, agents) don't.
JAVA_HOME="${JAVA_HOME:-$HOME/Android/jdk21}" ANDROID_HOME="${ANDROID_HOME:-$HOME/Android/sdk}" ./gradlew assembleDebug --console=plain -q

APK=app/build/outputs/apk/debug/app-debug.apk
VC=$(grep -oP 'versionCode \K\d+' app/build.gradle)
VN=$(grep -oP 'versionName "\K[^"]+' app/build.gradle)
NAME="ahchord-android-${VN}.apk"

echo "publishing $NAME (versionCode $VC, $(du -h "$APK" | cut -f1))"
docker cp "$APK" "ahchord-app:/data/apk/$NAME"

TMP="$(mktemp -d)"
printf '{"versionCode": %s, "versionName": "%s"}\n' "$VC" "$VN" > "$TMP/version.json"
docker cp "$TMP/version.json" "ahchord-app:/data/apk/$NAME.version.json"
rm -rf "$TMP"

# FindLatestApk sorts by mtime — keep the volume tidy (older builds are no longer served).
docker exec ahchord-app sh -c 'cd /data/apk && ls -t *.apk 2>/dev/null | tail -n +2 | while read -r old; do rm -f "$old" "$old.version.json"; done; ls -l'

echo -n "server now reports: "
# :5001 = the compose app on the build-host box (published on the LAN for the phone
# app; Traefik serves https://ahchord.local); :5000 = the dotnet dev loop.
curl -fsS "http://localhost:5001/api/app/version" 2>/dev/null \
  || curl -fsS "http://localhost:5000/api/app/version"
echo
