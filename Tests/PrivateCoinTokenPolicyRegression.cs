using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using PrivateCoin.Core;

internal static class PrivateCoinTokenPolicyRegression
{
    static int checks;
    static readonly Transaction[] Empty = new Transaction[0];
    static readonly Dictionary<string, RSACryptoServiceProvider> Keys = new Dictionary<string, RSACryptoServiceProvider>();
    static string Hash(string value) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant(); }
    static string Address(RSACryptoServiceProvider key) => Hash(key.ToXmlString(false));
    static string Sign(string address, byte[] payload) => Convert.ToBase64String(Keys[address].SignData(payload, CryptoConfig.MapNameToOID("SHA256")));
    static void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
    static void Reject(Action action, string label)
    {
        try { action(); } catch (Exception error) when (error is InvalidOperationException || error is ArgumentException || error is OverflowException) { Check(true, label); return; }
        throw new Exception("Expected rejection: " + label);
    }
    static Transaction Complete(UnsignedTokenCreation draft) => draft.Complete(draft.InputAddresses.Select(address => Sign(address, draft.SigningPayload)));
    static Transaction Complete(UnsignedTokenManagement draft) => draft.Complete(draft.InputAddresses.Select(address => Sign(address, draft.SigningPayload)), draft.AdministratorAddresses.Select(address => Sign(address, draft.SigningPayload)));
    static Transaction Copy(Transaction value)
    {
        using (var stream = new MemoryStream()) { var serializer = new DataContractJsonSerializer(typeof(Transaction)); serializer.WriteObject(stream, value); stream.Position = 0; return (Transaction)serializer.ReadObject(stream); }
    }
    static void Resign(Transaction tx)
    {
        var payload = Encoding.UTF8.GetBytes((string)typeof(Transaction).GetMethod("SigningPayload", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(tx, null));
        foreach (var input in tx.Inputs) input.Signature = Sign(Hash(input.PublicKey), payload);
        foreach (var proof in tx.TokenAuthorizations ?? new List<TokenAuthorization>()) proof.Signature = Sign(Hash(proof.PublicKey), payload);
        tx.Id = LegacyConsensusFixture.Id(tx);
    }
    public static void Main()
    {
        using (var owner = new RSACryptoServiceProvider(2048)) using (var coadmin = new RSACryptoServiceProvider(2048))
        using (var first = new Wallet()) using (var second = new Wallet())
        {
            owner.PersistKeyInCsp = false; coadmin.PersistKeyInCsp = false;
            string address = Address(owner), other = Address(coadmin), a = first.CreateReceiveAddress(), b = second.CreateReceiveAddress();
            Keys[address] = owner; Keys[other] = coadmin;
            var publicKeys = new[] { owner.ToXmlString(false), coadmin.ToXmlString(false) };
            var chain = new Blockchain(); LegacyConsensusFixture.FundBatch(chain, address, other, a, b);
            LegacyConsensusFixture.SelfBlock(chain, new[] { first.CreateStakeLockTransaction(chain, Empty, a, Blockchain.OneCoin, 0), second.CreateStakeLockTransaction(chain, Empty, b, Blockchain.OneCoin, 0) });
            var validators = new[] { first.CreateValidatorStake(a, Blockchain.OneCoin), second.CreateValidatorStake(b, Blockchain.OneCoin) };
            using (var batches = new ValidationBatchFixture(chain))
            {
                var policy = new TokenPolicy { Mintable = true, MaximumSupply = 200, Burnable = true, Pausable = true,
                    Administrators = new List<string> { address, other }, RequiredSignatures = 2, Description = "Token de teste | : ◇", Website = "https://example.com" };
                var create = Complete(TokenCreation.Prepare(chain, Empty, publicKeys, "Política", "POL", 0, 100, address, address, 2, null, policy));
                chain.ValidatePendingTransactions(new[] { create }); batches.Confirm(chain, new[] { create }, validators);
                string id = create.Token.Id;
                Check(chain.IsValid() && chain.GetTokenRuntime(id).Supply == 100 && chain.GetTokens().Single().Policy.RequiredSignatures == 2, "policy confirmed and copied into catalogue");
                Reject(() => TokenManagement.Prepare(chain, Empty, new[] { publicKeys[0] }, id, "mint", 1, address, address, 2), "missing second administrator key");
                var forgedRegistration = chain.CreateWalletCreationTransaction(new string('e', 64), Empty);
                forgedRegistration.TokenCommand = new TokenCommand { TokenId = id, Operation = "mint", Amount = 100, Revision = 0 };
                forgedRegistration.TokenAuthorizations = new List<TokenAuthorization>(); forgedRegistration.Id = LegacyConsensusFixture.Id(forgedRegistration);
                Reject(() => chain.ValidatePendingTransactions(new[] { forgedRegistration }), "self-validated registration cannot carry token commands");
                var mint = Complete(TokenManagement.Prepare(chain, Empty, publicKeys, id, "mint", 50, other, address, 2));
                var missing = Copy(mint); missing.TokenAuthorizations.RemoveAt(0); Resign(missing);
                Reject(() => chain.ValidatePendingTransactions(new[] { missing }), "threshold enforced by consensus after valid input signatures");
                var duplicate = Copy(mint); duplicate.TokenAuthorizations[1] = duplicate.TokenAuthorizations[0]; Resign(duplicate);
                Reject(() => chain.ValidatePendingTransactions(new[] { duplicate }), "duplicate administrator cannot meet threshold");
                var tampered = Copy(mint); tampered.TokenCommand.Amount++; tampered.Id = LegacyConsensusFixture.Id(tampered);
                Reject(() => chain.ValidatePendingTransactions(new[] { tampered }), "command amount committed by signatures");
                chain.ValidatePendingTransactions(new[] { mint });
                Check(chain.GetTokenRuntime(id).Supply == 100, "pending mint does not change confirmed supply");
                batches.Confirm(chain, new[] { mint }, validators);
                Check(chain.GetTokenRuntime(id).Supply == 150 && chain.GetTokenBalance(new[] { other }, id) == 50, "mint changes only selected token supply and recipient balance");
                Reject(() => TokenManagement.Prepare(chain, Empty, publicKeys, id, "mint", 51, address, address, 2), "cumulative issuance cap");
                using (var wallet = Wallet.FromPrivateKeys(new[] { owner.ToXmlString(true) }))
                    batches.Confirm(chain, new[] { wallet.CreateTransaction(chain, Empty, other, 500, 2) }, validators);
                var burn = Complete(TokenManagement.Prepare(chain, Empty, new[] { publicKeys[1] }, id, "burn", 20, null, other, 2));
                batches.Confirm(chain, new[] { burn }, validators);
                Check(chain.GetTokenRuntime(id).Supply == 130 && chain.GetTokenRuntime(id).Issued == 150 && chain.GetTokenBalance(new[] { other }, id) == 30, "holder burn reduces supply without administrative permission");
                Reject(() => TokenManagement.Prepare(chain, Empty, publicKeys, id, "mint", 51, address, address, 2), "burn does not reopen issuance cap");
                var poolDraft = TokenExchange.PrepareLiquidity(chain, Empty, new[] { publicKeys[0] }, null, id, 1000, 20, address, 2);
                var pool = poolDraft.Complete(poolDraft.Inputs.Select(input => Sign(Hash(input.PublicKey), poolDraft.SigningPayload)));
                batches.Confirm(chain, new[] { pool }, validators);
                var reserves = chain.GetUnspentOutputs(new[] { address }).Where(output => output.TransactionId == pool.Id && output.IsPoolReserve).OrderBy(output => output.OutputIndex).ToArray();
                var swapDraft = TokenExchange.PrepareSwap(chain, Empty, new[] { publicKeys[1] }, reserves[0], reserves[1], publicKeys[0], 0, 100, 1, other, 2);
                var swap = swapDraft.Complete(swapDraft.Inputs.Take(swapDraft.UserInputCount).Select(input => Sign(Hash(input.PublicKey), swapDraft.SigningPayload)));
                var pause = Complete(TokenManagement.Prepare(chain, Empty, publicKeys, id, "pause", 0, null, address, 2));
                var forgedNative = Copy(pause); forgedNative.Outputs[0].Amount++; Resign(forgedNative);
                Reject(() => chain.ValidatePendingTransactions(new[] { forgedNative }), "token management cannot mint native POVIX");
                batches.Confirm(chain, new[] { pause }, validators);
                Check(chain.GetTokenRuntime(id).Paused, "pause confirmed by administrators");
                Reject(() => chain.ValidatePendingTransactions(new[] { swap }), "pause also rejects pool swaps");
                Reject(() => TokenManagement.Prepare(chain, Empty, publicKeys, id, "mint", 1, address, address, 2), "mint blocked while paused");
                Reject(() => TokenManagement.Prepare(chain, Empty, publicKeys, id, "burn", 1, null, address, 2), "burn blocked while paused");
                using (var wallet = Wallet.FromPrivateKeys(new[] { owner.ToXmlString(true) }))
                {
                    var transfer = wallet.CreateTokenTransferTransaction(chain, Empty, id, other, 1, 2);
                    Reject(() => chain.ValidatePendingTransactions(new[] { transfer }), "ordinary token transfer blocked while paused");
                }
                var resume = Complete(TokenManagement.Prepare(chain, Empty, publicKeys, id, "resume", 0, null, address, 2));
                batches.Confirm(chain, new[] { resume }, validators);
                Check(!chain.GetTokenRuntime(id).Paused, "resume preserves token state");
                swapDraft = TokenExchange.PrepareSwap(chain, Empty, publicKeys, reserves[0], reserves[1], publicKeys[0], 0, 100, 1, address, 2);
                swap = swapDraft.Complete(swapDraft.Inputs.Take(swapDraft.UserInputCount).Select(input => Sign(Hash(input.PublicKey), swapDraft.SigningPayload)));
                chain.ValidatePendingTransactions(new[] { swap });
                Check(true, "pool swaps become valid after resume with current funding");
                var stale = Copy(mint); Resign(stale);
                Reject(() => chain.ValidatePendingTransactions(new[] { stale }), "old command replay rejected");
                var restored = new Blockchain(chain.Blocks);
                Check(restored.IsValid() && restored.GetTokenRuntime(id).Supply == 130 && restored.GetTokenRuntime(id).Revision == 4, "replay restores issuance burn pause and revision");
                var vesting = policy.Copy(); vesting.Mintable = false; vesting.MaximumSupply = 9; vesting.VestingStartHeight = chain.Blocks.Count + 3; vesting.VestingIntervalBlocks = 2; vesting.VestingTranches = 3;
                var locked = Complete(TokenCreation.Prepare(chain, Empty, publicKeys, "Parcelas", "PAR", 0, 9, address, address, 2, null, vesting));
                batches.Confirm(chain, new[] { locked }, validators);
                Check(chain.GetSpendableTokenOutputs(new[] { address }, Empty, locked.Token.Id).Count == 0, "all initial tranches start locked");
                var forgedLock = Copy(locked); forgedLock.Outputs[0].UnlockHeight = 0; Resign(forgedLock);
                Reject(() => chain.ValidatePendingTransactions(new[] { forgedLock }), "signed creation cannot bypass schedule");
                while (chain.Blocks.Count < vesting.VestingStartHeight) LegacyConsensusFixture.FundBatch(chain, "advance-" + chain.Blocks.Count.ToString(CultureInfo.InvariantCulture));
                Check(chain.GetSpendableTokenOutputs(new[] { address }, Empty, locked.Token.Id).Sum(output => output.Output.Amount) == 3, "only first tranche unlocks at first release block");
                using (var wallet = Wallet.FromPrivateKeys(new[] { owner.ToXmlString(true) }))
                {
                    Reject(() => wallet.CreateTokenTransferTransaction(chain, Empty, locked.Token.Id, other, 4, 2), "locked balance cannot fund a larger transfer");
                    var transfer = wallet.CreateTokenTransferTransaction(chain, Empty, locked.Token.Id, other, 3, 2);
                    batches.Confirm(chain, new[] { transfer }, validators);
                    Check(chain.GetTokenBalance(new[] { other }, locked.Token.Id) == 3, "released tranche can be transferred");
                }
                var confidential = policy.Copy(); confidential.Privacy = "confidential";
                Reject(() => TokenCreation.Prepare(chain, Empty, publicKeys, "Sigilo", "SIG", 0, 10, address, address, 2, null, confidential), "confidential mode rejected rather than publishing plaintext as private");
                var noMint = policy.Copy(); noMint.Mintable = false; noMint.MaximumSupply = 100;
                var fixedToken = Complete(TokenCreation.Prepare(chain, Empty, publicKeys, "Fixo", "FIX", 0, 100, address, address, 2, null, noMint));
                batches.Confirm(chain, new[] { fixedToken }, validators);
                Reject(() => TokenManagement.Prepare(chain, Empty, publicKeys, fixedToken.Token.Id, "mint", 1, address, address, 2), "fixed supply cannot be expanded");
                Check(chain.IsValid() && Blockchain.MaximumSupply == 180000L * Blockchain.OneCoin, "full chain valid and native supply constants unchanged");
                Console.WriteLine("PASS " + checks + " token policy checks");
            }
        }
    }
}
