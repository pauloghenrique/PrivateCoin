using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Mvc;
using System.Web.SessionState;
using Povix.Dex.Controllers;
using Povix.Dex.Models;
using Povix.Dex.Services;
using PrivateCoin.Core;

internal static class PovixDexLiquidityMain
{
    public static int Main(string[] args)
    { try { PovixDexRegression.RunLiquidityRegression(args[0], args[1]); return 0; } catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
}

internal static partial class PovixDexRegression
{
    public static void RunLiquidityRegression(string directory, string nodeExecutable)
    {
        Directory.CreateDirectory(directory);
        Check(TokenExchange.Quote(long.MaxValue / 2, long.MaxValue, long.MaxValue / 2) > 0, "quote uses integer arithmetic without multiplication overflow");
        using (var provider = new Wallet()) using (var buyer = new Wallet()) using (var validator1 = new Wallet()) using (var validator2 = new Wallet())
        {
            string address = provider.CreateReceiveAddress(), buyerAddress = buyer.CreateReceiveAddress();
            string v1 = validator1.CreateReceiveAddress(), v2 = validator2.CreateReceiveAddress();
            var chain = new Blockchain();
            foreach (string destination in new[] { address, buyerAddress, v1, v2 }) LegacyConsensusFixture.Fund(chain, destination);
            LegacyConsensusFixture.SelfBlock(chain, validator1.CreateStakeLockTransaction(chain, new Transaction[0], v1, Blockchain.OneCoin, 0));
            LegacyConsensusFixture.SelfBlock(chain, validator2.CreateStakeLockTransaction(chain, new Transaction[0], v2, Blockchain.OneCoin, 0));
            var validators = new[] { validator1.CreateValidatorStake(v1, Blockchain.OneCoin), validator2.CreateValidatorStake(v2, Blockchain.OneCoin) };
            using (var batches = new ValidationBatchFixture(chain))
            {
                var creation = TokenCreation.Prepare(chain, new Transaction[0], PublicKeys(provider), "Pool token A", "PTA", 2, 100000, address, address, 2);
                var tokenA = creation.Complete(creation.InputAddresses.Select(item => SignBytes(provider, item, creation.SigningPayload)));
                batches.Confirm(chain, new[] { tokenA }, validators);
                creation = TokenCreation.Prepare(chain, new Transaction[0], PublicKeys(provider), "Pool token B", "PTB", 2, 200000, address, address, 2);
                var tokenB = creation.Complete(creation.InputAddresses.Select(item => SignBytes(provider, item, creation.SigningPayload)));
                batches.Confirm(chain, new[] { tokenB }, validators);
                var transfer = TokenTransfer.Prepare(chain, new Transaction[0], PublicKeys(provider), tokenA.Token.Id, buyerAddress, 10000, address, 2);
                batches.Confirm(chain, new[] { transfer.Complete(transfer.InputAddresses.Select(item => SignBytes(provider, item, transfer.SigningPayload))) }, validators);
                var forkBeforePools = chain.Blocks.ToArray();
                int dexPort = Port();
                using (var peer = new PeerNode(Port(), false))
                {
                    var received = new Dictionary<string, Transaction>();
                    peer.SynchronizationRequested += (s, e) => peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                    peer.TransactionReceived += (s, e) => { lock (received) received[e.Transaction.Id] = e.Transaction; };
                    peer.Start();
                    var service = OpenOfflineService(directory, dexPort);
                    try
                    {
                        var poolModel = new CreatePoolViewModel { AssetA = "povix", AssetB = tokenA.Token.Id, AmountA = "1", AmountB = "100", FeeAtomic = 2 };
                        RejectExchange(() => service.PreparePool(poolModel, PublicKeys(provider), address, "provider"), "network_not_ready");
                        peer.ConnectAsync("127.0.0.1", dexPort).GetAwaiter().GetResult();
                        peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                        Wait(() => service.GetNetwork().CanCreate, "liquidity DEX synchronizes with a disposable P2P node");
                        var controller = LiquidityControllerFor(service, "provider", new SessionStateItemCollection());
                        var draft = ExchangeData(((JsonResult)controller.PreparePool(poolModel, TransportKeys(provider), address)).Data);
                        Check(controller.Session["Povix.Dex.TokenDraftSession"] != null, "MVC pool preparation retains its session cookie");
                        var poolSignatures = BrowserExchange(directory, nodeExecutable, service, draft, provider, false);
                        RejectExchange(() => service.SubmitPoolAsync((string)draft["draftId"], poolSignatures, "outsider").GetAwaiter().GetResult(), "draft_session_changed");
                        RejectExchange(() => service.SubmitPoolAsync((string)draft["draftId"], new[] { Convert.ToBase64String(new byte[256]) }, "provider").GetAwaiter().GetResult(), "signatures_invalid");
                        var result = ExchangeData(((JsonResult)controller.SubmitPool((string)draft["draftId"], poolSignatures).GetAwaiter().GetResult()).Data);
                        string poolId = (string)result["transactionId"];
                        Check(((string)result["receiptUrl"]).Contains("liquidez/operacao/"), "pool submission returns a dedicated receipt route");
                        Check(service.GetExchangeReceipt(poolId).Status == "pending" && !(bool)ExchangePool(service, poolId)["available"], "pending pool cannot quote or spend unconfirmed reserves");
                        Wait(() => { lock (received) return received.ContainsKey(poolId); }, "pool transaction propagates over the existing P2P protocol");
                        Transaction poolTransaction; lock (received) poolTransaction = received[poolId];
                        Check(poolTransaction.Kind == TransactionKind.TokenTransfer && poolTransaction.Outputs[0].Amount == Blockchain.OneCoin && poolTransaction.Outputs[1].Amount == 10000,
                            "pool separates native POVIX reserves from token quantities without a consensus upgrade");
                        batches.Confirm(chain, new[] { poolTransaction }, validators); peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                        Wait(() => service.GetExchangeReceipt(poolId).Status == "confirmed", "pool opens only after its real confirmation block");
                        var catalogue = ExchangeData(service.GetExchangeCatalogue(PublicKeys(provider)));
                        Check((string)((Dictionary<string, object>)((object[])catalogue["assets"]).Single(item => (string)((Dictionary<string, object>)item)["id"] == "povix"))["balanceAtomic"] ==
                            (chain.GetBalance(new[] { address }) - Blockchain.OneCoin).ToString(), "liquidity reserves do not appear as free provider balance");
                        var secondDraft = ExchangeData(service.PreparePool(new CreatePoolViewModel { AssetA = tokenA.Token.Id, AssetB = tokenB.Token.Id,
                            AmountA = "50", AmountB = "100", FeeAtomic = 2 }, PublicKeys(provider), address, "second-pool"));
                        var secondSignatures = BrowserExchange(directory, nodeExecutable, service, secondDraft, provider, false);
                        string secondPool = service.SubmitPoolAsync((string)secondDraft["draftId"], secondSignatures, "second-pool").GetAwaiter().GetResult();
                        Wait(() => { lock (received) return received.ContainsKey(secondPool); }, "token/token liquidity reaches the peer");
                        Transaction second; lock (received) second = received[secondPool];
                        Check(second.Inputs.All(input => input.TransactionId != poolId || input.OutputIndex > 1), "creating another pool cannot consume registered reserves");
                        batches.Confirm(chain, new[] { second }, validators); peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                        Wait(() => (bool)ExchangePool(service, secondPool)["available"], "two distinct token reserves form another confirmed pool");
                        var swapModel = new SwapViewModel { PoolId = poolId, InputSide = 0, Amount = "0,1", MinimumReceive = "9", FeeAtomic = 2 };
                        var minimumController = LiquidityControllerFor(service, "minimum-limit", new SessionStateItemCollection());
                        var minimumResult = ExchangeData(((JsonResult)minimumController.PrepareSwap(new SwapViewModel { PoolId = poolId,
                            InputSide = 0, Amount = "0,1", MinimumReceive = "10", FeeAtomic = 2 }, TransportKeys(buyer), buyerAddress)).Data);
                        Check(minimumController.Response.StatusCode == 400 && (string)minimumResult["code"] == "preparation_invalid" && !minimumResult.ContainsKey("draftId"),
                            "server rejects a quote below the user minimum before any signature");
                        var swapDraft = ExchangeData(service.PrepareSwap(swapModel, PublicKeys(buyer), buyerAddress, "buyer"));
                        var buyerSignatures = BrowserExchange(directory, nodeExecutable, service, swapDraft, buyer, false);
                        RejectExchange(() => service.SubmitSwapAsync((string)swapDraft["draftId"], buyerSignatures, "buyer").GetAwaiter().GetResult(), "signatures_invalid");
                        var unsignedReserveSignatures = buyerSignatures.Concat(new[] { Convert.ToBase64String(new byte[256]), Convert.ToBase64String(new byte[256]) }).ToArray();
                        RejectExchange(() => service.SubmitSwapAsync((string)swapDraft["draftId"], unsignedReserveSignatures, "buyer").GetAwaiter().GetResult(), "signature_invalid");
                        var concurrent = ExchangeData(service.PrepareSwap(swapModel, PublicKeys(buyer), buyerAddress, "concurrent"));
                        var concurrentSignatures = BrowserExchange(directory, nodeExecutable, service, concurrent, buyer, false);
                        RejectExchange(() => service.ProposeSwap((string)swapDraft["draftId"], buyerSignatures, "another-session"), "draft_session_changed");
                        var wrong = buyerSignatures.ToArray(); wrong[0] = Convert.ToBase64String(new byte[256]);
                        RejectExchange(() => service.ProposeSwap((string)swapDraft["draftId"], wrong, "buyer"), "signature_invalid");
                        service.ProposeSwap((string)swapDraft["draftId"], buyerSignatures, "buyer");
                        service.ProposeSwap((string)concurrent["draftId"], concurrentSignatures, "concurrent");
                        Check(((object[])Json.DeserializeObject(Json.Serialize(service.GetSwapRequests(PublicKeys(buyer))))).Length == 0, "another wallet cannot find provider approval requests");
                        Check(((object[])Json.DeserializeObject(Json.Serialize(service.GetSwapRequests(PublicKeys(provider))))).Length == 2 && chain.GetTokenBalance(new[] { buyerAddress }, tokenA.Token.Id) == 10000,
                            "signed buyer proposal waits for the provider and makes no partial payment");
                        var providerSignatures = BrowserExchange(directory, nodeExecutable, service, swapDraft, provider, true);
                        RejectExchange(() => service.AcceptSwapAsync((string)swapDraft["draftId"], new[] { Convert.ToBase64String(new byte[256]), Convert.ToBase64String(new byte[256]) }).GetAwaiter().GetResult(), "signature_invalid");
                        long beforeBuyerNative = chain.GetBalance(new[] { buyerAddress });
                        long beforeBuyerToken = chain.GetTokenBalance(new[] { buyerAddress }, tokenA.Token.Id);
                        string swapId = service.AcceptSwapAsync((string)swapDraft["draftId"], providerSignatures).GetAwaiter().GetResult();
                        Check(service.AcceptSwapAsync((string)swapDraft["draftId"], providerSignatures).GetAwaiter().GetResult() == swapId, "provider retry is idempotent");
                        RejectExchange(() => service.AcceptSwapAsync((string)concurrent["draftId"], providerSignatures).GetAwaiter().GetResult(), "pool_changed");
                        Check(service.GetExchangeReceipt(swapId).Status == "pending" && !(bool)ExchangePool(service, poolId)["available"] &&
                            chain.GetTokenBalance(new[] { buyerAddress }, tokenA.Token.Id) == beforeBuyerToken, "submitted swap occupies reserves and remains pending until confirmation");
                        Wait(() => { lock (received) return received.ContainsKey(swapId); }, "fully signed atomic swap propagates to the peer");
                        Transaction swap; lock (received) swap = received[swapId];
                        Check(swap.Inputs.Count == ((object[])swapDraft["inputs"]).Length && swap.Outputs[0].Amount == 110000000 &&
                            swap.Outputs[1].Amount == 10000 - long.Parse((string)swapDraft["receiveAtomic"]), "both provider reserves participate in the same exact swap transaction");
                        swap.TransactionApproval = chain.CreateTransactionApproval(swap, new[] { swap }, validators.Take(1));
                        peer.BroadcastAsync(swap).GetAwaiter().GetResult();
                        Wait(() => service.GetExchangeReceipt(swapId).Status == "validated", "swap approval is shown separately from block confirmation");
                        Check(service.GetExchangeReceipt(swapId).Confirmations == 0 && chain.GetTokenBalance(new[] { buyerAddress }, tokenA.Token.Id) == beforeBuyerToken,
                            "validator approval cannot release the swap outputs early");
                        var confirmed = batches.Confirm(chain, new[] { swap }, validators); peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                        Wait(() => service.GetExchangeReceipt(swapId).BlockHash == confirmed.Hash, "swap receipt reports the actual confirming block");
                        Check(chain.IsValid() && chain.GetBalance(new[] { buyerAddress }) == beforeBuyerNative - 10000000 - 2 &&
                            chain.GetTokenBalance(new[] { buyerAddress }, tokenA.Token.Id) == beforeBuyerToken + long.Parse((string)swapDraft["receiveAtomic"]),
                            "confirmed atomic swap preserves every asset and charges the buyer only input amount plus POVIX fee");
                        Check((string)ExchangePool(service, poolId)["reserveTransactionId"] == swapId && (bool)ExchangePool(service, poolId)["available"], "pool tracks its new confirmed reserves");
                        var reverse = ExchangeData(service.PrepareSwap(new SwapViewModel { PoolId = poolId, InputSide = 1, Amount = "5", MinimumReceive = "0,04", FeeAtomic = 2 },
                            PublicKeys(buyer), buyerAddress, "reverse"));
                        BrowserExchange(directory, nodeExecutable, service, reverse, buyer, false);
                        Check((string)((Dictionary<string, object>)((object[])reverse["outputs"])[2])["assetId"] == "povix", "reverse direction returns native POVIX and still requires a native network fee");
                        Check(((object[])Json.DeserializeObject(Json.Serialize(service.GetTransferHistory(PublicKeys(provider), tokenA.Token.Id)))).Length == 0, "liquidity receipts do not enter the restricted creator-transfer history");
                        var tokensDraft = ExchangeData(service.PrepareSwap(new SwapViewModel { PoolId = secondPool, InputSide = 0, Amount = "1", MinimumReceive = "1,9", FeeAtomic = 2 },
                            PublicKeys(buyer), buyerAddress, "token-token"));
                        var tokensUserSignatures = BrowserExchange(directory, nodeExecutable, service, tokensDraft, buyer, false);
                        service.ProposeSwap((string)tokensDraft["draftId"], tokensUserSignatures, "token-token");
                        var tokensProviderSignatures = BrowserExchange(directory, nodeExecutable, service, tokensDraft, provider, true);
                        long oldA = chain.GetTokenBalance(new[] { buyerAddress }, tokenA.Token.Id), oldB = chain.GetTokenBalance(new[] { buyerAddress }, tokenB.Token.Id), oldNative = chain.GetBalance(new[] { buyerAddress });
                        string tokensSwapId = service.AcceptSwapAsync((string)tokensDraft["draftId"], tokensProviderSignatures).GetAwaiter().GetResult();
                        Wait(() => { lock (received) return received.ContainsKey(tokensSwapId); }, "token/token swap propagates with signatures from both wallets");
                        Transaction tokensSwap; lock (received) tokensSwap = received[tokensSwapId];
                        batches.Confirm(chain, new[] { tokensSwap }, validators); peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                        Wait(() => service.GetExchangeReceipt(tokensSwapId).Status == "confirmed", "token/token swap confirms atomically");
                        Check(chain.IsValid() && chain.GetTokenBalance(new[] { buyerAddress }, tokenA.Token.Id) == oldA - 100 &&
                            chain.GetTokenBalance(new[] { buyerAddress }, tokenB.Token.Id) == oldB + long.Parse((string)tokensDraft["receiveAtomic"]) &&
                            chain.GetBalance(new[] { buyerAddress }) == oldNative - 2, "token/token swaps conserve both token supplies and use POVIX only for the fee");
                        var ownNativeSwap = SwapWithSelectedWallet(directory, nodeExecutable, service, chain, peer, received, provider,
                            batches, validators, new SwapViewModel { PoolId = poolId, InputSide = 0, Amount = "0,05", MinimumReceive = "3", FeeAtomic = 2 }, "own-native");
                        ownNativeSwap = SwapWithSelectedWallet(directory, nodeExecutable, service, chain, peer, received, provider,
                            batches, validators, new SwapViewModel { PoolId = poolId, InputSide = 1, Amount = "1", MinimumReceive = "0,01", FeeAtomic = 2 }, "own-native-reverse");
                        tokensSwap = SwapWithSelectedWallet(directory, nodeExecutable, service, chain, peer, received, provider,
                            batches, validators, new SwapViewModel { PoolId = secondPool, InputSide = 0, Amount = "1", MinimumReceive = "1,8", FeeAtomic = 2 }, "own-tokens");
                        tokensSwapId = tokensSwap.Id;
                        var allProviderOutputs = chain.GetUnspentOutputs(new[] { address });
                        var nativeFeeInput = allProviderOutputs.First(output => output.Output.AssetId == null && output.TransactionId != ownNativeSwap.Id);
                        var allowedInputs = new[] { tokensSwapId + ":0", tokensSwapId + ":1", nativeFeeInput.TransactionId + ":" + nativeFeeInput.OutputIndex };
                        var excluded = allProviderOutputs.Where(output => !allowedInputs.Contains(output.TransactionId + ":" + output.OutputIndex))
                            .Select(output => output.TransactionId + ":" + output.OutputIndex).ToArray();
                        var outside = TokenExchange.PrepareLiquidity(chain, new Transaction[0], PublicKeys(provider), tokenA.Token.Id, tokenB.Token.Id,
                            tokensSwap.Outputs[0].Amount, tokensSwap.Outputs[1].Amount, address, 2, excluded);
                        var outsideTransaction = outside.Complete(outside.Sources.Select(output => SignBytes(provider, output.Output.OneTimeAddress, outside.SigningPayload)));
                        Check(outsideTransaction.Inputs.Any(input => input.TransactionId == tokensSwapId && input.OutputIndex == 0), "provider retains control of its own reserves outside the DEX");
                        batches.Confirm(chain, new[] { outsideTransaction }, validators); peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                        Wait(() => (string)ExchangePool(service, secondPool)["status"] == "unavailable", "spending registered reserves outside the DEX disables the pool");
                        Check(!File.ReadAllText(Path.Combine(directory, "dex-network.json")).Contains("<D>"), "persisted liquidity state contains no private RSA keys");
                        service.Dispose(); service = OpenOfflineService(directory, dexPort);
                        Check(service.GetExchangeReceipt(swapId).Status == "confirmed" && (string)ExchangePool(service, poolId)["reserveTransactionId"] == ownNativeSwap.Id &&
                            service.SubmitPoolAsync((string)draft["draftId"], poolSignatures, "provider").GetAwaiter().GetResult() == poolId,
                            "pools, reserve lineage, confirmed receipts and accepted retries survive restart");
                        peer.ConnectAsync("127.0.0.1", dexPort).GetAwaiter().GetResult(); peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                        Wait(() => service.GetNetwork().CanCreate, "liquidity service resynchronizes after restart");
                        var fork = new Blockchain(forkBeforePools);
                        while (fork.ChainWork <= chain.ChainWork) LegacyConsensusFixture.Fund(fork, "liquidity-fork-" + fork.Blocks.Count);
                        peer.BroadcastChainAsync(fork.Blocks).GetAwaiter().GetResult();
                        Wait(() => service.GetExchangeReceipt(poolId).BlockHash == null, "reorganization removes orphaned liquidity confirmations");
                        Check(!(bool)ExchangePool(service, poolId)["available"] && service.GetExchangeReceipt(swapId).Confirmations == 0,
                            "orphaned pools cannot expose stale reserves as spendable");
                    }
                    finally { service.Dispose(); }
                }
            }
        }
        Console.WriteLine(checks + " liquidity checks passed");
    }
    private static Dictionary<string, object> ExchangeData(object data) => (Dictionary<string, object>)Json.DeserializeObject(Json.Serialize(data));
    private static Transaction SwapWithSelectedWallet(string directory, string node, TokenNetworkService service, Blockchain chain, PeerNode peer,
        Dictionary<string, Transaction> received, Wallet wallet, ValidationBatchFixture batches, ValidatorStake[] validators, SwapViewModel model, string owner)
    {
        var addresses = wallet.OwnedOneTimeAddresses.ToArray();
        var balances = chain.GetTokens().ToDictionary(token => token.Id, token => chain.GetTokenBalance(addresses, token.Id));
        long native = chain.GetBalance(addresses);
        var controller = LiquidityControllerFor(service, owner, new SessionStateItemCollection());
        var draft = ExchangeData(((JsonResult)controller.PrepareSwap(model, TransportKeys(wallet), addresses[0])).Data);
        Check(draft.ContainsKey("draftId") && controller.Response.StatusCode == 200, "selected provider wallet can prepare a swap with its own pool");
        var signatures = BrowserExchange(directory, node, service, draft, wallet, false);
        RejectExchange(() => service.SubmitSwapAsync((string)draft["draftId"], signatures, "another-session").GetAwaiter().GetResult(), "draft_session_changed");
        var forged = signatures.ToArray(); forged[forged.Length - 1] = Convert.ToBase64String(new byte[256]);
        RejectExchange(() => service.SubmitSwapAsync((string)draft["draftId"], forged, owner).GetAwaiter().GetResult(), "signature_invalid");
        var result = ExchangeData(((JsonResult)controller.SubmitSwap((string)draft["draftId"], signatures).GetAwaiter().GetResult()).Data);
        Check(controller.Response.StatusCode == 200 && ((string)result["receiptUrl"]).Contains("liquidez/operacao/"), "one wallet sends the complete swap through the MVC endpoint");
        string id = (string)result["transactionId"];
        Check(service.SubmitSwapAsync((string)draft["draftId"], signatures, owner).GetAwaiter().GetResult() == id, "retrying the fully signed swap is idempotent");
        Check(service.GetExchangeReceipt(id).Status == "pending" && chain.GetBalance(addresses) == native &&
            balances.All(item => chain.GetTokenBalance(addresses, item.Key) == item.Value), "same-wallet signing cannot change balances before confirmation");
        Wait(() => { lock (received) return received.ContainsKey(id); }, "same-wallet atomic swap propagates to the network");
        Transaction tx; lock (received) tx = received[id];
        Check(tx.Inputs.Select(input => input.TransactionId + ":" + input.OutputIndex).Distinct().Count() == tx.Inputs.Count &&
            tx.Inputs[tx.Inputs.Count - 2].TransactionId == (string)draft["reserveTransactionId"], "free funding and pool reserves remain distinct inputs for one wallet");
        batches.Confirm(chain, new[] { tx }, validators); peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
        Wait(() => service.GetExchangeReceipt(id).Status == "confirmed", "same-wallet swap confirms without a separate provider request");
        Check(chain.IsValid() && chain.GetBalance(addresses) == native - tx.Fee &&
            balances.All(item => chain.GetTokenBalance(addresses, item.Key) == item.Value), "same-wallet swaps conserve all tokens and deduct only the network fee from the total wallet balance");
        Check((string)ExchangePool(service, model.PoolId)["reserveTransactionId"] == id && (bool)ExchangePool(service, model.PoolId)["available"], "pool tracks confirmed reserves after a same-wallet swap");
        return tx;
    }
    private static Dictionary<string, object> ExchangePool(TokenNetworkService service, string id) => ((object[])ExchangeData(service.GetExchangeCatalogue(new string[0]))["pools"])
        .Cast<Dictionary<string, object>>().Single(item => (string)item["poolId"] == id);
    private static void RejectExchange(Action action, string code)
    { try { action(); throw new Exception("Expected " + code); } catch (TokenOperationException error) { Check(error.Code == code, "liquidity rejects " + code); } }
    private static LiquidityController LiquidityControllerFor(TokenNetworkService service, string session, SessionStateItemCollection items)
    {
        var template = ControllerFor(service, session, items);
        template.ControllerContext.RouteData.Values["controller"] = "Liquidity";
        var controller = new LiquidityController { Url = template.Url };
        controller.ControllerContext = new ControllerContext(template.ControllerContext.RequestContext, controller); return controller;
    }
    private static string[] BrowserExchange(string directory, string node, TokenNetworkService service, Dictionary<string, object> draft, Wallet wallet, bool provider)
    {
        File.WriteAllText(Path.Combine(directory, "exchange-fixture.json"), Json.Serialize(new { wallet = EncryptedWallet(wallet), draft, provider,
            catalogue = service.GetExchangeCatalogue(PublicKeys(wallet)), networkId = Blockchain.NetworkId }));
        using (var process = Process.Start(new ProcessStartInfo(node) { UseShellExecute = false, Arguments = "Tests/PovixDexLiquidityWalletRegression.js " + directory }))
        { process.WaitForExit(); Check(process.ExitCode == 0, "actual liquidity form reviews and signs " + (provider ? "provider approval" : (string)draft["operation"]) + " locally"); }
        return ((object[])Json.DeserializeObject(File.ReadAllText(Path.Combine(directory, "exchange-browser-result.json")))).Cast<string>().ToArray();
    }
}
