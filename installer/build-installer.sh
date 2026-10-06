#!/usr/bin/env bash
set -euo pipefail

mode=Online
if [[ ${1:-} == --offline ]]; then
    mode=Offline
elif [[ $# -gt 0 ]]; then
    echo 'Uso: bash installer/build-installer.sh [--offline]' >&2
    exit 2
fi
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repo_root"
command -v xbuild >/dev/null || { echo 'Instale Mono/xbuild ou ative o toolchain do ambiente.' >&2; exit 1; }
command -v makensis >/dev/null || { echo 'Instale NSIS 3.' >&2; exit 1; }
xbuild PrivateCoin.Desktop/PrivateCoin.Desktop.csproj /p:Configuration=Release /verbosity:minimal

version=$(python3 - "$repo_root" <<'PY'
import pathlib, re, sys
info = (pathlib.Path(sys.argv[1]) / 'PrivateCoin.Desktop/Properties/AssemblyInfo.cs').read_text(encoding='utf-8-sig')
match = re.search(r'\[assembly:\s*AssemblyVersion\("(\d+\.\d+\.\d+\.\d+)"\)\]', info)
if not match:
    raise SystemExit('AssemblyVersion deve conter quatro números explícitos.')
print(match.group(1))
PY
)
output_dir="$repo_root/artifacts/installer"
mkdir -p "$output_dir"
output_file="$output_dir/PrivateCoin.Desktop-$version-Setup-$mode.exe"
args=("-DAPP_VERSION=$version" "-DPAYLOAD_DIR=$repo_root/PrivateCoin.Desktop/bin/Release" "-DOUTPUT_FILE=$output_file")

if [[ $mode == Offline ]]; then
    command -v curl >/dev/null
    runtime_dir="$repo_root/artifacts/prerequisites"
    mkdir -p "$runtime_dir"
    runtime="$runtime_dir/ndp48-x86-x64-allos-enu.exe"
    expected_hash=95889d6de3f2070c07790ad6cf2000d33d9a1bdfc6a381725ab82ab1c314fd53
    if [[ ! -f $runtime ]]; then
        partial=$(mktemp "$runtime_dir/dotnet48-download.XXXXXX")
        trap 'rm -f -- "$partial"' EXIT
        curl --fail --location --show-error --proto '=https' --proto-redir '=https' \
            'https://download.visualstudio.microsoft.com/download/pr/7afca223-55d2-470a-8edc-6a1739ae3252/abd170b4b0ec15ad0222a809b761a036/ndp48-x86-x64-allos-enu.exe' \
            --output "$partial"
        printf '%s  %s\n' "$expected_hash" "$partial" | sha256sum --check --status
        mv -- "$partial" "$runtime"
        trap - EXIT
    fi
    printf '%s  %s\n' "$expected_hash" "$runtime" | sha256sum --check --status
    args+=("-DDOTNET48_INSTALLER=$runtime")
fi

makensis -V3 "${args[@]}" "$repo_root/installer/PrivateCoin.Desktop.nsi"
cd "$output_dir"
sha256sum "$(basename -- "$output_file")" > "$output_file.sha256"
chmod 644 "$output_file" "$output_file.sha256"
printf 'Instalador gerado: %s\n' "$output_file"
