#!/usr/bin/env bash
# gate.sh — Defter 4.0 kapısı. Depo kökünden çalıştırılır.
# Kullanım: tools/gate.sh <etiket> [filtre]
#   etiket  bu koşunun adı; her etiket kendi .scratch/gate/<etiket> kökünü alır
#   filtre  isteğe bağlı dotnet test --filter ifadesi (yoksa tüm testler)
# Gerçek makine köklerine (LOCALAPPDATA, APPDATA, USERPROFILE, TEMP) hiçbir test dokunmaz:
# hepsi ağacın içine yönlendirilir. NuGet paket önbelleği gerçek yerinde kalır (ağ yok, --no-restore).
set -uo pipefail
LABEL=${1:?etiket}; FILTER=${2:-}
TREE="$(pwd)"
[ -f "$TREE/Oom.sln" ] || { echo "gate: depo kökünden çalıştır (Oom.sln yok: $TREE)"; exit 2; }
ENVD="$TREE/.scratch/gate/$LABEL"
rm -rf "$ENVD"; mkdir -p "$ENVD"/{localappdata,appdata,profile/AppData/Local,profile/AppData/Roaming,tmp,dotnet}
w() { cygpath -w "$1" 2>/dev/null || printf '%s' "$1"; }
REAL_NUGET="${NUGET_PACKAGES:-$(w "$HOME/.nuget/packages")}"
for n in $(compgen -e); do
  case "$n" in CLAUDE*|ANTHROPIC*|CODEX*|OOM_KABUL_*|OOM_INVOKED_BY|OOM_FAKE_NOW|OOM_SCAR_ROOT) unset "$n";; esac
done
export OOM_LOCALAPPDATA="$(w "$ENVD/localappdata")" LOCALAPPDATA="$(w "$ENVD/localappdata")"
export APPDATA="$(w "$ENVD/appdata")"
export OOM_USERPROFILE="$(w "$ENVD/profile")" USERPROFILE="$(w "$ENVD/profile")" HOME="$ENVD/profile"
export TEMP="$(w "$ENVD/tmp")" TMP="$(w "$ENVD/tmp")" TMPDIR="$ENVD/tmp" OOM_TEST_ROOT="$(w "$ENVD/tmp")"
export NUGET_PACKAGES="$REAL_NUGET"
export DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false DOTNET_CLI_HOME="$(w "$ENVD/dotnet")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1 DOTNET_CLI_UI_LANGUAGE=en
CIF='Kabul!=OzelVault&Kabul!=RetrieveSet&Category!=IntentionalRed&FullyQualifiedName!~Oom.Tests.Kabul.LocalGateKabul'
if [ -n "$FILTER" ]; then F="($FILTER)&$CIF"; else F="$CIF"; fi
echo "gate: etiket=$LABEL agac=$TREE"
start=$(date +%s)
dotnet build Oom.sln --no-restore -c Release --disable-build-servers || { echo "gate: build failed"; exit 2; }
dotnet test Oom.sln --no-build -c Release --filter "$F" --blame-hang-timeout 10m; rc=$?
echo "gate: done exit=$rc sure=$(( $(date +%s) - start ))s"
exit $rc
