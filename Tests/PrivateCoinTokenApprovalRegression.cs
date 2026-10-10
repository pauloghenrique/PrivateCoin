using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using PrivateCoin.Core;

internal static class PrivateCoinTokenApprovalRegression
{
    private static int checks;
    public static int Main()
    {
        try { Run(); Historical(); Console.WriteLine(checks + " token approval checks passed"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Run()
    {
        using (var issuer = new Wallet()) using (var first = new Wallet()) using (var second = new Wallet())
        {
            var chain = new Blockchain();
            string a = first.CreateReceiveAddress(), b = second.CreateReceiveAddress(), c = issuer.CreateReceiveAddress();
            LegacyConsensusFixture.FundBatch(chain, a, b, c);
            LegacyConsensusFixture.SelfBlock(chain, new[] {
                first.CreateStakeLockTransaction(chain, new Transaction[0], a, Blockchain.OneCoin, 0),
                second.CreateStakeLockTransaction(chain, new Transaction[0], b, Blockchain.OneCoin, 0) });
            var validators = new[] { first.CreateValidatorStake(a, Blockchain.OneCoin), second.CreateValidatorStake(b, Blockchain.OneCoin) };
            var creation = issuer.CreateTokenTransaction(chain, new Transaction[0], "Aurora", "AUR", 2, 100000, c, 27);
            var pending = new List<Transaction> { creation }; int height = chain.Blocks.Count;
            Check(!chain.HasValidTransactionApproval(creation, pending) && chain.SelectApprovedValidationBatch(pending).Count == 0,
                "an unapproved token is not eligible for its block");
            Reject(() => chain.AddProofOfStakeBlock(pending, validators), "a block cannot manufacture a missing approval");
            Check(chain.CreateTransactionApproval(creation, pending, new ValidatorStake[0]) == null &&
                chain.CreateTransactionApproval(creation, pending, new[] { issuer.CreateValidatorStake(c, Blockchain.OneCoin) }) == null,
                "only owned and confirmed collateral can approve the creation");
            long beforeFee = chain.GetSpendableBalance(first.OwnedOneTimeAddresses, new Transaction[0]);
            creation.TransactionApproval = chain.CreateTransactionApproval(creation, pending, validators.Take(1));
            Check(chain.HasValidTransactionApproval(creation, pending) && chain.SelectApprovedValidationBatch(pending).Count == 1 && chain.Blocks.Count == height,
                "approval makes the full batch eligible while adding no block");
            Check(chain.GetSpendableBalance(first.OwnedOneTimeAddresses, pending) == beforeFee && chain.GetTokens(pending).Count == 0,
                "approval pays no fee and registers no token before the block");
            var restarted = new Blockchain(Copy(chain.Blocks.ToList())); var saved = Copy(pending);
            Check(restarted.HasValidTransactionApproval(saved.Single(tx => tx.Id == creation.Id), saved), "approval survives restart while waiting for its block");
            var forged = Copy(creation); forged.TransactionApproval.Proof.Signature = Convert.ToBase64String(new byte[256]);
            Check(!chain.HasValidTransactionApproval(forged, pending), "forged signatures cannot approve the token");
            forged = Copy(creation); forged.TransactionApproval.Proof.RewardAddress = b;
            Check(!chain.HasValidTransactionApproval(forged, pending), "approval cannot redirect its fee");
            forged = Copy(creation); forged.TransactionApproval.CollateralOutputIndex = 999;
            Check(!chain.HasValidTransactionApproval(forged, pending), "nonexistent collateral cannot support approval");
            forged = Copy(creation); forged.TransactionApproval.AnchorHash = new string('a', 64);
            Check(!chain.HasValidTransactionApproval(forged, pending), "approval cannot anchor on another chain");
            forged = Copy(creation); forged.TransactionApproval.Proof.TransactionId = new string('b', 64);
            Check(!chain.HasValidTransactionApproval(forged, pending), "approval cannot be replayed for another transaction");
            var ownCreation = first.CreateTokenTransaction(chain, new Transaction[0], "Próprio", "OWN", 0, 100, a, 1);
            Check(chain.CreateTransactionApproval(ownCreation, new[] { ownCreation }, validators.Take(1)) == null, "a validator cannot approve its own token creation");
            long supplyBefore = chain.GetBalancesByAddress().Values.Sum();
            Block block = chain.AddProofOfStakeBlock(chain.SelectApprovedValidationBatch(pending), validators);
            Check(chain.IsValid() && chain.Blocks.Count == height + 1 && block.Transactions.Count == 2,
                "one block confirms one approved operation plus the scheduled reward");
            Check(block.TransactionValidations.Single().Signature == creation.TransactionApproval.Proof.Signature &&
                creation.GetValidationFeeOutput().Amount == 27 && creation.GetValidationFeeOutput().OneTimeAddress == a,
                "the block pays the exact fee to its approving validator");
            Check(block.Transactions[0].Outputs.Sum(output => output.Amount) == ProofOfStake.GetBlockReward(block.Height) &&
                chain.GetBalancesByAddress().Values.Sum() == supplyBefore + ProofOfStake.GetBlockReward(block.Height),
                "fee settlement preserves supply and remains separate from the scheduled reward");
            Check(new Blockchain(Copy(chain.Blocks.ToList())).GetTokenBalance(new[] { c }, creation.Token.Id) == 100000,
                "confirmed native token supply restores without applying its fee again");
            var tampered = Copy(chain.Blocks.ToList()); tampered.Last().Transactions.Single(tx => tx.Id == creation.Id).TransactionApproval = null;
            LegacyConsensusFixture.Mine(tampered.Last());
            Reject(() => new Blockchain(tampered), "received blocks cannot discard required approvals");
            Check(!new Blockchain().HasValidTransactionApproval(creation, new Transaction[0]), "orphaned collateral cannot preserve approval");
        }
    }
    private static void Historical()
    {
        using (var issuer = new Wallet()) using (var first = new Wallet()) using (var second = new Wallet())
        {
            var chain = new Blockchain(); string a = first.CreateReceiveAddress(), b = second.CreateReceiveAddress(), c = issuer.CreateReceiveAddress();
            LegacyConsensusFixture.Fund(chain, a); LegacyConsensusFixture.Fund(chain, b);
            for (int i = 0; i < 20; i++) LegacyConsensusFixture.Fund(chain, c);
            LegacyConsensusFixture.Lock(chain, first.CreateStakeLockTransaction(chain, new Transaction[0], a, Blockchain.OneCoin, 1));
            LegacyConsensusFixture.Lock(chain, second.CreateStakeLockTransaction(chain, new Transaction[0], b, Blockchain.OneCoin, 1));
            var validators = new[] { first.CreateValidatorStake(a, Blockchain.OneCoin), second.CreateValidatorStake(b, Blockchain.OneCoin) };
            var operations = new List<Transaction>();
            operations.Add(issuer.CreateTokenTransaction(chain, operations, "Histórico", "HIS", 0, 123, c, 1));
            while (operations.Count < 20) operations.Add(issuer.CreateTransaction(chain, operations, c, Blockchain.WalletCreationReward - 1, 1));
            LegacyConsensusFixture.Confirm(chain, operations, validators, 10);
            Check(chain.IsValid() && new Blockchain(Copy(chain.Blocks.ToList())).GetTokenBalance(new[] { c }, operations[0].Token.Id) == 123,
                "historical v10 token blocks retain their original signatures and balances");
            var historical = new List<Transaction>();
            historical.Add(issuer.CreateTokenTransaction(chain, historical, "Legado", "LEG", 0, 456, c, 1));
            while (historical.Count < 20) historical.Add(issuer.CreateTransaction(chain, historical, c, 1, 1));
            foreach (var operation in historical)
                operation.TransactionApproval = chain.CreateTransactionApproval(operation, historical, validators.Take(1));
            foreach (int version in new[] { 11, 12, 13, 14 })
            {
                var restored = new Blockchain(Copy(chain.Blocks.ToList()));
                LegacyConsensusFixture.ConfirmApprovedLegacy(restored, Copy(historical), validators, version);
                Check(new Blockchain(Copy(restored.Blocks.ToList())).GetTokenBalance(new[] { c }, historical[0].Token.Id) == 456,
                    "historical v" + version + " preserves its 20-operation block and original fees");
                var registration = restored.CreateWalletCreationTransaction("upgrade-v" + version, new Transaction[0]);
                restored.AddBlock(new[] { registration });
                Check(restored.IsValid() && restored.Blocks.Last().ConsensusVersion == 15 && restored.Blocks.Last().Transactions.Count == 1,
                    "historical v" + version + " extends with a single-operation v15 block");
            }
            var next = new List<Transaction>();
            for (int i = 0; i < 20; i++) next.Add(chain.CreateWalletCreationTransaction("after-v10-" + i, next));
            foreach (var registration in next) chain.AddBlock(new[] { registration });
            Check(chain.IsValid() && chain.Blocks.Last().ConsensusVersion == Blockchain.ConsensusVersion,
                "historical v10 token chains extend into the current consensus without issuing the token again");
        }
    }
    private static T Copy<T>(T value)
    {
        var serializer = new DataContractSerializer(typeof(T));
        using (var stream = new MemoryStream()) { serializer.WriteObject(stream, value); stream.Position = 0; return (T)serializer.ReadObject(stream); }
    }
    private static void Reject(Action action, string label)
    {
        try { action(); } catch (InvalidOperationException) { Check(true, label); return; }
        throw new Exception("Expected rejection: " + label);
    }
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
}
