using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Mvc;
using System.Web.SessionState;
using Povix.Dex.Controllers;
using Povix.Dex.Models;
using Povix.Dex.Services;
using PrivateCoin.Core;

internal static partial class PovixDexRegression
{
    private static void RunTransfers(string directory, string nodeExecutable, Wallet issuer, Blockchain chain,
        TokenNetworkService service, PeerNode peer, ValidatorStake[] validators, ValidationBatchFixture batches)
    {
        using (var recipient = new Wallet())
        {
            string destination = recipient.CreateReceiveAddress();
            Block reward;
            chain.TryAddWalletCreationReward(destination, out reward);
            peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
            Wait(() => service.GetNetwork().Height == chain.Blocks.Last().Height, "transfer screen reads the synchronized blockchain");
            string[] addresses = issuer.OwnedOneTimeAddresses.ToArray();
            var creation = chain.Blocks.SelectMany(block => block.Transactions).Single(tx => tx.Kind == TransactionKind.TokenCreate);
            string tokenId = creation.Token.Id;
            var list = (object[])Json.DeserializeObject(Json.Serialize(service.GetCreatedTokens(PublicKeys(issuer))));
            Check(list.Length == 1 && (string)((Dictionary<string, object>)list[0])["tokenId"] == tokenId &&
                long.Parse((string)((Dictionary<string, object>)list[0])["balanceAtomic"]) == long.MaxValue, "creator screen lists confirmed token metadata and exact spendable balance");
            var outsiderList = (object[])Json.DeserializeObject(Json.Serialize(service.GetCreatedTokens(PublicKeys(recipient))));
            Check(outsiderList.Length == 0, "another wallet cannot list the token as its own creation");
            var items = new SessionStateItemCollection();
            var controller = TransferControllerFor(service, "transfer-owner", items);
            var model = new TokenTransferViewModel { TokenId = tokenId, Amount = "1,23456789", DestinationAddress = destination, FeePriority = 2 };
            object incompleteWallet;
            Dictionary<string, object> incompleteToken;
            string receivingAddress = creation.Outputs.Single(output => output.AssetId == tokenId).OneTimeAddress;
            string[] originalKeys = issuer.ExportPrivateKeys().Where(xml => {
                using (var rsa = new RSACryptoServiceProvider()) {
                    rsa.PersistKeyInCsp = false; rsa.FromXmlString(xml);
                    using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rsa.ToXmlString(false)))).Replace("-", "").ToLowerInvariant() != receivingAddress;
                }
            }).ToArray();
            using (var desktopOriginal = Wallet.FromPrivateKeys(originalKeys))
            {
                incompleteWallet = EncryptedWallet(desktopOriginal);
                incompleteToken = (Dictionary<string, object>)((object[])Json.DeserializeObject(Json.Serialize(service.GetCreatedTokens(PublicKeys(desktopOriginal)))))[0];
                var receiving = (Dictionary<string, object>)((object[])incompleteToken["receivingAddresses"])[0];
                Check((string)incompleteToken["balanceAtomic"] == "0" && (string)incompleteToken["confirmedAtomic"] == "0" &&
                    service.GetBalance(desktopOriginal.OwnedOneTimeAddresses.ToArray()) > 0 &&
                    (string)receiving["address"] == receivingAddress && !(bool)receiving["owned"] && long.Parse((string)receiving["confirmedAtomic"]) == long.MaxValue,
                    "original Desktop keys reveal the missing issuance key without crediting another address as spendable");
                var unavailable = TransferData(TransferControllerFor(service, "desktop-original", new SessionStateItemCollection()).Prepare(model,
                    TransportKeys(desktopOriginal), desktopOriginal.OwnedOneTimeAddresses.First()));
                Check((string)unavailable["code"] == "preparation_invalid" && !unavailable.ContainsKey("draftId"),
                    "creator and POVIX fee keys cannot spend issued tokens without their receiving key");
            }
            var restoredReceiving = (Dictionary<string, object>)((object[])((Dictionary<string, object>)list[0])["receivingAddresses"])[0];
            Check((bool)restoredReceiving["owned"] && long.Parse((string)restoredReceiving["confirmedAtomic"]) == long.MaxValue,
                "updated wallet keys recover the real confirmed token balance and its receiving address");
            var blocked = TransferData(controller.Prepare(model, TransportKeys(recipient), destination));
            Check(controller.Response.StatusCode == 400 && (string)blocked["code"] == "creator_required", "DEX preparation rejects a wallet that did not create the token");
            controller = TransferControllerFor(service, "transfer-owner", items);
            long beforePovix = chain.GetBalance(addresses), amount = 123456789;
            var draft = TransferData(controller.Prepare(model, TransportKeys(issuer), addresses[0]));
            Check(items.Dirty && (string)draft["tokenId"] == tokenId && long.Parse((string)draft["amountAtomic"]) == amount,
                "MVC transfer preparation retains its session and converts the requested token amount exactly");
            File.WriteAllText(Path.Combine(directory, "transfer-fixture.json"), Json.Serialize(new { wallet = EncryptedWallet(issuer), incompleteWallet, incompleteToken,
                tokens = list, draft,
                expected = new { TokenId = tokenId, Amount = model.Amount, DestinationAddress = destination, Name = creation.Token.Name,
                    Symbol = creation.Token.Symbol, Decimals = creation.Token.Decimals, CreatorAddress = draft["creatorAddress"], FeeAtomic = draft["feeAtomic"] },
                changeAddress = addresses[0], networkId = Blockchain.NetworkId }));
            var start = new System.Diagnostics.ProcessStartInfo(nodeExecutable) { UseShellExecute = false,
                Arguments = "Tests/PovixDexTransferWalletRegression.js " + directory };
            using (var process = System.Diagnostics.Process.Start(start)) { process.WaitForExit(); Check(process.ExitCode == 0, "real transfer form reviews the payload and signs inputs plus creator authorization locally"); }
            var browser = Json.DeserializeObject(File.ReadAllText(Path.Combine(directory, "transfer-browser-result.json"))) as Dictionary<string, object>;
            var signatures = ((object[])browser["signatures"]).Cast<string>().ToArray();
            string creatorSignature = (string)browser["creatorSignature"], draftId = (string)draft["draftId"];
            RejectTransfer(TransferControllerFor(service, "another-session", new SessionStateItemCollection()), draftId, signatures, creatorSignature, "draft_session_changed");
            RejectTransfer(controller, draftId, signatures, SignBytes(recipient, destination, Convert.FromBase64String((string)draft["authorizationPayload"])), "creator_signature_invalid");
            RejectTransfer(controller, draftId, new[] { Convert.ToBase64String(new byte[256]), Convert.ToBase64String(new byte[256]) }, creatorSignature, "signature_invalid");
            var conflicts = TransferData(TransferControllerFor(service, "transfer-owner", items).Prepare(model, TransportKeys(issuer), addresses[0]));
            string[] conflictingSignatures = SignPrepared(conflicts, issuer);
            string conflictingAuthorization = SignBytes(issuer, (string)conflicts["creatorAddress"], Convert.FromBase64String((string)conflicts["authorizationPayload"]));
            Transaction propagated = null;
            peer.TransactionReceived += (sender, eventArgs) => { if (eventArgs.Transaction.Kind == TransactionKind.TokenTransfer) Interlocked.Exchange(ref propagated, eventArgs.Transaction); };
            controller = TransferControllerFor(service, "transfer-owner", items);
            var accepted = TransferData(controller.Submit(draftId, signatures, creatorSignature).GetAwaiter().GetResult());
            string transactionId = (string)accepted["transactionId"];
            Check(controller.Response.StatusCode == 200 && ((string)accepted["receiptUrl"]).Contains("tokens/movimentacao/"), "MVC transfer submission returns a dedicated movement receipt");
            Wait(() => propagated != null && propagated.Id == transactionId, "creator-authorized token movement propagates through the existing P2P network");
            Check(service.GetTransferReceipt(transactionId).Status == "pending" && chain.GetTokenBalance(new[] { destination }, tokenId) == 0 &&
                ((Dictionary<string, object>)((object[])Json.DeserializeObject(Json.Serialize(service.GetCreatedTokens(PublicKeys(issuer)))))[0])["balanceAtomic"].ToString() == "0",
                "pending movement reserves token inputs and does not claim recipient confirmation");
            var receipt = service.GetTransferReceipt(transactionId);
            Check(receipt.Amount == amount && receipt.ChangeOutputs.All(output => output.OneTimeAddress == addresses[0]) &&
                receipt.ChangeOutputs.Single(output => output.AssetId == tokenId).Amount == long.MaxValue - amount &&
                receipt.ChangeOutputs.Single(output => output.AssetId == null).Amount == beforePovix - receipt.Fee,
                "receipt exposes exact token and POVIX change under original wallet keys");
            Check(service.SubmitTransferAsync(draftId, signatures, creatorSignature, "transfer-owner").GetAwaiter().GetResult() == transactionId, "movement retry is idempotent");
            RejectTransfer(TransferControllerFor(service, "transfer-owner", items), (string)conflicts["draftId"], conflictingSignatures, conflictingAuthorization, "funding_unavailable");
            Block confirmed = batches.Confirm(chain, new[] { propagated }, validators);
            peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
            Wait(() => service.GetTransferReceipt(transactionId).Status == "confirmed", "movement receipt confirms only after a validated block");
            Check(chain.IsValid() && chain.GetTokenBalance(addresses, tokenId) == long.MaxValue - amount && chain.GetTokenBalance(new[] { destination }, tokenId) == amount &&
                chain.GetBalance(addresses) == beforePovix - receipt.Fee && service.GetBalance(addresses) == beforePovix - receipt.Fee,
                "confirmed movement conserves the token and deducts only the POVIX fee from the original wallet");
            var movedToken = (Dictionary<string, object>)((object[])Json.DeserializeObject(Json.Serialize(service.GetCreatedTokens(PublicKeys(issuer)))))[0];
            var movedReceiving = (Dictionary<string, object>)((object[])movedToken["receivingAddresses"])[0];
            Check((string)movedReceiving["confirmedAtomic"] == "0" && long.Parse((string)movedToken["balanceAtomic"]) == long.MaxValue - amount,
                "issuance diagnostics read current unspent outputs after transfer rather than the original issued quantity");
            Check(service.GetTransferReceipt(transactionId).BlockHash == confirmed.Hash && service.GetTransferReceipt(transactionId).Confirmations == 1 &&
                ((object[])Json.DeserializeObject(Json.Serialize(service.GetTransferHistory(PublicKeys(issuer), tokenId)))).Length == 1,
                "confirmed receipt and movement history expose real chain data");
            Check(((object[])Json.DeserializeObject(Json.Serialize(service.GetCreatedTokens(PublicKeys(recipient))))).Length == 0,
                "receiving tokens does not grant their creator permission in the DEX");
            var all = TokenTransfer.Prepare(chain, new Transaction[0], PublicKeys(issuer), tokenId, destination, long.MaxValue - amount, addresses[0], 2);
            Check(all.TokenChangeAmount == 0 && all.PovixChangeAmount == beforePovix - receipt.Fee - 2,
                "sending the entire remaining token balance creates no token change and preserves POVIX minus the fee");
            var allModel = new TokenTransferViewModel { TokenId = tokenId, Amount = ((long.MaxValue - amount) / 100000000m).ToString("0.00000000", System.Globalization.CultureInfo.InvariantCulture),
                DestinationAddress = destination, FeePriority = 2 };
            var allDraft = TransferData(TransferControllerFor(service, "transfer-owner", items).Prepare(allModel, TransportKeys(issuer), addresses[0]));
            string[] allSignatures = SignPrepared(allDraft, issuer);
            string allAuthorization = SignBytes(issuer, (string)allDraft["creatorAddress"], Convert.FromBase64String((string)allDraft["authorizationPayload"]));
            string allId = service.SubmitTransferAsync((string)allDraft["draftId"], allSignatures, allAuthorization, "transfer-owner").GetAwaiter().GetResult();
            Check(service.GetTransferReceipt(allId).Status == "pending", "full-balance transfer is accepted with creator authorization while its change awaits confirmation");
            // Supplying the public creator key is insufficient, even with valid signatures for every spendable input.
            var spoofedKeys = PublicKeys(recipient).Concat(new[] { creation.Inputs[0].PublicKey }).ToArray();
            var spoofed = TransferData(TransferControllerFor(service, "attacker", new SessionStateItemCollection()).Prepare(
                new TokenTransferViewModel { TokenId = tokenId, Amount = "0,1", DestinationAddress = addresses[0], FeePriority = 2 },
                spoofedKeys.Select(key => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(key))).ToArray(), destination));
            string[] ownedSignatures = SignPrepared(spoofed, recipient);
            RejectTransfer(TransferControllerFor(service, "attacker", new SessionStateItemCollection()), (string)spoofed["draftId"], ownedSignatures,
                SignBytes(recipient, destination, Convert.FromBase64String((string)spoofed["authorizationPayload"])), "creator_signature_invalid");
            RejectTransfer(TransferControllerFor(service, "attacker", new SessionStateItemCollection()), (string)spoofed["draftId"], ownedSignatures, creatorSignature, "creator_signature_invalid");
            var ordinary = TokenTransfer.Prepare(chain, new Transaction[0], PublicKeys(recipient), tokenId, addresses[0], 10000000, destination, 2);
            chain.ValidatePendingTransactions(new[] { ordinary.Complete(ordinary.InputAddresses.Select(address => SignBytes(recipient, address, ordinary.SigningPayload))) });
            Check(true, "holder transfers remain valid under the existing consensus outside the restricted DEX screen");
            using (var restored = new TokenNetworkService(directory, Port(), new string[0]))
            {
                Check(restored.GetTransferReceipt(allId).Status == "pending" && !restored.GetNetwork().CanCreate &&
                    restored.SubmitTransferAsync((string)allDraft["draftId"], allSignatures, allAuthorization, "transfer-owner").GetAwaiter().GetResult() == allId,
                    "movement receipt and idempotent acceptance survive a restart without claiming synchronization");
            }
        }
    }

    private static string[] TransportKeys(Wallet wallet) => PublicKeys(wallet).Select(key => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(key))).ToArray();
    private static Dictionary<string, object> TransferData(ActionResult result) => Json.DeserializeObject(Json.Serialize(((JsonResult)result).Data)) as Dictionary<string, object>;
    private static void RejectTransfer(TokenTransfersController controller, string id, string[] signatures, string authorization, string code)
    {
        var result = TransferData(controller.Submit(id, signatures, authorization).GetAwaiter().GetResult());
        Check(controller.Response.StatusCode == 400 && (string)result["code"] == code && !result.ContainsKey("transactionId"), "movement rejection identifies " + code);
    }
    private static string SignBytes(Wallet wallet, string address, byte[] payload)
    {
        foreach (string xml in wallet.ExportPrivateKeys()) using (var rsa = new RSACryptoServiceProvider())
        {
            rsa.PersistKeyInCsp = false; rsa.FromXmlString(xml);
            using (var hash = SHA256.Create()) if (BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rsa.ToXmlString(false)))).Replace("-", "").ToLowerInvariant() == address)
                return Convert.ToBase64String(rsa.SignData(payload, CryptoConfig.MapNameToOID("SHA256")));
        }
        throw new Exception("Test key not found");
    }
    private static TokenTransfersController TransferControllerFor(TokenNetworkService service, string session, SessionStateItemCollection items)
    {
        var template = ControllerFor(service, session, items);
        template.ControllerContext.RouteData.Values["controller"] = "TokenTransfers";
        template.ControllerContext.RouteData.Values["action"] = "Index";
        var controller = new TokenTransfersController();
        controller.ControllerContext = new ControllerContext(template.ControllerContext.RequestContext, controller);
        controller.Url = template.Url;
        return controller;
    }
}
