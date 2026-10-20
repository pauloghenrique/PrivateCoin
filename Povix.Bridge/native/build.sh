#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")/.."
: "${POVIX_XBUILD:=xbuild}"
: "${POVIX_MCS:=mcs}"
"$POVIX_XBUILD" ../PrivateCoin.Core/PrivateCoin.Core.csproj /p:Configuration=Release /verbosity:minimal
mkdir -p build/native
"$POVIX_MCS" -r:../PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll -r:System.Runtime.Serialization -r:System.Core -r:System.Security -out:build/native/BridgeNative.exe native/BridgeNative.cs
cp ../PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll build/native/
