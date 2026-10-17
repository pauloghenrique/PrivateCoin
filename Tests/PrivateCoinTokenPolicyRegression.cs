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
                var auto = policy.Copy(); auto.Mintable = false; auto.MaximumSupply = 10000; auto.Burnable = false; auto.AutoBurnBasisPoints = 100;
                Check(auto.CalculateTransferBurn(long.MaxValue) == long.MaxValue / 100, "percent calculation handles Int64 maximum without overflow");
                var taxedCreation = Complete(TokenCreation.Prepare(chain, Empty, publicKeys, "Taxado", "TAX", 0, 10000, address, address, 2, null, auto));
                batches.Confirm(chain, new[] { taxedCreation }, validators);
                string taxedId = taxedCreation.Token.Id;
                var transferDraft = TokenTransfer.Prepare(chain, Empty, new[] { publicKeys[0] }, taxedId, other, 100, address, 2);
                Check(transferDraft.AutomaticBurnAmount == 1 && transferDraft.TokenChangeAmount == 9899, "one percent burn charged on top of recipient amount");
                var taxedTransfer = transferDraft.Complete(transferDraft.InputAddresses.Select(ownerAddress => Sign(ownerAddress, transferDraft.SigningPayload)));
                chain.ValidatePendingTransactions(new[] { taxedTransfer });
                Check(chain.GetTokenRuntime(taxedId).Supply == 10000, "pending automatic burn does not change confirmed supply");
                var bypass = Copy(taxedTransfer); bypass.TokenTransferTerms = null; bypass.Outputs[1].Amount++; Resign(bypass);
                Reject(() => chain.ValidatePendingTransactions(new[] { bypass }), "conserving transfer cannot omit a mandatory automatic burn");
                var underpay = Copy(taxedTransfer); underpay.TokenTransferTerms.BurnAmount = 0; underpay.Outputs[1].Amount++; Resign(underpay);
                Reject(() => chain.ValidatePendingTransactions(new[] { underpay }), "valid holder signatures cannot underpay automatic burn");
                var overpay = Copy(taxedTransfer); overpay.TokenTransferTerms.BurnAmount = 2; overpay.Outputs[1].Amount--; Resign(overpay);
                Reject(() => chain.ValidatePendingTransactions(new[] { overpay }), "automatic burn must match the policy exactly");
                var redirect = Copy(taxedTransfer); redirect.Outputs[1].OneTimeAddress = other; Resign(redirect);
                Reject(() => chain.ValidatePendingTransactions(new[] { redirect }), "untaxed token change cannot be redirected to recipient");
                batches.Confirm(chain, new[] { taxedTransfer }, validators);
                Check(chain.GetTokenRuntime(taxedId).Supply == 9999 && chain.GetTokenBalance(new[] { other }, taxedId) == 100 && chain.GetTokenBalance(new[] { address }, taxedId) == 9899, "confirmed transfer pays recipient and permanently destroys the fee");
                Check(new Blockchain(chain.Blocks).GetTokenRuntime(taxedId).Supply == 9999, "restart replays automatic burn supply");
                using (var wallet = Wallet.FromPrivateKeys(new[] { owner.ToXmlString(true) }))
                {
                    var desktopTransfer = wallet.CreateTokenTransferTransaction(chain, Empty, taxedId, other, 100, 2);
                    Check(desktopTransfer.TokenTransferTerms.BurnAmount == 1, "Desktop wallet includes the automatic burn");
                    chain.ValidatePendingTransactions(new[] { desktopTransfer });
                    Reject(() => wallet.CreateTokenTransferTransaction(chain, Empty, taxedId, other, 9899, 2), "sending entire balance fails when additional burn cannot be funded");
                }
                Check(auto.CalculateTransferBurn(99) == 0 && auto.CalculateTransferBurn(100) == 1, "percentage fractions round down at atomic precision");
                auto.AutoBurnBasisPoints = 0; auto.AutoBurnFixedAtomic = 3; auto.MaximumSupply = 100;
                var fixedFeeCreation = Complete(TokenCreation.Prepare(chain, Empty, publicKeys, "Fixa", "FEE", 0, 100, address, address, 2, null, auto));
                batches.Confirm(chain, new[] { fixedFeeCreation }, validators);
                var fixedDraft = TokenTransfer.Prepare(chain, Empty, new[] { publicKeys[0] }, fixedFeeCreation.Token.Id, other, 1, address, 2);
                var fixedTransfer = fixedDraft.Complete(fixedDraft.InputAddresses.Select(ownerAddress => Sign(ownerAddress, fixedDraft.SigningPayload)));
                Check(fixedDraft.AutomaticBurnAmount == 3 && fixedDraft.TokenChangeAmount == 96, "fixed burn charged even on a small transfer");
                batches.Confirm(chain, new[] { fixedTransfer }, validators);
                Check(chain.GetTokenRuntime(fixedFeeCreation.Token.Id).Supply == 97, "fixed burn reduces current supply without permitting manual burn");
                var invalid = auto.Copy(); invalid.AutoBurnBasisPoints = 100;
                Reject(() => TokenCreation.Prepare(chain, Empty, publicKeys, "Inválido", "INV", 0, 100, address, address, 2, null, invalid), "conflicting burn modes are rejected");
                invalid.AutoBurnFixedAtomic = 0; invalid.AutoBurnBasisPoints = 10001;
                Reject(() => TokenCreation.Prepare(chain, Empty, publicKeys, "Inválido", "INV", 0, 100, address, address, 2, null, invalid), "burn percentage over one hundred rejected");
                // Pool-owned rules do not inherit the direct-transfer fee configured on TAX.
                Func<UnsignedTokenExchange, Transaction> finishPool = draft => draft.Complete(draft.Inputs.Take(draft.Kind == TransactionKind.LiquidityClose ? draft.Inputs.Count : draft.UserInputCount).Select(input => Sign(Hash(input.PublicKey), draft.SigningPayload)));
                Func<Transaction, UnspentOutput[]> poolReserves = tx => chain.GetUnspentOutputs(new[] { address }).Where(output => output.TransactionId == tx.Id && output.IsPoolReserve).OrderBy(output => output.OutputIndex).ToArray();
                var plainPool = finishPool(TokenExchange.PrepareLiquidity(chain, Empty, publicKeys, null, taxedId, 100, 1000, address, 2));
                batches.Confirm(chain, new[] { plainPool }, validators);
                var plainReserves = poolReserves(plainPool);
                var untaxedSwap = finishPool(TokenExchange.PrepareSwap(chain, Empty, publicKeys, plainReserves[0], plainReserves[1], publicKeys[0], 0, 100, 1, address, 2));
                Check(!plainPool.PoolBurnEnabled && untaxedSwap.LiquiditySwap.OutputBurn == 0, "pool burn disabled by default despite direct token fee");
                batches.Confirm(chain, new[] { untaxedSwap }, validators);
                Check(chain.GetTokenRuntime(taxedId).Supply == 9999, "default pool does not burn token supply");
                var burnPool = finishPool(TokenExchange.PrepareLiquidity(chain, Empty, publicKeys, null, taxedId, 100, 1000, address, 2, burnPolicy: new PoolBurnPolicy { BasisPoints = 1000 }));
                batches.Confirm(chain, new[] { burnPool }, validators);
                var burnReserves = poolReserves(burnPool);
                long gross = TokenExchange.Quote(100, 1000, 100);
                Reject(() => TokenExchange.PrepareSwap(chain, Empty, publicKeys, burnReserves[0], burnReserves[1], publicKeys[0], 0, 100, gross, address, 2), "minimum receive uses net payout after pool burn");
                var poolSwap = finishPool(TokenExchange.PrepareSwap(chain, Empty, publicKeys, burnReserves[0], burnReserves[1], publicKeys[0], 0, 100, 1, address, 2));
                Check(poolSwap.LiquiditySwap.InputBurn == 0 && poolSwap.LiquiditySwap.OutputBurn == gross / 10 && poolSwap.Outputs[2].Amount == gross - gross / 10, "own pool percentage replaces direct fee and exempts native input");
                chain.ValidatePendingTransactions(new[] { poolSwap });
                Check(chain.GetTokenRuntime(taxedId).Supply == 9999, "pending pool burn does not alter confirmed supply");
                var changedRule = Copy(poolSwap); changedRule.PoolBurnPolicy.BasisPoints = 500;
                // Only user inputs have signatures; reserve authorization is consensus controlled.
                Action<Transaction> resignPool = tx => { var payload = Encoding.UTF8.GetBytes((string)typeof(Transaction).GetMethod("SigningPayload", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(tx, null)); foreach (var input in tx.Inputs.Take(tx.Inputs.Count - 2)) input.Signature = Sign(Hash(input.PublicKey), payload); tx.Id = LegacyConsensusFixture.Id(tx); };
                resignPool(changedRule);
                Reject(() => chain.ValidatePendingTransactions(new[] { changedRule }), "pool rate immutable even with valid trader signatures");
                var omittedBurn = Copy(poolSwap); omittedBurn.LiquiditySwap.OutputBurn = 0; omittedBurn.Outputs[2].Amount += gross / 10; resignPool(omittedBurn);
                Reject(() => chain.ValidatePendingTransactions(new[] { omittedBurn }), "trader cannot bypass pool burn");
                batches.Confirm(chain, new[] { poolSwap }, validators);
                long afterPool = 9999 - gross / 10;
                Check(chain.GetTokenRuntime(taxedId).Supply == afterPool && chain.GetTokenRuntime(taxedId).Issued == 10000, "confirmed pool burn reduces supply without reopening issuance cap");
                burnReserves = poolReserves(poolSwap);
                var reverse = finishPool(TokenExchange.PrepareSwap(chain, Empty, publicKeys, burnReserves[0], burnReserves[1], publicKeys[0], 1, 100, 1, address, 2));
                Check(reverse.LiquiditySwap.InputBurn == 10 && reverse.LiquiditySwap.OutputBurn == 0, "reverse swap charges token input and exempts native output");
                batches.Confirm(chain, new[] { reverse }, validators);
                Check(new Blockchain(chain.Blocks).GetTokenRuntime(taxedId).Supply == afterPool - 10, "restart replays both pool burn directions");
                var fixedPool = finishPool(TokenExchange.PrepareLiquidity(chain, Empty, publicKeys, taxedId, fixedToken.Token.Id, 100, 20, address, 2, allowUserLiquidity: true, burnPolicy: new PoolBurnPolicy { FixedA = 3, FixedB = 1 }));
                batches.Confirm(chain, new[] { fixedPool }, validators);
                var fixedReserves = poolReserves(fixedPool);
                var added = finishPool(TokenExchange.PrepareLiquidityChange(chain, Empty, publicKeys, fixedReserves[0], fixedReserves[1], publicKeys[0], address, 10, true, 2));
                batches.Confirm(chain, new[] { added }, validators);
                fixedReserves = poolReserves(added);
                var removed = finishPool(TokenExchange.PrepareLiquidityChange(chain, Empty, publicKeys, fixedReserves[0], fixedReserves[1], publicKeys[0], address, 10, false, 2));
                batches.Confirm(chain, new[] { removed }, validators);
                Check(chain.GetTokenRuntime(fixedToken.Token.Id).Supply == 100 && removed.PoolBurnPolicy.FixedB == 1, "liquidity deposits and withdrawals preserve rule without burning");
                fixedReserves = poolReserves(removed);
                var fixedSwap = finishPool(TokenExchange.PrepareSwap(chain, Empty, publicKeys, fixedReserves[0], fixedReserves[1], publicKeys[0], 0, 20, 1, address, 2));
                Check(fixedSwap.LiquiditySwap.InputBurn == 3 && fixedSwap.LiquiditySwap.OutputBurn == 1, "token pair burns independent fixed amount for each asset");
                batches.Confirm(chain, new[] { fixedSwap }, validators);
                Check(chain.GetTokenRuntime(taxedId).Supply == afterPool - 13 && chain.GetTokenRuntime(fixedToken.Token.Id).Supply == 99, "fixed pool burn works even on token without direct burn");
                fixedReserves = poolReserves(fixedSwap);
                var closing = finishPool(TokenExchange.PrepareReserveOperation(chain, Empty, publicKeys, fixedReserves[0], fixedReserves[1], address, 2, true));
                batches.Confirm(chain, new[] { closing }, validators);
                Check(chain.GetTokenRuntime(fixedToken.Token.Id).Supply == 99 && closing.PoolBurnPolicy.FixedA == 3, "pool closure preserves rule and returns reserves without burn");
                Check(chain.IsValid() && Blockchain.MaximumSupply == 180000L * Blockchain.OneCoin, "full chain valid and native supply constants unchanged");
                Console.WriteLine("PASS " + checks + " token policy checks");
            }
        }
    }
}
