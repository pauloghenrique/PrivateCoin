// SPDX-License-Identifier: MIT
pragma solidity 0.8.30;
import {IERC20} from "@openzeppelin/contracts/token/ERC20/IERC20.sol";
import {IERC20Metadata} from "@openzeppelin/contracts/token/ERC20/extensions/IERC20Metadata.sol";
import {SafeERC20} from "@openzeppelin/contracts/token/ERC20/utils/SafeERC20.sol";
import {ReentrancyGuard} from "@openzeppelin/contracts/utils/ReentrancyGuard.sol";
import {EIP712} from "@openzeppelin/contracts/utils/cryptography/EIP712.sol";
import {ECDSA} from "@openzeppelin/contracts/utils/cryptography/ECDSA.sol";

/// @notice Test-only federated BEP-20 custody. Transfer fees and rebasing are unsupported.
contract UsdtVault is EIP712, ReentrancyGuard {
    using SafeERC20 for IERC20;
    bytes32 public constant RELEASE_TYPEHASH = keccak256("Release(bytes32 burnId,address recipient,uint256 nativeAmount,uint256 deadline)");
    bytes32 public constant PAUSE_TYPEHASH = keccak256("Pause(bool paused,uint256 nonce,uint256 deadline)");
    IERC20 public immutable asset;
    bytes32 public immutable nativeNetwork;
    bytes32 public immutable nativeToken;
    uint256 public immutable scale;
    uint8 public immutable nativeDecimals;
    uint256 public immutable threshold;
    uint256 public immutable reserveCap;
    uint256 public immutable dailyReleaseCap;
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
        EIP712("POVIX USDT Bridge", "1") {
        require(block.chainid == 97 || block.chainid == 31337, "test networks only");
        require(token.code.length > 0 && network != bytes32(0) && representation != bytes32(0), "invalid asset or network");
        uint8 precision = IERC20Metadata(token).decimals();
        require(precision <= 18, "unsupported precision");
        require(members.length >= 3 && members.length <= 10 && quorum > members.length / 2 && quorum <= members.length, "majority required");
        require(cap > 0 && dailyCap > 0 && dailyCap <= cap, "invalid caps");
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
    function release(bytes32 burnId, address recipient, uint256 nativeAmount, uint256 deadline, bytes[] calldata signatures) external nonReentrant {
        require(!paused, "bridge paused");
        require(burnId != bytes32(0) && !processedBurns[burnId], "burn already processed or zero");
        require(recipient != address(0) && nativeAmount > 0 && nativeAmount <= accountedReserve, "invalid release");
        _authorize(keccak256(abi.encode(RELEASE_TYPEHASH, burnId, recipient, nativeAmount, deadline)), deadline, signatures);
        uint256 day = block.timestamp / 1 days;
        require(releasedPerDay[day] + nativeAmount <= dailyReleaseCap, "daily release cap");
        processedBurns[burnId] = true;
        releasedPerDay[day] += nativeAmount;
        accountedReserve -= nativeAmount;
        uint256 assetAmount = nativeAmount * scale;
        uint256 beforeBalance = asset.balanceOf(address(this));
        uint256 beforeRecipient = asset.balanceOf(recipient);
        require(recipient != address(this) && beforeBalance >= (accountedReserve + nativeAmount) * scale, "reserve deficit or self release");
        asset.safeTransfer(recipient, assetAmount);
        require(asset.balanceOf(address(this)) == beforeBalance - assetAmount && asset.balanceOf(recipient) == beforeRecipient + assetAmount, "nonstandard transfer unsupported");
        emit Released(burnId, recipient, nativeAmount, nativeAmount * scale);
    }
    function setPaused(bool value, uint256 deadline, bytes[] calldata signatures) external {
        uint256 nonce = pauseNonce;
        _authorize(keccak256(abi.encode(PAUSE_TYPEHASH, value, nonce, deadline)), deadline, signatures);
        pauseNonce = nonce + 1; paused = value; emit BridgePaused(value, nonce);
    }
    function _authorize(bytes32 data, uint256 deadline, bytes[] calldata signatures) private view {
        require(block.timestamp <= deadline, "authorization expired");
        require(signatures.length >= threshold && signatures.length <= operators.length, "quorum required");
        address previous; bytes32 digest = _hashTypedDataV4(data);
        for (uint256 i; i < signatures.length; ++i) {
            address signer = ECDSA.recover(digest, signatures[i]);
            require(isOperator[signer] && signer > previous, "invalid or duplicate signer order"); previous = signer;
        }
    }
}
