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
        for (int index = 0; index < Blockchain.ValidationsPerBlock; index++)
        {
            LegacyConsensusFixture.Fund(chain, filler.CreateReceiveAddress());
        }
    }

    public Block Confirm(Blockchain chain, IEnumerable<Transaction> transactions, IEnumerable<ValidatorStake> validators)
    {
        var batch = transactions.ToList();
        while (batch.Count < Blockchain.ValidationsPerBlock)
            batch.Add(filler.CreateTransaction(chain, batch, destination, 1, 1));
        return chain.AddProofOfStakeBlock(batch, validators);
    }

    public void Dispose() { filler.Dispose(); }
}
