using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PrivateCoin.Core;
using Povix.Dex.Models;
using Povix.Dex.Services;

internal static class PovixDexPublicLiquidityMain
{
    public static int Main(string[] args)
    { try { PovixDexRegression.RunPublicLiquidity(args[0], args[1]); return 0; } catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
}
internal static partial class PovixDexRegression
{
    public static void RunPublicLiquidity(string directory, string node)
    {
        Directory.CreateDirectory(directory);
        using (var provider = new Wallet()) using (var buyer = new Wallet()) using (var stranger = new Wallet())
        using (var v1 = new Wallet()) using (var v2 = new Wallet())
        {
            string address = provider.CreateReceiveAddress(), buyerAddress = buyer.CreateReceiveAddress(), strangerAddress = stranger.CreateReceiveAddress();
            string validatorAddress1 = v1.CreateReceiveAddress(), validatorAddress2 = v2.CreateReceiveAddress();
            var chain = new Blockchain();
            foreach (var destination in new[] { address, buyerAddress, strangerAddress, validatorAddress1, validatorAddress2 }) LegacyConsensusFixture.Fund(chain, destination);
            LegacyConsensusFixture.SelfBlock(chain, v1.CreateStakeLockTransaction(chain, new Transaction[0], validatorAddress1, Blockchain.OneCoin, 0));
            LegacyConsensusFixture.SelfBlock(chain, v2.CreateStakeLockTransaction(chain, new Transaction[0], validatorAddress2, Blockchain.OneCoin, 0));
            var validators = new[] { v1.CreateValidatorStake(validatorAddress1, Blockchain.OneCoin), v2.CreateValidatorStake(validatorAddress2, Blockchain.OneCoin) };
            using (var batches = new ValidationBatchFixture(chain))
            {
                var creation = TokenCreation.Prepare(chain, new Transaction[0], PublicKeys(provider), "Public pool", "PUB", 2, 100000, address, address, 2);
                var token = creation.Complete(creation.InputAddresses.Select(item => SignBytes(provider, item, creation.SigningPayload)));
                batches.Confirm(chain, new[] { token }, validators);
                var transfer = TokenTransfer.Prepare(chain, new Transaction[0], PublicKeys(provider), token.Token.Id, buyerAddress, 10000, address, 2);
                batches.Confirm(chain, new[] { transfer.Complete(transfer.InputAddresses.Select(item => SignBytes(provider, item, transfer.SigningPayload))) }, validators);
                int port = Port();
                using (var peer = new PeerNode(Port(), false))
                {
                    var received = new Dictionary<string, Transaction>();
                    peer.SynchronizationRequested += (s, e) => peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                    peer.TransactionReceived += (s, e) => { lock (received) received[e.Transaction.Id] = e.Transaction; };
                    peer.Start();
                    var service = OpenOfflineService(directory, port);
                    try
                    {
                        peer.ConnectAsync("127.0.0.1", port).GetAwaiter().GetResult(); peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                        Wait(() => service.GetNetwork().CanCreate, "public liquidity synchronizes");
                        RejectExchange(() => service.PreparePool(new CreatePoolViewModel { RewardMode = "invalid" }, PublicKeys(provider), address, "invalid-reward"), "reward_mode_invalid");
                        foreach (var invalid in new[] {
                            new CreatePoolViewModel { RewardMode = "loyalty", LoyaltyFeePercent = "0", LoyaltyMinimumReserve = "100", LoyaltyEarlyExit = "locked" },
                            new CreatePoolViewModel { RewardMode = "loyalty", LoyaltyFeePercent = "100.01", LoyaltyMinimumReserve = "100", LoyaltyEarlyExit = "locked" },
                            new CreatePoolViewModel { RewardMode = "loyalty", LoyaltyFeePercent = "20", LoyaltyMinimumReserve = "0", LoyaltyEarlyExit = "forfeit" },
                            new CreatePoolViewModel { RewardMode = "loyalty", LoyaltyFeePercent = "20", LoyaltyMinimumReserve = "100", LoyaltyEarlyExit = "invalid" } })
                            RejectExchange(() => service.PreparePool(invalid, PublicKeys(provider), address, "invalid-policy"), "loyalty_policy_invalid");
                        var pool = ExchangeData(service.PreparePool(new CreatePoolViewModel { AssetA = "povix", AssetB = token.Token.Id, AmountA = "1", AmountB = "100", AllowUserLiquidity = true, RewardMode = "loyalty", LoyaltyFeePercent = "20", LoyaltyMinimumReserve = "100", LoyaltyEarlyExit = "locked", FeeAtomic = 2 }, PublicKeys(provider), address, "owner"));
                        var signatures = BrowserExchange(directory, node, service, pool, provider, false);
                        string poolId = service.SubmitPoolAsync((string)pool["draftId"], signatures, "owner").GetAwaiter().GetResult();
                        ConfirmPublic(service, peer, received, chain, batches, validators, poolId);
                        Check((bool)ExchangePool(service, poolId)["allowUserLiquidity"], "pool creation persists enabled user liquidity");
                        Check((string)ExchangePool(service, poolId)["rewardMode"] == "loyalty" && service.GetExchangeReceipt(poolId).RewardMode == "loyalty", "reward selection is recorded in catalogue and receipt");
                        var savedPolicy = service.GetExchangeReceipt(poolId).LoyaltyPolicy;
                        Check(savedPolicy.FeeBasisPoints == 2000 && savedPolicy.MinimumReserveAtomic == 10000000000L && savedPolicy.EarlyExit == "locked", "creator-defined bonus percentage, minimum and locked exit are recorded exactly");
                        var forfeitPolicy = ExchangeData(service.PreparePool(new CreatePoolViewModel { AssetA = "povix", AssetB = token.Token.Id, AmountA = "0.1", AmountB = "10", RewardMode = "loyalty", LoyaltyFeePercent = "12,34", LoyaltyMinimumReserve = "0.00000001", LoyaltyEarlyExit = "forfeit", FeeAtomic = 2 }, PublicKeys(provider), address, "forfeit-policy"));
                        Check((int)((Dictionary<string, object>)forfeitPolicy["loyaltyPolicy"])["feeBasisPoints"] == 1234 && (string)((Dictionary<string, object>)forfeitPolicy["loyaltyPolicy"])["earlyExit"] == "forfeit", "creator can choose forfeiture and exact fractional percentages");
                        var request = new LiquidityChangeViewModel { PoolId = poolId, Operation = "add", Shares = "10000000", FeeAtomic = 2 };
                        var add = ExchangeData(service.PrepareLiquidityChange(request, PublicKeys(buyer), buyerAddress, "buyer"));
                        signatures = BrowserExchange(directory, node, service, add, buyer, false);
                        RejectExchange(() => service.SubmitLiquidityChangeAsync((string)add["draftId"], signatures, "other-session").GetAwaiter().GetResult(), "draft_session_changed");
                        string addId = service.SubmitLiquidityChangeAsync((string)add["draftId"], signatures, "buyer").GetAwaiter().GetResult();
                        Check(addId == service.SubmitLiquidityChangeAsync((string)add["draftId"], signatures, "buyer").GetAwaiter().GetResult(), "liquidity submission is idempotent");
                        var added = ConfirmPublic(service, peer, received, chain, batches, validators, addId);
                        Check(added.Outputs[0].Amount == 110000000 && added.Outputs[1].Amount == 11000 && added.PoolParticipation.Shares[buyerAddress] == 10000000, "addition conserves the ratio and assigns shares to the signer");
                        RejectExchange(() => service.PrepareReserveOperation(poolId, "close", PublicKeys(provider), 2, 2, "owner"), "pool_has_providers");
                        var reserves = chain.GetUnspentOutputs(new[] { address }).Where(item => item.TransactionId == addId).OrderBy(item => item.OutputIndex).Take(2).ToArray();
                        var forgedClose = TokenExchange.PrepareReserveOperation(chain, new Transaction[0], PublicKeys(provider), reserves[0], reserves[1], address, 2, true);
                        var closeTx = forgedClose.Complete(forgedClose.Sources.Select(item => SignBytes(provider, item.Output.OneTimeAddress, forgedClose.SigningPayload)));
                        RejectPublic(() => chain.ValidatePendingTransactions(new[] { closeTx }), "consensus rejects provider closure that steals other shares");
                        RejectPublic(() => TokenExchange.PrepareLiquidityChange(chain, new Transaction[0], PublicKeys(stranger), reserves[0], reserves[1], PublicKeys(provider).First(key => AddressForTest(key) == address), strangerAddress, 1, false, 2), "non-holder cannot withdraw");
                        var providerKey = PublicKeys(provider).First(key => AddressForTest(key) == address);
                        var withdrawal = TokenExchange.PrepareLiquidityChange(chain, new Transaction[0], PublicKeys(buyer), reserves[0], reserves[1], providerKey, buyerAddress, 10000000, false, 2);
                        var withdrawalTx = withdrawal.Complete(withdrawal.Sources.Take(withdrawal.UserInputCount).Select(item => SignBytes(buyer, item.Output.OneTimeAddress, withdrawal.SigningPayload)));
                        var malicious = CloneExchangeData(withdrawalTx); malicious.Outputs[2].OneTimeAddress = strangerAddress;
                        RejectAutomaticSwap(chain, malicious, buyer, withdrawal.UserInputCount, "signed withdrawal cannot redirect another holder's payout");
                        malicious = CloneExchangeData(withdrawalTx); malicious.PoolParticipation.Shares[address]--;
                        RejectAutomaticSwap(chain, malicious, buyer, withdrawal.UserInputCount, "signed withdrawal cannot change another holder's shares");
                        RejectPublic(() => TokenExchange.PrepareLiquidityChange(chain, new Transaction[0], PublicKeys(provider), reserves[0], reserves[1], providerKey,
                            address, 100000000, false, 2), "creator must retain shares until pool closure");
                        var swap = TokenExchange.PrepareSwap(chain, new Transaction[0], PublicKeys(buyer), reserves[0], reserves[1], providerKey, 0, 1000000, 1, buyerAddress, 2);
                        var swapTx = swap.Complete(swap.Sources.Take(swap.UserInputCount).Select(item => SignBytes(buyer, item.Output.OneTimeAddress, swap.SigningPayload)));
                        chain.ValidatePendingTransactions(new[] { swapTx });
                        Check(swapTx.PoolParticipation.Canonical() == added.PoolParticipation.Canonical(), "swaps preserve all provider shares");
                        malicious = CloneExchangeData(swapTx); malicious.PoolParticipation = null;
                        RejectAutomaticSwap(chain, malicious, buyer, swap.UserInputCount, "signed swap cannot disable user liquidity");
                        request.Operation = "remove";
                        var remove = ExchangeData(service.PrepareLiquidityChange(request, PublicKeys(buyer), buyerAddress, "buyer"));
                        signatures = BrowserExchange(directory, node, service, remove, buyer, false);
                        string removeId = service.SubmitLiquidityChangeAsync((string)remove["draftId"], signatures, "buyer").GetAwaiter().GetResult();
                        var removed = ConfirmPublic(service, peer, received, chain, batches, validators, removeId);
                        Check(removed.Outputs[2].Amount == 10000000 && removed.Outputs[3].Amount == 1000 && removed.Outputs[2].OneTimeAddress == buyerAddress && !removed.PoolParticipation.Shares.ContainsKey(buyerAddress), "full holder withdrawal returns only that holder's two assets");
                        var restored = new Blockchain(CloneExchangeData(chain.Blocks.ToArray()));
                        Check(restored.IsValid() && restored.GetUnspentOutputs(new[] { address }).First(item => item.TransactionId == removeId).PoolParticipation.Shares[address] == 100000000, "participation survives blockchain serialization and replay");
                        service.Dispose(); service = OpenOfflineService(directory, port);
                        Check(service.GetExchangeReceipt(poolId).LoyaltyPolicy.EarlyExit == "locked" && service.GetExchangeReceipt(poolId).LoyaltyPolicy.FeeBasisPoints == 2000, "bonus policy survives DEX restart");
                        Check((string)ExchangePool(service, poolId)["rewardMode"] == "loyalty", "reward selection survives reserve changes and DEX restart");
                        Check((bool)ExchangePool(service, poolId)["allowUserLiquidity"] && (bool)ExchangePool(service, poolId)["canManage"], "DEX restart preserves permission and permits closure after other holders exit");
                    }
                    finally { service.Dispose(); }
                }
            }
        }
        Console.WriteLine(checks + " public liquidity checks passed");
    }
    private static string AddressForTest(string key) => (string)typeof(Blockchain).Assembly.GetType("PrivateCoin.Core.Crypto").GetMethod("Sha256", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, new object[] { key });
    private static void RejectPublic(Action action, string label)
    { bool rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; } Check(rejected, label); }
    private static Transaction ConfirmPublic(TokenNetworkService service, PeerNode peer, Dictionary<string, Transaction> received, Blockchain chain, ValidationBatchFixture batches, ValidatorStake[] validators, string id)
    {
        Wait(() => { lock (received) return received.ContainsKey(id); }, "liquidity operation propagates to peer");
        Transaction tx; lock (received) tx = received[id];
        batches.Confirm(chain, new[] { tx }, validators); peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
        Wait(() => service.GetExchangeReceipt(id).Status == "confirmed", "liquidity operation confirms"); return tx;
    }
}
