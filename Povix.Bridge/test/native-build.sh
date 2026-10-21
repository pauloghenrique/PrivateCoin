#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")/.."
sh native/build.sh
: "${POVIX_MCS:=mcs}"
"$POVIX_MCS" -r:../PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll -r:System.Runtime.Serialization -r:System.Core -r:System.Numerics -out:build/native/BridgeNativeRegression.exe -main:BridgeNativeRegression native/BridgeNative.cs test/BridgeNativeRegression.cs ../Tests/ValidationBatchFixture.cs ../Tests/LegacyConsensusFixture.cs
"$POVIX_MCS" -r:../PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll -r:System.Runtime.Serialization -r:System.Core -r:System.Numerics -out:build/native/MultiAssetNativeRegression.exe -main:MultiAssetNativeRegression native/BridgeNative.cs test/MultiAssetNativeRegression.cs ../Tests/ValidationBatchFixture.cs ../Tests/LegacyConsensusFixture.cs
"$POVIX_MCS" -r:../PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll -r:System.Runtime.Serialization -r:System.Core -r:System.Numerics -out:build/native/ProductionNativeRegression.exe -main:ProductionNativeRegression native/BridgeNative.cs test/ProductionNativeRegression.cs ../Tests/ValidationBatchFixture.cs ../Tests/LegacyConsensusFixture.cs
"$POVIX_MCS" -r:../PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll -r:System.Runtime.Serialization -r:System.Core -r:System.Numerics -out:build/native/ProductionBurnLimitRegression.exe -main:ProductionBurnLimitRegression native/BridgeNative.cs test/ProductionBurnLimitRegression.cs
