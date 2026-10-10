using System;
using System.Collections.Generic;
using System.Linq;
using PrivateCoin.Core;

internal sealed class ValidationBatchFixture : IDisposable
{
    private readonly Wallet filler = new Wallet();
    private readonly string destination;

    public ValidationBatchFixture(Blockchain chain)
    {
        destination = filler.CreateReceiveAddress();
        LegacyConsensusFixture.FundBatch(chain, Enumerable.Range(0, Blockchain.ValidationsPerBlock)
            .Select(index => filler.CreateReceiveAddress()).ToArray());
    }

    public Block Confirm(Blockchain chain, IEnumerable<Transaction> transactions, IEnumerable<ValidatorStake> validators)
    {
        var batch = transactions.ToList();
        while (batch.Count < Blockchain.ValidationsPerBlock)
            batch.Add(filler.CreateTransaction(chain, batch, destination, 1, 1));
        foreach (Transaction creation in batch.Where(Blockchain.RequiresLockedTokenApproval))
            if (!chain.HasValidTransactionApproval(creation, batch))
                creation.TransactionApproval = chain.CreateTransactionApproval(creation, batch, validators);
        return chain.AddProofOfStakeBlock(batch, validators);
    }

    public void Dispose() { filler.Dispose(); }
}
