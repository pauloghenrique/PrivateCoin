// SPDX-License-Identifier: MIT
pragma solidity 0.8.30;
import {IERC20} from "@openzeppelin/contracts/token/ERC20/IERC20.sol";
import {IERC20Metadata} from "@openzeppelin/contracts/token/ERC20/extensions/IERC20Metadata.sol";
import {SafeERC20} from "@openzeppelin/contracts/token/ERC20/utils/SafeERC20.sol";
import {ReentrancyGuard} from "@openzeppelin/contracts/utils/ReentrancyGuard.sol";
import {EIP712} from "@openzeppelin/contracts/utils/cryptography/EIP712.sol";
import {ECDSA} from "@openzeppelin/contracts/utils/cryptography/ECDSA.sol";

/// @notice Federated BEP-20 custody with delayed redemptions. Transfer fees and rebasing are unsupported.
contract ProductionVault is EIP712, ReentrancyGuard {
    using SafeERC20 for IERC20;
    bytes32 public constant RELEASE_TYPEHASH = keccak256("Release(bytes32 burnId,address recipient,uint256 nativeAmount,uint256 deadline,uint256 nonce)");
    bytes32 public constant PAUSE_TYPEHASH = keccak256("Pause(bool paused,uint256 nonce,uint256 deadline)");
    IERC20 public immutable asset;
    bytes32 public immutable nativeNetwork;
    bytes32 public immutable nativeToken;
    uint256 public immutable scale;
    uint8 public immutable nativeDecimals;
    uint256 public immutable threshold;
    uint256 public immutable reserveCap;
    uint256 public immutable dailyReleaseCap;
    function bridgeVersion() external pure returns(uint256) { return 2; }
    uint256 public constant releaseDelay = 1 days;
    struct PendingRelease { address recipient; uint256 nativeAmount; uint256 executeAfter; }
    mapping(bytes32 => PendingRelease) public pendingReleases;
    mapping(bytes32 => uint256) public releaseNonces;
    uint256 public reservedForRelease;
    event ReleaseQueued(bytes32 indexed burnId, address indexed recipient, uint256 nativeAmount, uint256 executeAfter, uint256 nonce);
    event ReleaseCancelled(bytes32 indexed burnId, address indexed operator);
    event EmergencyPaused(address indexed operator);
    uint256 public accountedReserve;
    uint256 public depositNonce;
    uint256 public pauseNonce;
    bool public paused = true;
    address[] public operators;
    mapping(address => bool) public isOperator;
    mapping(bytes32 => bool) public processedBurns;
    mapping(uint256 => uint256) public releasedPerDay;
    event Deposited(bytes32 indexed depositId, address indexed sender, bytes32 indexed nativeRecipient, uint256 nativeAmount, uint256 assetAmount);
    event Released(bytes32 indexed burnId, address indexed recipient, uint256 nativeAmount, uint256 assetAmount);
    event BridgePaused(bool paused, uint256 nonce);

    constructor(address token, bytes32 network, bytes32 representation, address[] memory members, uint256 quorum, uint256 cap, uint256 dailyCap)
        EIP712("POVIX Asset Bridge", "2") {
        require(block.chainid == 56 || block.chainid == 97 || block.chainid == 31337, "unsupported chain");
        require(token.code.length > 0 && network != bytes32(0) && representation != bytes32(0), "invalid asset or network");
        uint8 precision = IERC20Metadata(token).decimals();
        require(precision <= 18, "unsupported precision");
        require((members.length == 2 || members.length >= 5 && quorum >= 3) && members.length <= 10 && quorum > members.length / 2 && quorum <= members.length, "majority required");
        require(cap > 0 && cap <= uint256(uint64(type(int64).max)) && dailyCap > 0 && dailyCap <= cap, "invalid caps");
        for (uint256 i; i < members.length; ++i) {
            require(members[i] != address(0) && !isOperator[members[i]], "invalid operator");
            isOperator[members[i]] = true; operators.push(members[i]);
        }
        asset = IERC20(token); nativeDecimals = precision < 8 ? precision : 8; scale = 10 ** (precision - nativeDecimals); nativeNetwork = network; nativeToken = representation;
        threshold = quorum; reserveCap = cap; dailyReleaseCap = dailyCap;
    }
    function deposit(uint256 assetAmount, bytes32 nativeRecipient) external nonReentrant returns (bytes32 id) {
        require(!paused, "bridge paused");
        require(assetAmount > 0 && assetAmount % scale == 0 && nativeRecipient != bytes32(0), "invalid deposit or precision dust");
        uint256 nativeAmount = assetAmount / scale;
        require(nativeAmount <= uint256(uint64(type(int64).max)) && accountedReserve + nativeAmount <= reserveCap, "reserve cap");
        uint256 beforeBalance = asset.balanceOf(address(this));
        require(beforeBalance >= accountedReserve * scale, "reserve deficit");
        asset.safeTransferFrom(msg.sender, address(this), assetAmount);
        require(asset.balanceOf(address(this)) == beforeBalance + assetAmount, "fee-on-transfer unsupported");
        accountedReserve += nativeAmount;
        id = keccak256(abi.encode(block.chainid, address(this), ++depositNonce));
        emit Deposited(id, msg.sender, nativeRecipient, nativeAmount, assetAmount);
    }
    // A quorum authorizes a queue entry; no assets leave before the delay.
    function release(bytes32 burnId, address recipient, uint256 nativeAmount, uint256 deadline, bytes[] calldata signatures) external nonReentrant {
        require(!paused, "bridge paused");
        require(burnId != bytes32(0) && !processedBurns[burnId] && pendingReleases[burnId].executeAfter == 0, "burn already queued or processed");
        require(recipient != address(0) && recipient != address(this) && nativeAmount > 0 && nativeAmount <= dailyReleaseCap && nativeAmount <= accountedReserve - reservedForRelease, "invalid release");
        uint256 nonce = releaseNonces[burnId];
        _authorize(keccak256(abi.encode(RELEASE_TYPEHASH, burnId, recipient, nativeAmount, deadline, nonce)), deadline, signatures);
        releaseNonces[burnId] = nonce + 1;
        uint256 ready = block.timestamp + releaseDelay;
        pendingReleases[burnId] = PendingRelease(recipient, nativeAmount, ready);
        reservedForRelease += nativeAmount;
        emit ReleaseQueued(burnId, recipient, nativeAmount, ready, nonce);
    }
    // Any operator may stop a suspicious queue entry. Requeue needs fresh quorum signatures.
    function cancelRelease(bytes32 burnId) external {
        require(isOperator[msg.sender], "operator required");
        PendingRelease memory item = pendingReleases[burnId];
        require(item.executeAfter != 0, "release not queued");
        reservedForRelease -= item.nativeAmount;
        delete pendingReleases[burnId];
        emit ReleaseCancelled(burnId, msg.sender);
    }
    function emergencyPause() external {
        require(isOperator[msg.sender], "operator required");
        paused = true; pauseNonce += 1; emit EmergencyPaused(msg.sender);
    }
    function executeRelease(bytes32 burnId) external nonReentrant {
        require(!paused, "bridge paused");
        PendingRelease memory item = pendingReleases[burnId];
        require(item.executeAfter != 0 && block.timestamp >= item.executeAfter && !processedBurns[burnId], "release not ready");
        uint256 day = block.timestamp / 1 days;
        require(releasedPerDay[day] + item.nativeAmount <= dailyReleaseCap, "daily release cap");
        processedBurns[burnId] = true;
        releasedPerDay[day] += item.nativeAmount;
        reservedForRelease -= item.nativeAmount;
        accountedReserve -= item.nativeAmount;
        delete pendingReleases[burnId];
        uint256 assetAmount = item.nativeAmount * scale;
        uint256 beforeBalance = asset.balanceOf(address(this));
        uint256 beforeRecipient = asset.balanceOf(item.recipient);
        require(beforeBalance >= (accountedReserve + item.nativeAmount) * scale, "reserve deficit");
        asset.safeTransfer(item.recipient, assetAmount);
        require(asset.balanceOf(address(this)) == beforeBalance - assetAmount && asset.balanceOf(item.recipient) == beforeRecipient + assetAmount, "nonstandard transfer unsupported");
        emit Released(burnId, item.recipient, item.nativeAmount, assetAmount);
    }
    function setPaused(bool value, uint256 deadline, bytes[] calldata signatures) external {
        uint256 nonce = pauseNonce;
        _authorize(keccak256(abi.encode(PAUSE_TYPEHASH, value, nonce, deadline)), deadline, signatures);
        pauseNonce = nonce + 1; paused = value; emit BridgePaused(value, nonce);
    }
    function _authorize(bytes32 data, uint256 deadline, bytes[] calldata signatures) private view {
        require(block.timestamp <= deadline && deadline <= block.timestamp + 15 minutes, "authorization expired or too long");
        require(signatures.length >= threshold && signatures.length <= operators.length, "quorum required");
        address previous; bytes32 digest = _hashTypedDataV4(data);
        for (uint256 i; i < signatures.length; ++i) {
            address signer = ECDSA.recover(digest, signatures[i]);
            require(isOperator[signer] && signer > previous, "invalid or duplicate signer order"); previous = signer;
        }
    }
}
