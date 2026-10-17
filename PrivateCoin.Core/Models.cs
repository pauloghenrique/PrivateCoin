using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;

namespace PrivateCoin.Core
{
    public enum TransactionKind
    {
        Transfer = 0,
        StakeLock = 1,
        StakeUnlock = 2,
        TokenCreate = 3,
        TokenTransfer = 4,
        WalletCreate = 5,
        WalletDistribution = 6,
        LiquidityPool = 7,
        LiquiditySwap = 8,
        LiquidityClose = 9,
        LiquidityAdd = 10,
        LiquidityRemove = 11,
        TokenManage = 12,
        LaunchCreate = 13,
        LaunchContribute = 14,
        LaunchActivate = 15,
        LaunchRefund = 16
    }

    [DataContract]
    public sealed class LiquiditySwapTerms
    {
        [DataMember(Order = 1)] public int InputSide { get; set; }
        [DataMember(Order = 2)] public long Amount { get; set; }
        [DataMember(Order = 3, EmitDefaultValue = false)] public long InputBurn { get; set; }
        [DataMember(Order = 4, EmitDefaultValue = false)] public long OutputBurn { get; set; }
    }

    [DataContract]
    public sealed class PoolBurnPolicy
    {
        [DataMember(Order = 1)] public int BasisPoints { get; set; }
        [DataMember(Order = 2)] public long FixedA { get; set; }
        [DataMember(Order = 3)] public long FixedB { get; set; }
        public PoolBurnPolicy Copy() => new PoolBurnPolicy { BasisPoints = BasisPoints, FixedA = FixedA, FixedB = FixedB };
        internal string Canonical() => BasisPoints.ToString(CultureInfo.InvariantCulture) + ":" + FixedA.ToString(CultureInfo.InvariantCulture) + ":" + FixedB.ToString(CultureInfo.InvariantCulture);
        public TokenPolicy ForAsset(int side) => new TokenPolicy { AutoBurnBasisPoints = BasisPoints, AutoBurnFixedAtomic = side == 0 ? FixedA : FixedB };
        internal void Validate(string a, string b)
        {
            if (BasisPoints < 0 || BasisPoints > 10000 || FixedA < 0 || FixedB < 0 ||
                BasisPoints > 0 && (FixedA != 0 || FixedB != 0) || BasisPoints == 0 && FixedA == 0 && FixedB == 0 ||
                a == null && FixedA != 0 || b == null && FixedB != 0)
                throw new InvalidOperationException("Invalid pool burn policy; POVIX cannot be burned.");
        }
    }

    [DataContract]
    public sealed class PoolParticipation
    {
        [DataMember(Order = 1)] public Dictionary<string, long> Shares { get; set; } = new Dictionary<string, long>();
        public PoolParticipation Copy() => new PoolParticipation { Shares = new Dictionary<string, long>(Shares, StringComparer.Ordinal) };
        public long Total => Shares.Values.Aggregate(0L, (sum, value) => checked(sum + value));
        public string Canonical() => string.Join(",", Shares.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Key + ":" + item.Value.ToString(CultureInfo.InvariantCulture)));
    }

    [DataContract]
    public sealed class LiquidityChangeTerms
    {
        [DataMember(Order = 1)] public string Address { get; set; }
        [DataMember(Order = 2)] public long Shares { get; set; }
    }

    [DataContract]
    public sealed class LaunchPoolState
    {
        [DataMember(Order = 1)] public long Target { get; set; }
        [DataMember(Order = 2)] public long SaleTokens { get; set; }
        [DataMember(Order = 3)] public long TotalTokens { get; set; }
        [DataMember(Order = 4)] public int DeadlineHeight { get; set; }
        [DataMember(Order = 5)] public bool AllowUserLiquidity { get; set; }
        [DataMember(Order = 6)] public Dictionary<string, long> Contributions { get; set; } = new Dictionary<string, long>();
        public long Raised => Contributions.Values.Aggregate(0L, (total, value) => checked(total + value));
        public string Terms() => string.Join("|", new[] { Target.ToString(CultureInfo.InvariantCulture), SaleTokens.ToString(CultureInfo.InvariantCulture),
            TotalTokens.ToString(CultureInfo.InvariantCulture), DeadlineHeight.ToString(CultureInfo.InvariantCulture), AllowUserLiquidity ? "1" : "0" });
        public string Canonical() => Terms() + "|" + string.Join(",", (Contributions ?? new Dictionary<string, long>()).OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Key + ":" + item.Value.ToString(CultureInfo.InvariantCulture)));
        public LaunchPoolState Copy() => new LaunchPoolState { Target = Target, SaleTokens = SaleTokens, TotalTokens = TotalTokens,
            DeadlineHeight = DeadlineHeight, AllowUserLiquidity = AllowUserLiquidity, Contributions = new Dictionary<string, long>(Contributions, StringComparer.Ordinal) };
    }

    [DataContract]
    public sealed class TokenDefinition
    {
        [DataMember(Order = 1)] public string Id { get; set; }
        [DataMember(Order = 2)] public string Name { get; set; }
        [DataMember(Order = 3)] public string Symbol { get; set; }
        [DataMember(Order = 4)] public int Decimals { get; set; }
        [DataMember(Order = 5)] public long Supply { get; set; }
        [DataMember(Order = 6, EmitDefaultValue = false)] public TokenPolicy Policy { get; set; }

        public const int MaximumNameLength = 64;
        public const int MaximumSymbolLength = 10;
        public const int MaximumDecimals = 8;

        internal static string IdFor(TransactionInput input)
        {
            return Crypto.Sha256("povix-token-v1|" + input.TransactionId + "|" +
                input.OutputIndex.ToString(CultureInfo.InvariantCulture));
        }

        internal void Validate()
        {
            if (string.IsNullOrWhiteSpace(Name) || Name.Length > MaximumNameLength || Name.Any(char.IsControl) ||
                string.IsNullOrEmpty(Symbol) || Symbol.Length > MaximumSymbolLength ||
                Symbol.Any(c => c < 'A' || c > 'Z') || Decimals < 0 || Decimals > MaximumDecimals || Supply <= 0)
                throw new InvalidOperationException("Invalid token: name up to 64 characters, symbol A-Z up to 10, decimals 0-8, positive atomic supply required.");
            Policy?.Validate(Supply);
        }
    }

    [DataContract]
    public sealed class TokenTransferTerms
    {
        [DataMember(Order = 1)] public string TokenId { get; set; }
        [DataMember(Order = 2)] public long Amount { get; set; }
        [DataMember(Order = 3)] public long BurnAmount { get; set; }
        internal string Canonical() => TokenId + ":" + Amount.ToString(CultureInfo.InvariantCulture) + ":" + BurnAmount.ToString(CultureInfo.InvariantCulture);
    }

    [DataContract]
    public sealed class BlockValidator
    {
        [DataMember(Order = 1)] public string ValidatorId { get; set; }
        [DataMember(Order = 2)] public string RewardAddress { get; set; }
        [DataMember(Order = 3)] public long LockedAmount { get; set; }
        [DataMember(Order = 4)] public bool IsCreator { get; set; }
        [DataMember(Order = 5, EmitDefaultValue = false)] public List<string> OwnedAddresses { get; set; }
        [DataMember(Order = 6)] public string PublicKey { get; set; }
        [DataMember(Order = 7)] public string VoteSignature { get; set; }
    }

    [DataContract]
    public sealed class TransactionValidation
    {
        [DataMember(Order = 1)] public string TransactionId { get; set; }
        [DataMember(Order = 2)] public string ValidatorId { get; set; }
        [DataMember(Order = 3)] public string RewardAddress { get; set; }
        [DataMember(Order = 4)] public string PublicKey { get; set; }
        [DataMember(Order = 5)] public string Signature { get; set; }

        internal string ProofPayload()
        {
            return TransactionId + ":" + ValidatorId + ":" + RewardAddress + ":" + PublicKey + ":" + Signature;
        }
    }

    [DataContract]
    public sealed class TransactionApproval
    {
        [DataMember(Order = 1)] public int AnchorHeight { get; set; }
        [DataMember(Order = 2)] public string AnchorHash { get; set; }
        [DataMember(Order = 3)] public string CollateralTransactionId { get; set; }
        [DataMember(Order = 4)] public int CollateralOutputIndex { get; set; }
        [DataMember(Order = 5)] public TransactionValidation Proof { get; set; }

        internal string ProofPayload()
        {
            return AnchorHeight.ToString(CultureInfo.InvariantCulture) + ":" + AnchorHash + ":" +
                CollateralTransactionId + ":" + CollateralOutputIndex.ToString(CultureInfo.InvariantCulture) + ":" +
                (Proof == null ? string.Empty : Proof.ProofPayload());
        }
    }

    [DataContract]
    public sealed class TransactionInput
    {
        [DataMember(Order = 1)] public string TransactionId { get; set; }
        [DataMember(Order = 2)] public int OutputIndex { get; set; }
        [DataMember(Order = 3)] public string PublicKey { get; set; }
        [DataMember(Order = 4)] public string Signature { get; set; }
    }

    [DataContract]
    public sealed class TransactionOutput
    {
        [DataMember(Order = 1)] public long Amount { get; set; }
        [DataMember(Order = 3, EmitDefaultValue = false)] public string AssetId { get; set; }
        // A one-use hash. No persistent wallet address is ever written to the chain.
        [DataMember(Order = 2)] public string OneTimeAddress { get; set; }
        [DataMember(Order = 4, EmitDefaultValue = false)] public int UnlockHeight { get; set; }
    }

    [DataContract]
    public sealed class Transaction
    {
        [DataMember(Order = 1)] public string Id { get; set; }
        [DataMember(Order = 2)] public long TimestampUtcTicks { get; set; }
        [DataMember(Order = 3)] public List<TransactionInput> Inputs { get; set; } = new List<TransactionInput>();
        [DataMember(Order = 4)] public List<TransactionOutput> Outputs { get; set; } = new List<TransactionOutput>();
        [DataMember(Order = 5, EmitDefaultValue = false)] public long Fee { get; set; }
        [DataMember(Order = 6, EmitDefaultValue = false)] public TransactionKind Kind { get; set; }
        [DataMember(Order = 7, EmitDefaultValue = false)] public string ValidatorPublicKey { get; set; }
        [DataMember(Order = 8, EmitDefaultValue = false)] public string ValidatorRewardAddress { get; set; }
        [DataMember(Order = 9, EmitDefaultValue = false)] public List<string> ValidatorOwnedAddresses { get; set; }

        [DataMember(Order = 10, EmitDefaultValue = false)] public TokenDefinition Token { get; set; }
        [DataMember(Order = 11, EmitDefaultValue = false)] public string WalletDistributionId { get; set; }

        [DataMember(Order = 12, EmitDefaultValue = false)] public TransactionApproval TransactionApproval { get; set; }
        [DataMember(Order = 13, EmitDefaultValue = false)] public LiquiditySwapTerms LiquiditySwap { get; set; }

        [DataMember(Order = 14, EmitDefaultValue = false)] public PoolParticipation PoolParticipation { get; set; }
        [DataMember(Order = 15, EmitDefaultValue = false)] public LiquidityChangeTerms LiquidityChange { get; set; }
        [DataMember(Order = 16, EmitDefaultValue = false)] public TokenCommand TokenCommand { get; set; }
        [DataMember(Order = 17, EmitDefaultValue = false)] public List<TokenAuthorization> TokenAuthorizations { get; set; }
        [DataMember(Order = 18, EmitDefaultValue = false)] public TokenTransferTerms TokenTransferTerms { get; set; }
        [DataMember(Order = 19, EmitDefaultValue = false)] public PoolBurnPolicy PoolBurnPolicy { get; set; }
        public bool PoolBurnEnabled => PoolBurnPolicy != null;

        [DataMember(Order = 20, EmitDefaultValue = false)] public LaunchPoolState Launch { get; set; }

        internal string SigningPayload()
        {
            var value = new StringBuilder(TimestampUtcTicks.ToString(CultureInfo.InvariantCulture));
            foreach (var input in Inputs)
                value.Append('|').Append(input.TransactionId).Append(':').Append(input.OutputIndex.ToString(CultureInfo.InvariantCulture));
            foreach (var output in Outputs)
                value.Append('|').Append(output.Amount.ToString(CultureInfo.InvariantCulture)).Append(':').Append(output.OneTimeAddress);
            // Preserve the identifiers of fee-free transactions created by older clients.
            if (Fee != 0) value.Append("|fee:").Append(Fee.ToString(CultureInfo.InvariantCulture));
            if (Kind != TransactionKind.Transfer)
                value.Append("|kind:").Append(((int)Kind).ToString(CultureInfo.InvariantCulture))
                    .Append("|validator:").Append(ValidatorPublicKey).Append("|reward:").Append(ValidatorRewardAddress)
                    .Append("|owned:").Append(string.Join(",", ValidatorOwnedAddresses ?? new List<string>()));
            if (WalletDistributionId != null) AppendField(value, WalletDistributionId);
            // Length-prefix new fields so delimiters in token names cannot change the encoding.
            if (Token != null || Outputs.Any(output => output.AssetId != null))
            {
                value.Append("|assets-v1|");
                foreach (var output in Outputs) AppendField(value, output.AssetId);
                if (Token != null)
                {
                    AppendField(value, Token.Id); AppendField(value, Token.Name); AppendField(value, Token.Symbol);
                    value.Append('|').Append(Token.Decimals.ToString(CultureInfo.InvariantCulture))
                        .Append('|').Append(Token.Supply.ToString(CultureInfo.InvariantCulture));
                }
            }
            if (LiquiditySwap != null)
                value.Append("|pool-swap-v1|").Append(LiquiditySwap.InputSide.ToString(CultureInfo.InvariantCulture))
                    .Append('|').Append(LiquiditySwap.Amount.ToString(CultureInfo.InvariantCulture));
            if (LiquiditySwap != null && (LiquiditySwap.InputBurn != 0 || LiquiditySwap.OutputBurn != 0))
                value.Append("|pool-burn-v1|").Append(LiquiditySwap.InputBurn.ToString(CultureInfo.InvariantCulture))
                    .Append('|').Append(LiquiditySwap.OutputBurn.ToString(CultureInfo.InvariantCulture));
            if (PoolParticipation != null) value.Append("|pool-participation-v1|").Append(PoolParticipation.Canonical());
            if (LiquidityChange != null) value.Append("|pool-change-v1|").Append(LiquidityChange.Address)
                .Append('|').Append(LiquidityChange.Shares.ToString(CultureInfo.InvariantCulture));
            if (Token?.Policy != null) value.Append("|token-policy-v1|").Append(Token.Policy.Canonical());
            if (TokenCommand != null) value.Append("|token-command-v1|").Append(TokenCommand.Canonical());
            if (TokenAuthorizations != null)
                foreach (var proof in TokenAuthorizations) AppendField(value, proof.PublicKey);
            if (Outputs.Any(output => output.UnlockHeight != 0))
                value.Append("|token-locks-v1|").Append(string.Join(",", Outputs.Select(output => output.UnlockHeight.ToString(CultureInfo.InvariantCulture))));
            if (TokenTransferTerms != null) value.Append("|token-transfer-burn-v1|").Append(TokenTransferTerms.Canonical());
            if (PoolBurnEnabled) value.Append("|pool-burn-enabled-v1|").Append(PoolBurnPolicy.Canonical());
            if (Launch != null) value.Append("|pool-launch-v1|").Append(Launch.Canonical());
            return value.ToString();
        }

        private static void AppendField(StringBuilder value, string field)
        {
            value.Append('|').Append(field == null ? -1 : field.Length).Append(':').Append(field);
        }

        /// <summary>The approval commits the fee output at the index immediately after the creator's outputs.</summary>
        public TransactionOutput GetValidationFeeOutput()
        {
            return TransactionApproval?.Proof == null || Fee <= 0 ? null :
                new TransactionOutput { Amount = Fee, OneTimeAddress = TransactionApproval.Proof.RewardAddress };
        }

        internal string CalculateId()
        {
            string signatures = string.Join("|", Inputs.Select(i => i.Signature ?? string.Empty));
            if (TokenAuthorizations != null) signatures += "|token-authorizations-v1|" + string.Join("|", TokenAuthorizations.Select(proof => proof.Signature ?? string.Empty));
            return Crypto.Sha256(SigningPayload() + "|" + signatures);
        }
    }

    [DataContract]
    public sealed class Block
    {
        [DataMember(Order = 1)] public int Height { get; set; }
        [DataMember(Order = 2)] public string PreviousHash { get; set; }
        [DataMember(Order = 3)] public long TimestampUtcTicks { get; set; }
        [DataMember(Order = 4)] public long Nonce { get; set; }
        [DataMember(Order = 5)] public List<Transaction> Transactions { get; set; } = new List<Transaction>();
        [DataMember(Order = 6)] public string Hash { get; set; }
        [DataMember(Order = 7, EmitDefaultValue = false)] public List<BlockValidator> Validators { get; set; }
        [DataMember(Order = 8, EmitDefaultValue = false)] public int ConsensusVersion { get; set; }
        [DataMember(Order = 9, EmitDefaultValue = false)] public List<TransactionValidation> TransactionValidations { get; set; }

        [DataMember(Order = 10, EmitDefaultValue = false)] public GenesisRules Rules { get; set; }

        internal string CalculateHash()
        {
            string validatorProof = Validators == null ? string.Empty : string.Join("|", Validators.Select(v =>
                v.ValidatorId + ":" + v.RewardAddress + ":" + v.LockedAmount.ToString(CultureInfo.InvariantCulture) + ":" + v.IsCreator +
                (v.OwnedAddresses == null ? string.Empty : ":addresses:" + string.Join(",", v.OwnedAddresses)) +
                ":key:" + v.PublicKey + ":vote:" + v.VoteSignature));
            return Crypto.Sha256(Height.ToString(CultureInfo.InvariantCulture) + "|" + PreviousHash + "|" +
                TimestampUtcTicks.ToString(CultureInfo.InvariantCulture) + "|" + Nonce.ToString(CultureInfo.InvariantCulture) + "|" +
                string.Join("|", Transactions.Select(t => t.Id)) + (Validators == null ? string.Empty : "|pos|" + validatorProof) +
                ValidationProofPayload() + (Rules == null ? string.Empty : "|genesis-rules:" + Rules.CalculateHash()));
        }

        internal string ValidationProofPayload()
        {
            return ConsensusVersion == 0 && TransactionValidations == null ? string.Empty :
                "|consensus:" + ConsensusVersion.ToString(CultureInfo.InvariantCulture) + "|transaction-validations|" +
                string.Join("|", (TransactionValidations ?? new List<TransactionValidation>()).Select(item => item == null ? string.Empty : item.ProofPayload())) +
                (ConsensusVersion >= 11 ? "|transaction-approvals|" + string.Join("|", Transactions.Select(tx =>
                    tx.TransactionApproval == null ? string.Empty : tx.TransactionApproval.ProofPayload())) : string.Empty);
        }
    }

    public sealed class UnspentOutput
    {
        public PoolBurnPolicy PoolBurnPolicy { get; internal set; }
        public bool PoolBurnEnabled => PoolBurnPolicy != null;
        internal TokenRuntime TokenRuntime { get; set; }
        public string TransactionId { get; internal set; }
        public int OutputIndex { get; internal set; }
        public TransactionOutput Output { get; internal set; }
        public TransactionKind TransactionKind { get; internal set; }
        public PoolParticipation PoolParticipation { get; internal set; }
        public LaunchPoolState Launch { get; internal set; }
        public bool IsPoolReserve => TransactionKind == TransactionKind.LiquidityPool || TransactionKind == TransactionKind.LiquiditySwap ||
            TransactionKind == TransactionKind.LiquidityAdd || TransactionKind == TransactionKind.LiquidityRemove ||
            TransactionKind == TransactionKind.LaunchCreate || TransactionKind == TransactionKind.LaunchContribute || TransactionKind == TransactionKind.LaunchActivate;
        public string ValidatorPublicKey { get; internal set; }
        public string ValidatorRewardAddress { get; internal set; }
        public IReadOnlyCollection<string> ValidatorOwnedAddresses { get; internal set; }
    }

}
