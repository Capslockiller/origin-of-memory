#!/usr/bin/env bash
# gate.sh — Defter 4.0 kapısı. Depo kökünden çalıştırılır.
# Kullanım: tools/gate.sh <etiket> [ci|leak|"<dotnet test filtresi>"]
#   ci     (varsayılan) CI süzgeçli küme, tam yönlendirilmiş ortam
#   leak   ci + gerçek kök anlık görüntü farkı (yalnız ana döngü; eşzamanlı koşulmaz)
#   diğer  verilen filtre, aynı ortam (bir şeridin kendi kahin koşusu)
# Kaynak: 3.1.1 kapısı (ops/oom311/gate/oom-gate.sh), bu makinede 689 testle leak=False ölçülmüş biçim.
# Ortam bloğu değişmez: her etiket kendi köklerini alır, gerçek LOCALAPPDATA/APPDATA/USERPROFILE/TEMP'e test dokunmaz.
# Ölçüldü 2026-10-10: ortam içinde `dotnet restore` ŞART — dışarıda restore edilip içeride --no-restore derlenince
# Microsoft.CodeCoverage shim'i bin'e kopyalanmıyor ve test host'u hiçbir şey basmadan 0 dönüyor (sahte yeşil).
# Kural 6: özet satırı olmayan koşu geçmiş sayılmaz (çıkış 3).
set -uo pipefail
LABEL=${1:?etiket}; MODE=${2:-ci}
TREE="$(pwd)"
[ -f "$TREE/Oom.sln" ] || { echo "gate: depo kökünden çalıştır (Oom.sln yok: $TREE)"; exit 2; }
G311="E:/OdenaWorks/30-Kutuphane/Suru/ops/oom311/gate"
ENVROOT="${OOM_GATE_ENVROOT:-$TREE/.scratch/gate}"
ENVD="$ENVROOT/$LABEL"
# Paket önbelleği: 3.1.1 kapısının dolu önbelleği varsa o (ağ gerekmez), yoksa kullanıcının global önbelleği.
if [ -d "$G311/nuget" ]; then NUGET_DIR="$G311/nuget"; else NUGET_DIR="$(cd ~ && pwd)/.nuget/packages"; fi
rm -rf "$ENVD"; mkdir -p "$ENVD"/{localappdata,appdata,profile/AppData/Local,profile/AppData/Roaming,tmp,nuget-http,nuget-plugins,dotnet} "$NUGET_DIR"
w() { cygpath -w "$1"; }
for n in $(compgen -e); do
  case "$n" in CLAUDE*|ANTHROPIC*|CODEX*|OOM_KABUL_*|OOM_INVOKED_BY|OOM_FAKE_NOW|OOM_SCAR_ROOT) unset "$n";; esac
done
NEWPATH=""; IFS=':' read -ra PARTS <<< "$PATH"
for p in "${PARTS[@]}"; do
  if [ -e "$p/claude" ] || [ -e "$p/claude.exe" ] || [ -e "$p/claude.cmd" ]; then :; else NEWPATH="${NEWPATH:+$NEWPATH:}$p"; fi
done
export PATH="$NEWPATH"
export OOM_LOCALAPPDATA="$(w "$ENVD/localappdata")" LOCALAPPDATA="$(w "$ENVD/localappdata")"
export APPDATA="$(w "$ENVD/appdata")"
export OOM_USERPROFILE="$(w "$ENVD/profile")" USERPROFILE="$(w "$ENVD/profile")" HOME="$ENVD/profile"
export TEMP="$(w "$ENVD/tmp")" TMP="$(w "$ENVD/tmp")" TMPDIR="$ENVD/tmp" OOM_TEST_ROOT="$(w "$ENVD/tmp")"
export NUGET_PACKAGES="$(w "$NUGET_DIR")" NUGET_HTTP_CACHE_PATH="$(w "$ENVD/nuget-http")" NUGET_PLUGINS_CACHE_PATH="$(w "$ENVD/nuget-plugins")"
export DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false
export DOTNET_CLI_HOME="$(w "$ENVD/dotnet")" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1 DOTNET_CLI_UI_LANGUAGE=en
CIF='Kabul!=OzelVault&Kabul!=RetrieveSet&Category!=IntentionalRed&FullyQualifiedName!~Oom.Tests.Kabul.LocalGateKabul'
case "$MODE" in ci|leak) FILTER="$CIF";; *) FILTER="($MODE)&$CIF";; esac
echo "gate: etiket=$LABEL kip=$MODE agac=$TREE nuget=$NUGET_PACKAGES"
start=$(date +%s)
if [ "$MODE" = leak ]; then python "$G311/snap.py" snap "$ENVD/before" || exit 2; fi
dotnet restore Oom.sln --disable-build-servers || { echo "gate: restore failed"; exit 2; }
dotnet build Oom.sln --no-restore -c Release --disable-build-servers || { echo "gate: build failed"; exit 2; }
TLOG="$ENVD/test.log"
dotnet test Oom.sln --no-build -c Release --filter "$FILTER" --blame-hang-timeout 10m 2>&1 | tee "$TLOG"; rc=${PIPESTATUS[0]}
if ! grep -q -E "^(Passed!|Failed!)" "$TLOG"; then
  echo "gate: TEST ÖZETİ YOK — koşu geçmiş sayılmaz (exit=$rc, log=$TLOG)"; rc=3
fi
if [ "$MODE" = leak ]; then
  python "$G311/snap.py" diff "$ENVD/before.json"; drc=$?
  [ $drc -eq 3 ] && { echo "gate: LEAK into a real root"; rc=3; }
fi
echo "gate: done exit=$rc sure=$(( $(date +%s) - start ))s"
exit $rc
