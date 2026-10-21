// SPDX-License-Identifier: MIT
pragma solidity 0.8.30;
import {ERC20} from "@openzeppelin/contracts/token/ERC20/ERC20.sol";
contract MockAsset is ERC20 {
    uint8 private immutable precision;
    constructor(uint8 p) ERC20("TEST ONLY same name", "DUP") { precision = p; _mint(msg.sender, 1000000 * 10 ** p); }
    function decimals() public view override returns(uint8) { return precision; }
}
