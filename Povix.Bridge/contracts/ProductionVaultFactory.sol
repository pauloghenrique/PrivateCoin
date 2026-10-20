// SPDX-License-Identifier: MIT
pragma solidity 0.8.30;
import {ProductionVault} from "./ProductionVault.sol";
import {EIP712} from "@openzeppelin/contracts/utils/cryptography/EIP712.sol";
import {ECDSA} from "@openzeppelin/contracts/utils/cryptography/ECDSA.sol";
import {Strings} from "@openzeppelin/contracts/utils/Strings.sol";

/// @notice A single canonical production vault per source token. New vaults begin paused.
contract ProductionVaultFactory is EIP712 {
    bytes32 public constant ASSET_TYPEHASH = keccak256("Asset(address asset,bytes32 codeHash,uint256 cap,uint256 dailyCap,uint256 deadline)");
    mapping(address => bool) public isOperator;
    bytes32 public immutable nativeNetwork;
    address[] public operators;
    uint256 public immutable threshold;
    uint256 public immutable reserveCap;
    uint256 public immutable dailyReleaseCap;
    address public immutable primaryAsset;
    mapping(address => address) public vaults;
    event VaultCreated(address indexed asset, address indexed vault, bytes32 indexed nativeToken);
    constructor(address primaryVault) EIP712("POVIX Asset Factory", "2") {
        require(block.chainid == 56 || block.chainid == 97 || block.chainid == 31337, "unsupported chain");
        ProductionVault primary = ProductionVault(primaryVault);
        require(primary.nativeToken() == 0xc834c6b8054805426ae71bc9b71815feaa6728985ed403706ee26e12094d6a0d, "primary pUSDT required");
        nativeNetwork = primary.nativeNetwork(); threshold = primary.threshold();
        reserveCap = primary.reserveCap(); dailyReleaseCap = primary.dailyReleaseCap();
        // The primary vault already validates a 3..10 member majority committee.
        for (uint256 i; i < 10; ++i) {
            try primary.operators(i) returns (address member) { operators.push(member); isOperator[member] = true; } catch { break; }
        }
        require(primary.bridgeVersion() == 2 && operators.length >= 5 && threshold > operators.length / 2, "invalid committee");
        primaryAsset = address(primary.asset()); vaults[primaryAsset] = primaryVault;
    }
    function representation(address asset) public view returns(bytes32) {
        if (asset == primaryAsset) return 0xc834c6b8054805426ae71bc9b71815feaa6728985ed403706ee26e12094d6a0d;
        bytes32 digest = sha256(bytes(string.concat("povix-bnb-token-v1|", Strings.toString(block.chainid), "|", Strings.toHexString(asset))));
        return bytes32((uint256(digest) & ((uint256(1) << 240) - 1)) | (uint256(0xb9b0) << 240));
    }
    function createVault(address asset, bytes32 codeHash, uint256 cap, uint256 dailyCap, uint256 deadline, bytes[] calldata signatures) external returns(address) {
        require(block.timestamp <= deadline && deadline <= block.timestamp + 15 minutes, "authorization expired or too long");
        require(asset.code.length > 0 && asset.codehash == codeHash, "asset code mismatch");
        require(cap > 0 && cap <= uint256(uint64(type(int64).max)) && dailyCap > 0 && dailyCap <= cap, "invalid asset limits");
        require(signatures.length >= threshold && signatures.length <= operators.length, "quorum required");
        bytes32 digest = _hashTypedDataV4(keccak256(abi.encode(ASSET_TYPEHASH, asset, codeHash, cap, dailyCap, deadline)));
        address previous;
        for (uint256 i; i < signatures.length; ++i) {
            address member = ECDSA.recover(digest, signatures[i]);
            require(isOperator[member] && member > previous, "invalid signer order"); previous = member;
        }
        require(vaults[asset] == address(0), "asset already registered");
        bytes32 token = representation(asset);
        address vault = address(new ProductionVault(asset, nativeNetwork, token, operators, threshold, cap, dailyCap));
        vaults[asset] = vault; emit VaultCreated(asset, vault, token); return vault;
    }
}
