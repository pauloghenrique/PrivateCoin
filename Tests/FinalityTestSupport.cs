using System;
using System.Linq;
using PrivateCoin.Core;

internal static class FinalityTestSupport
{
    public static Blockchain ActiveChain(Wallet wallet, out ValidatorStake signer, string nodeId = null)
    {
        var chain = nodeId == null ? new Blockchain() : new Blockchain(nodeId);
        string address = wallet.CreateReceiveAddress();
        Block reward;
        chain.TryAddWalletCreationReward(address, out reward);
        chain.AddBlock(new[] { wallet.CreateStakeLockTransaction(chain, new Transaction[0], address, Blockchain.OneCoin, 1) });
        signer = wallet.CreateValidatorStake(address, Blockchain.OneCoin);
        return chain;
    }

    public static void FinalizeAvailable(Blockchain chain, params ValidatorStake[] signers)
    {
        int height;
        while ((height = chain.NextFinalityHeight) > 0)
        {
            foreach (ValidatorStake stake in chain.GetFinalityValidators(height))
            {
                ValidatorStake signer = signers.FirstOrDefault(v => v.PublicKey == stake.PublicKey);
                if (signer == null) continue;
                chain.AddFinalityVote(chain.CreateFinalityVote(height, signer));
                if (chain.FinalizedHeight == height) break;
            }
            if (chain.FinalizedHeight < height) throw new Exception("Test signers cannot finalize this block.");
        }
    }
}
