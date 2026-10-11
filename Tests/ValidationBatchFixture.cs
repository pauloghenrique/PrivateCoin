using System;
using System.Collections.Generic;
using System.Linq;
using PrivateCoin.Core;

internal sealed class ValidationBatchFixture : IDisposable
{
    public ValidationBatchFixture(Blockchain chain) { }

    public Block Confirm(Blockchain chain, IEnumerable<Transaction> transactions, IEnumerable<ValidatorStake> validators)
    {
        var batch = transactions.ToList();
        // Work/reorganization checks need a real operation when advancing a tip.
        if (batch.Count == 0)
            batch.Add(chain.CreateWalletCreationTransaction("batch-fixture-" + Guid.NewGuid().ToString("N"), batch));
        foreach (Transaction creation in batch.Where(Blockchain.RequiresLockedTokenApproval))
            if (!chain.HasValidTransactionApproval(creation, batch))
                creation.TransactionApproval = chain.CreateTransactionApproval(creation, batch, validators);
        return chain.AddProofOfStakeBlock(batch, validators);
    }

    public void Dispose() { }
}
