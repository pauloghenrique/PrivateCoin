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
        using (var issuer = new Wallet()) using (var first = new Wallet()) using (var second = new Wallet()) using (var third = new Wallet())
        {
            var chain = new Blockchain(); var pending = new List<Transaction>();
            string a = first.CreateReceiveAddress(), b = second.CreateReceiveAddress(), c = issuer.CreateReceiveAddress(), d = third.CreateReceiveAddress();
            foreach (string address in new[] { a, b, c, d }) pending.Add(chain.CreateWalletCreationTransaction(address, pending));
            pending.Add(first.CreateStakeLockTransaction(chain, pending, a, Blockchain.WalletCreationReward, 0));
            pending.Add(second.CreateStakeLockTransaction(chain, pending, b, Blockchain.WalletCreationReward, 0));
            pending.Add(third.CreateStakeLockTransaction(chain, pending, d, Blockchain.WalletCreationReward, 0));
            var validators = new[] { first.CreateValidatorStake(a, Blockchain.WalletCreationReward), second.CreateValidatorStake(b, Blockchain.WalletCreationReward), third.CreateValidatorStake(d, Blockchain.WalletCreationReward) };
            Transaction creation = issuer.CreateTokenTransaction(chain, pending, "Aurora", "AUR", 2, 100000, c, 27);
            pending.Add(creation); string originalId = creation.Id;
            chain.ValidatePendingTransactions(pending);
            Check(!chain.HasValidTransactionApproval(creation, pending) && chain.Blocks.Count == 1,
                "creator signatures reserve funds but do not claim approval or create a block");
            Check(chain.CreateTransactionApproval(creation, pending, new ValidatorStake[0]) == null,
                "without an owned locked validator the creation waits for approval");
            Check(chain.CreateTransactionApproval(creation, pending, new[] { issuer.CreateValidatorStake(c, Blockchain.OneCoin) }) == null,
                "an unfunded validator identity cannot approve a creation");
            while (pending.Count < 20) pending.Add(chain.CreateWalletCreationTransaction("token-batch-" + pending.Count, pending));
            Check(chain.SelectApprovedValidationBatch(pending).Count == 0,
                "an unapproved token does not complete a 20-operation batch");
            Reject(() => chain.AddProofOfStakeBlock(pending, validators), "block creation cannot manufacture a missing token approval");
            TransactionApproval approval = chain.CreateTransactionApproval(creation, pending, validators.Take(1));
            Check(approval != null && chain.Blocks.Count == 1, "one locked wallet can approve the token without creating a block");
            creation.TransactionApproval = approval;
            Check(creation.Id == originalId && chain.HasValidTransactionApproval(creation, pending),
                "a collateral-backed signature approves the original creator transaction");
            Check(chain.GetSpendableBalance(first.OwnedOneTimeAddresses, pending) == 27 && chain.GetBalance(first.OwnedOneTimeAddresses) == 0,
                "the chosen validator can immediately spend the fee despite all its initial tokens being locked");
            Check(chain.SelectApprovedValidationBatch(pending).Count == 20 && chain.Blocks.Count == 1,
                "approval counts once toward 20 but does not append a block");
            var ordered = Blockchain.OrderByFeePriority(pending).ToList();
            Check(ordered.FindIndex(tx => tx.Id == approval.CollateralTransactionId) < ordered.FindIndex(tx => tx.Id == creation.Id),
                "the approving collateral is included before its high-fee token operation");
            Check(chain.GetTokenBalances(new[] { c }).Count == 0 && chain.GetSpendableBalance(issuer.OwnedOneTimeAddresses, pending) == Blockchain.WalletCreationReward - creation.Fee,
                "approval returns exactly native funds minus the chosen fee while token confirmation still awaits its block");
            var restarted = new Blockchain(Copy(chain.Blocks.ToList())); var restoredPending = Copy(pending);
            Check(restarted.HasValidTransactionApproval(restoredPending.Single(tx => tx.Id == originalId), restoredPending),
                "pending approval survives serialization and restart without a block");
            var forged = Copy(creation); forged.TransactionApproval.Proof.Signature = Convert.ToBase64String(new byte[256]);
            Check(!chain.HasValidTransactionApproval(forged, pending), "a forged validator signature cannot approve the token");
            forged = Copy(creation); forged.TransactionApproval.Proof.RewardAddress = b;
            Check(!chain.HasValidTransactionApproval(forged, pending), "the approving fee address cannot be redirected");
            forged = Copy(creation); forged.TransactionApproval.CollateralOutputIndex = 999;
            Check(!chain.HasValidTransactionApproval(forged, pending), "nonexistent collateral cannot support approval");
            forged = Copy(creation); forged.TransactionApproval.AnchorHash = new string('a', 64);
            Check(!chain.HasValidTransactionApproval(forged, pending), "an unrelated chain anchor cannot support approval");
            forged = Copy(creation); forged.TransactionApproval.Proof.TransactionId = new string('b', 64);
            Check(!chain.HasValidTransactionApproval(forged, pending), "the proof cannot be replayed for another transaction");
            Transaction unlock = first.CreateStakeUnlockTransaction(chain, pending, a, 0);
            Check(chain.HasValidTransactionApproval(creation, pending.Concat(new[] { unlock })),
                "a later queued unlock does not pay the already validated fee again");
            Transaction ownCreation = first.CreateTokenTransaction(chain, pending, "Próprio", "OWN", 0, 100, a, 1);
            Check(chain.CreateTransactionApproval(ownCreation, pending, validators.Take(1)) == null,
                "a locked wallet cannot approve its own token creation");
            pending.RemoveAt(pending.Count - 1);
            Transaction feeSpend = first.CreateTransaction(chain, pending, "paid-from-fee", 25, 2);
            Check(feeSpend.Inputs.Single().TransactionId == creation.Id && feeSpend.Inputs[0].OutputIndex == creation.Outputs.Count,
                "a transfer spends the exact fee credit before its parent's block exists");
            pending.Add(feeSpend);
            feeSpend.TransactionApproval = chain.CreateTransactionApproval(feeSpend, pending, validators.Skip(1).Take(1));
            chain.ValidatePendingTransactions(pending);
            Check(chain.HasValidTransactionApproval(feeSpend, pending) && chain.GetSpendableBalance(first.OwnedOneTimeAddresses, pending) == 0 &&
                chain.GetSpendableBalance(second.OwnedOneTimeAddresses, pending) == 2 && chain.Blocks.Count == 1,
                "the fee-funded transfer reserves its credit and immediately pays the next validator without a block");
            var batch = chain.SelectApprovedValidationBatch(pending);
            var emptyReward = new Transaction { TimestampUtcTicks = DateTime.UtcNow.Ticks };
            emptyReward.Id = LegacyConsensusFixture.Id(emptyReward);
            var exhaustedBlock = new Block { ConsensusVersion = Blockchain.ConsensusVersion,
                Height = ProofOfStake.RewardPhases.Last().LastHeight + 1,
                Transactions = new[] { emptyReward }.Concat(batch).ToList(),
                TransactionValidations = batch.Where(Blockchain.RequiresLockedTokenApproval).Select(tx => tx.TransactionApproval.Proof).ToList() };
            var settlement = typeof(Blockchain).GetMethod("ValidateTransactionFees", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            settlement.Invoke(null, new object[] { exhaustedBlock, batch.ToArray(), validators, chain.Blocks.Last().Hash,
                new ValidatorReward[0], new Dictionary<string, UnspentOutput>(), chain.Blocks.ToList() });
            Check(ProofOfStake.GetBlockReward(exhaustedBlock.Height) == 0 && creation.GetValidationFeeOutput().Amount == 27,
                "after scheduled emission ends approvals still settle fees without minting a reward");
            Block block = chain.AddProofOfStakeBlock(batch, validators);
            Check(chain.Blocks.Count == 2 && block.Transactions.Count == 21 && chain.IsValid(),
                "a separate block step confirms exactly 20 approved operations");
            Check(block.TransactionValidations.Single(proof => proof.TransactionId == creation.Id).Signature == approval.Proof.Signature &&
                creation.GetValidationFeeOutput().OneTimeAddress == a && creation.GetValidationFeeOutput().Amount == 27 && block.Transactions[0].Outputs.Count == 2,
                "settlement preserves the pre-block validator signature and pays its chosen fee");
            Check(block.Transactions[0].Outputs.Take(2).Sum(output => output.Amount) == ProofOfStake.GetBlockReward(block.Height),
                "the scheduled block reward remains separate from the approval fee");
            Check(chain.GetBalance(first.OwnedOneTimeAddresses) == Blockchain.WalletCreationReward &&
                chain.GetBalance(new[] { "paid-from-fee" }) == 25,
                "confirming the batch does not repay the spent fee to the first validator");
            Check(chain.GetBalancesByAddress().Values.Sum() ==
                15 * Blockchain.WalletCreationReward + ProofOfStake.GetBlockReward(block.Height),
                "confirmed native supply includes initial and block rewards without minting fees");
            var ledger = new BlockchainQueryApi(chain).GetLedger(0, 100);
            Check(ledger.Single(entry => entry.TransactionId == feeSpend.Id).InputAmount == 27 &&
                ledger.Single(entry => entry.TransactionId == creation.Id).Outputs.Last().Amount == 27,
                "the explorer follows the fee output and its subsequent spend");
            Check(new Blockchain(Copy(chain.Blocks.ToList())).GetTokenBalance(new[] { c }, creation.Token.Id) == 100000,
                "confirmed approvals and token balances independently restore");
            var historicalApproval = Copy(chain.Blocks.ToList());
            historicalApproval.Last().ConsensusVersion = 11;
            LegacyConsensusFixture.Resign(historicalApproval.Last(), validators);
            LegacyConsensusFixture.Mine(historicalApproval.Last());
            var v11 = new Blockchain(historicalApproval);
            Check(v11.IsValid() && v11.GetTokenBalance(new[] { c }, creation.Token.Id) == 100000,
                "real v11 approval and fee-spend history retains its original balances");
            var upgrade = new List<Transaction>();
            for (int i = 0; i < 20; i++) upgrade.Add(v11.CreateWalletCreationTransaction("v11-upgrade-" + i, upgrade));
            v11.AddBlock(upgrade);
            Check(v11.IsValid() && v11.Blocks.Last().ConsensusVersion == Blockchain.ConsensusVersion,
                "v11 history extends into immediate token change consensus without duplicate fees");
            var tampered = Copy(chain.Blocks.ToList()); tampered.Last().Transactions.Single(tx => tx.Id == originalId).TransactionApproval = null;
            LegacyConsensusFixture.Mine(tampered.Last());
            Reject(() => new Blockchain(tampered), "a mined received block cannot discard its token approval");
            Check(!new Blockchain().HasValidTransactionApproval(creation, new Transaction[0]),
                "orphaned initial rewards and collateral cannot preserve approval");
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
            var next = new List<Transaction>();
            for (int i = 0; i < 20; i++) next.Add(chain.CreateWalletCreationTransaction("after-v10-" + i, next));
            chain.AddBlock(next);
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
