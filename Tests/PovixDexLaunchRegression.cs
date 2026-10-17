using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PrivateCoin.Core;
using Povix.Dex.Models;
using Povix.Dex.Services;

internal static class PovixDexLaunchMain
{
    public static int Main(string[] args) { try { PovixDexRegression.RunLaunch(args[0], args[1]); return 0; } catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
}
internal static partial class PovixDexRegression
{
    public static void RunLaunch(string directory, string node)
    {
        Directory.CreateDirectory(directory);
        using (var provider = new Wallet()) using (var buyer = new Wallet()) using (var other = new Wallet()) using (var v1 = new Wallet()) using (var v2 = new Wallet())
        {
            string address = provider.CreateReceiveAddress(), buyerAddress = buyer.CreateReceiveAddress(), otherAddress = other.CreateReceiveAddress(), va = v1.CreateReceiveAddress(), vb = v2.CreateReceiveAddress();
            var chain = new Blockchain();
            foreach (var destination in new[] { address, buyerAddress, otherAddress, va, vb }) LegacyConsensusFixture.Fund(chain, destination);
            LegacyConsensusFixture.SelfBlock(chain, v1.CreateStakeLockTransaction(chain, new Transaction[0], va, Blockchain.OneCoin, 0));
            LegacyConsensusFixture.SelfBlock(chain, v2.CreateStakeLockTransaction(chain, new Transaction[0], vb, Blockchain.OneCoin, 0));
            var validators = new[] { v1.CreateValidatorStake(va, Blockchain.OneCoin), v2.CreateValidatorStake(vb, Blockchain.OneCoin) };
            using (var batches = new ValidationBatchFixture(chain)) using (var peer = new PeerNode(Port(), false))
            {
                var prepared = TokenCreation.Prepare(chain, new Transaction[0], PublicKeys(provider), "Launch token", "LCH", 2, 1000000, address, address, 2, null, new TokenPolicy { Pausable = true, MaximumSupply = 1000000, Administrators = new List<string> { address } });
                var token = prepared.Complete(prepared.InputAddresses.Select(item => SignBytes(provider, item, prepared.SigningPayload))); batches.Confirm(chain, new[] { token }, validators);
                prepared = TokenCreation.Prepare(chain, new Transaction[0], PublicKeys(provider), "Funding token", "FUND", 2, 1000000, address, address, 2);
                var funding = prepared.Complete(prepared.InputAddresses.Select(item => SignBytes(provider, item, prepared.SigningPayload))); batches.Confirm(chain, new[] { funding }, validators);
                var transfer = TokenTransfer.Prepare(chain, new Transaction[0], PublicKeys(provider), funding.Token.Id, buyerAddress, 100000, address, 2);
                batches.Confirm(chain, new[] { transfer.Complete(transfer.InputAddresses.Select(item => SignBytes(provider, item, transfer.SigningPayload))) }, validators);
                var received = new Dictionary<string, Transaction>();
                peer.SynchronizationRequested += (s, e) => peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                peer.TransactionReceived += (s, e) => { lock (received) received[e.Transaction.Id] = e.Transaction; }; peer.Start();
                int port = Port(); var service = OpenOfflineService(directory, port);
                try {
                    peer.ConnectAsync("127.0.0.1", port).GetAwaiter().GetResult(); peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                    Wait(() => service.GetNetwork().CanCreate, "launch DEX synchronizes");
                    var model = new CreateLaunchPoolViewModel { AssetA = token.Token.Id, AssetB = "povix", SaleTokens = "100", LiquidityTokens = "100", InitialContribution = "0.1", Target = "1", DeadlineHeight = chain.Blocks.Count + 100, AllowUserLiquidity = true, FeeAtomic = 2 };
                    var draft = ExchangeData(service.PrepareLaunchPool(model, PublicKeys(provider), address, "create"));
                    var signatures = BrowserExchange(directory, node, service, draft, provider, false);
                    string poolId = service.SubmitPoolAsync((string)draft["draftId"], signatures, "create").GetAwaiter().GetResult();
                    Check(!(bool)ExchangePool(service, poolId)["available"], "pending launch cannot trade");
                    var creation = ConfirmPublic(service, peer, received, chain, batches, validators, poolId);
                    Check((string)ExchangePool(service, poolId)["status"] == "collecting" && !(bool)ExchangePool(service, poolId)["canManage"], "confirmed launch collects without allowing swaps or closure");
                    RejectExchange(() => service.PrepareSwap(new SwapViewModel { PoolId = poolId, Amount = "0.1", MinimumReceive = "1", FeeAtomic = 2 }, PublicKeys(buyer), buyerAddress, "early"), "pool_unavailable");
                    RejectExchange(() => service.PrepareReserveOperation(poolId, "close", PublicKeys(provider), 2, 2, "early"), "pool_unavailable");
                    var reserves = LaunchReserves(chain, address, poolId);
                    string providerKey = PublicKeys(provider).First(key => AddressForTest(key) == address);
                    RejectPublic(() => TokenExchange.PrepareLaunchStep(chain, new Transaction[0], PublicKeys(buyer), reserves[0], reserves[1], providerKey, buyerAddress, 100000000, false, 2), "cannot exceed launch target");
                    RejectPublic(() => TokenExchange.PrepareLaunchStep(chain, new Transaction[0], PublicKeys(buyer), reserves[0], reserves[1], providerKey, buyerAddress, 0, true, 2), "cannot refund before deadline");
                    var forgedSwap = TokenExchange.PrepareSwap(chain, new Transaction[0], PublicKeys(buyer), reserves[0], reserves[1], providerKey, 1, 1000000, 1, buyerAddress, 2);
                    var bad = forgedSwap.Complete(forgedSwap.Sources.Take(forgedSwap.UserInputCount).Select(item => SignBytes(buyer, item.Output.OneTimeAddress, forgedSwap.SigningPayload)));
                    RejectPublic(() => chain.ValidatePendingTransactions(new[] { bad }), "consensus rejects swaps spending launch escrow");
                    var partial = TokenExchange.PrepareLaunchStep(chain, new Transaction[0], PublicKeys(buyer), reserves[0], reserves[1], providerKey, buyerAddress, 40000000, false, 2);
                    var partialTx = partial.Complete(partial.Sources.Take(partial.UserInputCount).Select(item => SignBytes(buyer, item.Output.OneTimeAddress, partial.SigningPayload)));
                    var altered = CloneExchangeData(partialTx); altered.Launch.Target++;
                    RejectAutomaticSwap(chain, altered, buyer, partial.UserInputCount, "signed contributor cannot change the target");
                    altered = CloneExchangeData(partialTx); altered.Launch.DeadlineHeight++;
                    RejectAutomaticSwap(chain, altered, buyer, partial.UserInputCount, "signed contributor cannot extend the deadline");
                    altered = CloneExchangeData(partialTx); altered.Outputs[1].Amount--; altered.Outputs.Last(item => item.AssetId == null).Amount++;
                    RejectAutomaticSwap(chain, altered, buyer, partial.UserInputCount, "signed contribution cannot divert escrow to change");
                    altered = CloneExchangeData(partialTx); altered.Kind = TransactionKind.LaunchActivate;
                    RejectAutomaticSwap(chain, altered, buyer, partial.UserInputCount, "signed contribution cannot open before the target");
                    // Even a fully signed creator transfer cannot drain escrow.
                    bad = new Transaction { Kind = TransactionKind.TokenTransfer, Fee = 2, TimestampUtcTicks = DateTime.UtcNow.Ticks,
                        Inputs = creation.Inputs.Select(item => new TransactionInput { TransactionId = creation.Id, OutputIndex = 0, PublicKey = providerKey }).Take(1).ToList(),
                        Outputs = new List<TransactionOutput> { new TransactionOutput { AssetId = token.Token.Id, Amount = creation.Outputs[0].Amount, OneTimeAddress = address } } };
                    SignCoreExchange(bad, provider); RejectPublic(() => chain.ValidatePendingTransactions(new[] { bad }), "creator cannot spend launch tokens outside launch rules");
                    var request = new LaunchContributionViewModel { PoolId = poolId, Operation = "contribute", Amount = "0.4", FeeAtomic = 2 };
                    draft = ExchangeData(service.PrepareLaunchOperation(request, PublicKeys(buyer), buyerAddress, "buyer"));
                    signatures = BrowserExchange(directory, node, service, draft, buyer, false);
                    RejectExchange(() => service.SubmitLaunchOperationAsync((string)draft["draftId"], signatures, "wrong").GetAwaiter().GetResult(), "draft_session_changed");
                    string partId = service.SubmitLaunchOperationAsync((string)draft["draftId"], signatures, "buyer").GetAwaiter().GetResult();
                    Check(partId == service.SubmitLaunchOperationAsync((string)draft["draftId"], signatures, "buyer").GetAwaiter().GetResult(), "launch contribution retry is idempotent");
                    Check((string)ExchangePool(service, poolId)["status"] == "busy", "pending contribution locks escrow against concurrent steps");
                    var part = ConfirmPublic(service, peer, received, chain, batches, validators, partId);
                    Check(part.Launch.Raised == 50000000 && part.Kind == TransactionKind.LaunchContribute, "partial contribution does not activate or deliver sale tokens");
                    service.Dispose(); service = OpenOfflineService(directory, port);
                    Check((string)ExchangePool(service, poolId)["status"] == "collecting", "launch contributions persist across DEX restart");
                    peer.ConnectAsync("127.0.0.1", port).GetAwaiter().GetResult(); peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult(); Wait(() => service.GetNetwork().CanCreate, "launch restarts and resynchronizes");
                    var beforeOpen = CloneExchangeData(chain.Blocks.ToArray());
                    request.Amount = "0.5";
                    draft = ExchangeData(service.PrepareLaunchOperation(request, PublicKeys(other), otherAddress, "last"));
                    signatures = BrowserExchange(directory, node, service, draft, other, false);
                    reserves = LaunchReserves(chain, address, partId);
                    var completing = TokenExchange.PrepareLaunchStep(chain, new Transaction[0], PublicKeys(other), reserves[0], reserves[1], providerKey, otherAddress, 50000000, false, 2);
                    var completeTx = completing.Complete(completing.Sources.Take(completing.UserInputCount).Select(item => SignBytes(other, item.Output.OneTimeAddress, completing.SigningPayload)));
                    altered = CloneExchangeData(completeTx); altered.Outputs[2].OneTimeAddress = otherAddress;
                    if (altered.Outputs[2].OneTimeAddress == completeTx.Outputs[2].OneTimeAddress) altered.Outputs[2].OneTimeAddress = buyerAddress;
                    RejectAutomaticSwap(chain, altered, other, completing.UserInputCount, "final contributor cannot redirect bought tokens");
                    altered = CloneExchangeData(completeTx); altered.PoolParticipation.Shares[address]++;
                    RejectAutomaticSwap(chain, altered, other, completing.UserInputCount, "final contributor cannot inflate initial liquidity shares");
                    string openId = service.SubmitLaunchOperationAsync((string)draft["draftId"], signatures, "last").GetAwaiter().GetResult();
                    var active = ConfirmPublic(service, peer, received, chain, batches, validators, openId);
                    Check(active.Kind == TransactionKind.LaunchActivate && active.Outputs[0].Amount == 10000 && active.Outputs[1].Amount == 100000000, "last contribution atomically forms the exact AMM reserves");
                    Check(active.Outputs.Skip(2).Any(item => item.AssetId == token.Token.Id && item.OneTimeAddress == buyerAddress && item.Amount == 4000) &&
                        active.Outputs.Skip(2).Any(item => item.AssetId == token.Token.Id && item.OneTimeAddress == otherAddress && item.Amount == 5000), "buyers receive proportional tokens only at activation");
                    Check((bool)ExchangePool(service, poolId)["available"] && chain.GetUnspentOutputs(new[] { address }).Where(item => item.TransactionId == openId && item.OutputIndex < 2).All(item => item.Launch == null && item.IsPoolReserve), "activated launch exposes only normal locked pool reserves");

                    var swapDraft = ExchangeData(service.PrepareSwap(new SwapViewModel { PoolId = poolId, InputSide = 1, Amount = "0.01", MinimumReceive = "0.01", FeeAtomic = 2 }, PublicKeys(buyer), buyerAddress, "swap"));
                    var swapSignatures = BrowserExchange(directory, node, service, swapDraft, buyer, false);
                    string swapId = service.SubmitSwapAsync((string)swapDraft["draftId"], swapSignatures, "swap").GetAwaiter().GetResult(); ConfirmPublic(service, peer, received, chain, batches, validators, swapId);
                    Check((string)ExchangePool(service, poolId)["reserveTransactionId"] == swapId, "normal swaps preserve launch lineage after opening");
                    // A token/token launch exercises exact deadline and trustless full refund.
                    model.AssetB = funding.Token.Id; model.InitialContribution = "10"; model.Target = "100"; model.DeadlineHeight = chain.Blocks.Count + 4;
                    draft = ExchangeData(service.PrepareLaunchPool(model, PublicKeys(provider), address, "failed")); signatures = BrowserExchange(directory, node, service, draft, provider, false);
                    string failedId = service.SubmitPoolAsync((string)draft["draftId"], signatures, "failed").GetAwaiter().GetResult(); ConfirmPublic(service, peer, received, chain, batches, validators, failedId);
                    var failedRequest = new LaunchContributionViewModel { PoolId = failedId, Operation = "contribute", Amount = "20", FeeAtomic = 2 };
                    draft = ExchangeData(service.PrepareLaunchOperation(failedRequest, PublicKeys(buyer), buyerAddress, "token-buyer")); signatures = BrowserExchange(directory, node, service, draft, buyer, false);
                    string failedPartId = service.SubmitLaunchOperationAsync((string)draft["draftId"], signatures, "token-buyer").GetAwaiter().GetResult(); ConfirmPublic(service, peer, received, chain, batches, validators, failedPartId);
                    var pauseDraft = TokenManagement.Prepare(chain, new Transaction[0], PublicKeys(provider), token.Token.Id, "pause", 0, null, address, 2);
                    var pause = pauseDraft.Complete(pauseDraft.InputAddresses.Select(item => SignBytes(provider, item, pauseDraft.SigningPayload)), pauseDraft.AdministratorAddresses.Select(item => SignBytes(provider, item, pauseDraft.SigningPayload)));
                    batches.Confirm(chain, new[] { pause }, validators);
                    Check(chain.GetTokenRuntime(token.Token.Id).Paused, "token administrator can pause trading but cannot block launch restitution");
                    while (chain.Blocks.Count < model.DeadlineHeight) batches.Confirm(chain, new Transaction[0], validators);
                    peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult(); Wait(() => (string)ExchangePool(service, failedId)["status"] == "launch_expired", "deadline disables contributions and enables refunds");
                    RejectExchange(() => service.PrepareLaunchOperation(failedRequest, PublicKeys(buyer), buyerAddress, "late"), "launch_unavailable");
                    failedRequest.Operation = "refund";
                    draft = ExchangeData(service.PrepareLaunchOperation(failedRequest, PublicKeys(other), otherAddress, "refund-payer")); signatures = BrowserExchange(directory, node, service, draft, other, false);
                    reserves = LaunchReserves(chain, address, failedPartId);
                    var returning = TokenExchange.PrepareLaunchStep(chain, new Transaction[0], PublicKeys(other), reserves[0], reserves[1], providerKey, otherAddress, 0, true, 2);
                    var refundTx = returning.Complete(returning.Sources.Take(returning.UserInputCount).Select(item => SignBytes(other, item.Output.OneTimeAddress, returning.SigningPayload)));
                    altered = CloneExchangeData(refundTx); altered.Outputs[1].OneTimeAddress = otherAddress;
                    RejectAutomaticSwap(chain, altered, other, returning.UserInputCount, "refund fee payer cannot redirect principal");
                    string refundId = service.SubmitLaunchOperationAsync((string)draft["draftId"], signatures, "refund-payer").GetAwaiter().GetResult(); var refund = ConfirmPublic(service, peer, received, chain, batches, validators, refundId);
                    Check(refund.Kind == TransactionKind.LaunchRefund && refund.Outputs.Any(item => item.AssetId == funding.Token.Id && item.OneTimeAddress == buyerAddress && item.Amount == 2000) &&
                        refund.Outputs[0].AssetId == token.Token.Id && refund.Outputs[0].Amount == 20000 && refund.Outputs[0].OneTimeAddress == address, "refund returns both buyers' full funding and all creator tokens without creator signature");
                    Check((string)ExchangePool(service, failedId)["status"] == "closed" && !chain.GetUnspentOutputs(new[] { address, buyerAddress }).Any(item => item.TransactionId == refundId && item.IsPoolReserve), "failed launch finishes with free refunded funds and cannot trade");
                    var restored = new Blockchain(CloneExchangeData(chain.Blocks.ToArray())); Check(restored.IsValid(), "launch history restores and independently validates under v21");
                    var fork = new Blockchain(beforeOpen);
                    while (fork.ChainWork <= chain.ChainWork) batches.Confirm(fork, new Transaction[0], validators);
                    peer.BroadcastChainAsync(fork.Blocks).GetAwaiter().GetResult();
                    Wait(() => service.GetExchangeReceipt(openId).BlockHash == null, "chain reorganization revokes orphaned launch activation");
                    Check(!(bool)ExchangePool(service, poolId)["available"] && fork.GetTokenBalance(new[] { buyerAddress, otherAddress }, token.Token.Id) == 0,
                        "orphaned sale tokens and stale trading reserves cannot become spendable");
                } finally { service.Dispose(); }
            }
        }
        Console.WriteLine(checks + " launch checks passed");
    }
    private static UnspentOutput[] LaunchReserves(Blockchain chain, string address, string id) => chain.GetUnspentOutputs(new[] { address }).Where(item => item.TransactionId == id && item.OutputIndex < 2).OrderBy(item => item.OutputIndex).ToArray();
}
