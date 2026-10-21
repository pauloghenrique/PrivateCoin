// SPDX-License-Identifier: MIT
pragma solidity 0.8.30;
import {UsdtVault} from "./UsdtVault.sol";
import {Strings} from "@openzeppelin/contracts/utils/Strings.sol";

/// @notice A single canonical test vault per source token. New vaults begin paused.
contract AssetVaultFactory {
    bytes32 public immutable nativeNetwork;
    address[] public operators;
    uint256 public immutable threshold;
    uint256 public immutable reserveCap;
    uint256 public immutable dailyReleaseCap;
    address public immutable primaryAsset;
    mapping(address => address) public vaults;
    event VaultCreated(address indexed asset, address indexed vault, bytes32 indexed nativeToken);
    constructor(address primaryVault) {
        require(block.chainid == 97 || block.chainid == 31337, "test networks only");
        UsdtVault primary = UsdtVault(primaryVault);
        require(primary.nativeToken() == 0xc834c6b8054805426ae71bc9b71815feaa6728985ed403706ee26e12094d6a0d, "primary pUSDT required");
        nativeNetwork = primary.nativeNetwork(); threshold = primary.threshold();
        reserveCap = primary.reserveCap(); dailyReleaseCap = primary.dailyReleaseCap();
        // The primary vault already validates a 3..10 member majority committee.
        for (uint256 i; i < 10; ++i) {
            try primary.operators(i) returns (address member) { operators.push(member); } catch { break; }
        }
        require(operators.length >= 3 && threshold > operators.length / 2, "invalid committee");
        primaryAsset = address(primary.asset()); vaults[primaryAsset] = primaryVault;
    }
    function representation(address asset) public view returns(bytes32) {
        if (asset == primaryAsset) return 0xc834c6b8054805426ae71bc9b71815feaa6728985ed403706ee26e12094d6a0d;
        bytes32 digest = sha256(bytes(string.concat("povix-bnb-token-v1|", Strings.toString(block.chainid), "|", Strings.toHexString(asset))));
        return bytes32((uint256(digest) & ((uint256(1) << 240) - 1)) | (uint256(0xb9b0) << 240));
    }
    function createVault(address asset) external returns(address) {
        require(vaults[asset] == address(0), "asset already registered");
        bytes32 token = representation(asset);
        address vault = address(new UsdtVault(asset, nativeNetwork, token, operators, threshold, reserveCap, dailyReleaseCap));
        vaults[asset] = vault; emit VaultCreated(asset, vault, token); return vault;
    }
}
