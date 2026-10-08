using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Runtime.Serialization.Json;
using DEXPovix.Models;
using DEXPovix.Services;
using PrivateCoin.Core;

class DexTokenRegression
{
    static int checks;
    static void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
    static void Reject(Action action, string label) { try { action(); } catch (InvalidOperationException) { Check(true, label); return; } throw new Exception(label); }
    static string Serialize(Transaction tx)
    {
        using (var stream = new MemoryStream())
        { new DataContractJsonSerializer(typeof(Transaction)).WriteObject(stream, tx); return Encoding.UTF8.GetString(stream.ToArray()); }
    }
    static void Main()
    {
        var chain = new Blockchain();
        using (var wallet = new Wallet())
        using (var va = new Wallet())
        using (var vb = new Wallet())
        {
            Block reward;
            chain.TryAddWalletCreationReward(wallet.CreateReceiveAddress(), out reward);
            string a = va.CreateReceiveAddress(), b = vb.CreateReceiveAddress();
            chain.TryAddWalletCreationReward(a, out reward); chain.TryAddWalletCreationReward(b, out reward);
            chain.AddBlock(new[] { va.CreateStakeLockTransaction(chain, new Transaction[0], a, Blockchain.OneCoin, 1), vb.CreateStakeLockTransaction(chain, new Transaction[0], b, Blockchain.OneCoin, 1) });
            string destination = wallet.CreateReceiveAddress();
            var tx = wallet.CreateTokenTransaction(chain, new Transaction[0], "Token DEX", "DEX", 2, 100000, destination, 1);
            var model = new CreateTokenViewModel { Name = "Token DEX", Symbol = "DEX", Decimals = 2, Supply = 100000, Destination = destination, SignedTransaction = Serialize(tx) };
            var parsed = TokenSubmission.Parse(model);
            chain.ValidatePendingTransactions(new[] { parsed });
            Check(parsed.Id == tx.Id, "signed JSON preserves transaction and signature");
            Check(chain.GetTokens().Count == 0, "submission does not issue local token");
            model.Supply++; Reject(() => TokenSubmission.Parse(model), "reject mismatched supply"); model.Supply--;
            model.Destination = new string('a',64); Reject(() => TokenSubmission.Parse(model), "reject mismatched destination"); model.Destination = destination;
            model.Name = "Alterado"; Reject(() => TokenSubmission.Parse(model), "reject mismatched metadata"); model.Name = "Token DEX";
            model.SignedTransaction = "not json"; Reject(() => TokenSubmission.Parse(model), "reject malformed JSON");
            model.SignedTransaction = "null"; Reject(() => TokenSubmission.Parse(model), "reject null JSON");
            model.SignedTransaction = new string('x',131073); Reject(() => TokenSubmission.Parse(model), "reject oversized JSON");
            tx.Inputs[0].Signature = "invalid"; model.SignedTransaction = Serialize(tx);
            Reject(() => chain.ValidatePendingTransactions(new[] { TokenSubmission.Parse(model) }), "reject modified signature");
            chain.AddProofOfStakeBlock(new[] { parsed }, new[] { va.CreateValidatorStake(a, Blockchain.OneCoin), vb.CreateValidatorStake(b, Blockchain.OneCoin) });
            Check(chain.GetTokens().Single().Id == parsed.Token.Id && chain.GetTokenBalance(wallet.OwnedOneTimeAddresses, parsed.Token.Id) == 100000, "validator confirmation registers token in existing chain");
        }
        Console.WriteLine(checks + " checks passed");
    }
}
