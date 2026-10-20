// SPDX-License-Identifier: MIT
pragma solidity 0.8.30;
import {ERC20} from "@openzeppelin/contracts/token/ERC20/ERC20.sol";
contract MockUsdt is ERC20 {
    bool public chargeFee;
    constructor() ERC20("TEST ONLY - Mock USD", "TESTUSD") { _mint(msg.sender, 1000000 * 1e18); }
    function setFee(bool value) external { chargeFee = value; }
    function _update(address from, address to, uint256 value) internal override {
        if (chargeFee && from != address(0) && to != address(0) && value > 1) {
            super._update(from, address(0), 1); super._update(from, to, value - 1);
        } else super._update(from, to, value);
    }
}
