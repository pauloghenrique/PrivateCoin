using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using PrivateCoin.Core;
using PrivateCoin.Site.Models;

// Compile alongside Core sources to exercise signed adversarial transactions.
class TokenRegression
{
    static int checks;
    static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        checks++; Console.WriteLine("PASS " + label);
    }
    static void Reject(Action action, string label)
    {
        try { action(); } catch (InvalidOperationException) { Check(true, label); return; }
        throw new Exception("Accepted: " + label);
    }
    static Transaction Clone(Transaction tx)
    {
        var serializer = new DataContractJsonSerializer(typeof(Transaction));
        using (var stream = new MemoryStream())
        {
            serializer.WriteObject(stream, tx); stream.Position = 0;
            return (Transaction)serializer.ReadObject(stream);
        }
    }
    static void Sign(Transaction tx, Wallet wallet)
    {
        byte[] payload = Encoding.UTF8.GetBytes(tx.SigningPayload());
        foreach (var input in tx.Inputs)
        {
            using (var key = new RSACryptoServiceProvider())
            {
                key.PersistKeyInCsp = false;
                foreach (var xml in wallet.ExportPrivateKeys())
                {
                    key.FromXmlString(xml);
                    if (key.ToXmlString(false) == input.PublicKey) break;
                }
                if (key.ToXmlString(false) != input.PublicKey) throw new Exception("Signing key missing");
                input.Signature = Convert.ToBase64String(key.SignData(payload, CryptoConfig.MapNameToOID("SHA256")));
            }
        }
        tx.Id = tx.CalculateId();
    }
    static void Main()
    {
        var pending = new Transaction[0];
        var chain = new Blockchain();
        using (var alice = new Wallet()) using (var bob = new Wallet())
        using (var validatorA = new Wallet()) using (var validatorB = new Wallet())
        {
            Block reward;
            chain.TryAddWalletCreationReward(alice.CreateReceiveAddress(), out reward);
            chain.TryAddWalletCreationReward(bob.CreateReceiveAddress(), out reward);
            string va = validatorA.CreateReceiveAddress(), vb = validatorB.CreateReceiveAddress();
            chain.TryAddWalletCreationReward(va, out reward);
            chain.TryAddWalletCreationReward(vb, out reward);
            chain.AddBlock(new[] { validatorA.CreateStakeLockTransaction(chain, pending, va, Blockchain.OneCoin, 1),
                validatorB.CreateStakeLockTransaction(chain, pending, vb, Blockchain.OneCoin, 1) });
            var validators = new[] { validatorA.CreateValidatorStake(va, Blockchain.OneCoin), validatorB.CreateValidatorStake(vb, Blockchain.OneCoin) };
            var create = alice.CreateTokenTransaction(chain, pending, "Meu Token", "MTK", 2, 100000, alice.CreateReceiveAddress(), 1);
            string id = create.Token.Id;
            Check(id.Length == 64, "stable token identifier");
            chain.ValidatePendingTransactions(new[] { create });
            Check(chain.GetTokens().Count == 0 && chain.GetTokenBalance(alice.OwnedOneTimeAddresses, id) == 0, "pending creation does not confirm tokens");
            Reject(() => chain.ValidatePendingTransactions(new[] { create, create }), "duplicate creation/double spend");
            var invalid = Clone(create); invalid.Outputs[0].Amount++; Sign(invalid, alice);
            Reject(() => chain.ValidatePendingTransactions(new[] { invalid }), "signed supply mismatch");
            invalid = Clone(create); invalid.Token.Id = new string('a',64); invalid.Outputs[0].AssetId = invalid.Token.Id; Sign(invalid, alice);
            Reject(() => chain.ValidatePendingTransactions(new[] { invalid }), "signed arbitrary token identifier");
            invalid = Clone(create); invalid.Token.Decimals = 9; Sign(invalid, alice);
            Reject(() => chain.ValidatePendingTransactions(new[] { invalid }), "signed invalid decimals");
            invalid = Clone(create); invalid.Token.Name += "!"; invalid.Id = invalid.CalculateId();
            Reject(() => chain.ValidatePendingTransactions(new[] { invalid }), "metadata is covered by signature");
            chain.AddProofOfStakeBlock(new[] { create }, validators);
            Check(chain.IsValid() && chain.GetTokens().Single().Supply == 100000, "creation in validator-confirmed block");
            Check(chain.GetTokenBalance(alice.OwnedOneTimeAddresses,id) == 100000 && chain.GetBalance(alice.OwnedOneTimeAddresses) == Blockchain.WalletCreationReward - 1, "separate token and POVIX balances");
            Check(chain.GetBalancesByAddress().Values.Sum() == 4 * Blockchain.WalletCreationReward - 2 + ProofOfStake.GetBlockReward(chain.Blocks.Count-1), "address balances exclude tokens and pay token fee to creator");
            var transfer = alice.CreateTokenTransferTransaction(chain,pending,id,bob.CreateReceiveAddress(),30000,1);
            Check(chain.GetSpendableTokenOutputs(alice.OwnedOneTimeAddresses,new[] { transfer },id).Count == 0, "pending token input is reserved");
            Reject(() => alice.CreateTokenTransferTransaction(chain,new[] { transfer },id,bob.CreateReceiveAddress(),1,1), "pending token change is unavailable");
            invalid = Clone(transfer); invalid.Outputs[0].Amount++; Sign(invalid,alice);
            Reject(() => chain.ValidatePendingTransactions(new[] { invalid }), "signed token inflation");
            invalid = Clone(transfer); invalid.Outputs[0].AssetId = new string('b',64); Sign(invalid,alice);
            Reject(() => chain.ValidatePendingTransactions(new[] { invalid }), "signed asset substitution");
            invalid = Clone(transfer); invalid.Kind = TransactionKind.Transfer; Sign(invalid,alice);
            Reject(() => chain.ValidatePendingTransactions(new[] { invalid }), "ordinary payment cannot consume token inputs");
            invalid = Clone(transfer); invalid.Outputs[0].AssetId = null; Sign(invalid,alice);
            Reject(() => chain.ValidatePendingTransactions(new[] { invalid }), "tokens cannot become POVIX");
            chain.AddProofOfStakeBlock(new[] { transfer },validators);
            Check(chain.IsValid() && chain.GetTokenBalance(alice.OwnedOneTimeAddresses,id) == 70000 && chain.GetTokenBalance(bob.OwnedOneTimeAddresses,id) == 30000, "transfer and token change");
            Reject(() => chain.ValidatePendingTransactions(new[] { transfer }), "confirmed input cannot be spent again");
            var next = bob.CreateTokenTransaction(chain,pending,"Outro Token","MTK",0,7,bob.CreateReceiveAddress(),1);
            Check(next.Token.Id != id, "symbols may repeat but identifiers remain unique");
            chain.AddProofOfStakeBlock(new[] { next },validators);
            Check(chain.GetTokens().Count == 2, "multiple independent assets");
            var ledger = new BlockchainQueryApi(chain).GetLedger(0,100);
            Check(ledger.Single(e => e.TransactionId == create.Id).OutputAmount == Blockchain.WalletCreationReward - 1 && ledger.Single(e => e.TransactionId == transfer.Id).Outputs.Any(o => o.AssetId == id), "ledger retains asset ids and POVIX-only totals");
            Check(HomeViewModel.LedgerAmount(ledger.Single(e => e.TransactionId == create.Id)) == 0 &&
                HomeViewModel.LedgerAmount(ledger.Single(e => e.TransactionId == transfer.Id)) == 0 &&
                HomeViewModel.EntryTypeName(ledger.Single(e => e.TransactionId == create.Id).Type) == "Criação de token", "explorer does not label token quantities as POVIX");
            var serializer = new DataContractJsonSerializer(typeof(Block[]));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream,chain.Blocks.ToArray()); stream.Position=0;
                var restored = new Blockchain((Block[])serializer.ReadObject(stream));
                Check(restored.GetTokens().Count==2 && restored.GetTokenBalance(bob.OwnedOneTimeAddresses,id)==30000, "serialized chain replay");
                Check(new Blockchain().TryReplaceChain(restored.Blocks), "synchronized chain accepts tokens");
            }
            var ordinary = alice.CreateTransaction(chain,bob.CreateReceiveAddress(),100);
            chain.AddProofOfStakeBlock(new[] { ordinary },validators);
            Check(chain.IsValid() && chain.GetTokenBalance(alice.OwnedOneTimeAddresses,id)==70000,"ordinary payments preserve tokens");
        }
        Console.WriteLine(checks + " checks passed");
    }
}
