using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PrivateCoin.Core
{
    /// <summary>Rebuilt from confirmed transactions; never accepted as peer-supplied state.</summary>
    public sealed class InitialLaunchStatus
    {
        public bool Created { get; internal set; }
        public int Purchases { get; internal set; }
        public bool Active => Created && Purchases == InitialLaunch.MaximumPurchases;
        public long Remaining => Created ? InitialLaunch.ReserveSupply - Purchases * InitialLaunch.PurchaseAmount : 0;
        public string FundingAssetId { get; internal set; }
        public string ProviderPublicKey { get; internal set; }
        public string RootTransactionId { get; internal set; }
        public string ReserveTransactionId { get; internal set; }
        internal InitialLaunchStatus Copy() => (InitialLaunchStatus)MemberwiseClone();
    }

    /// <summary>Native POVIX bootstrap and standalone local funding assets.</summary>
    public static class InitialLaunch
    {
        public const long ReserveSupply = 359400L * Blockchain.OneCoin;
        public const long PrincipalSupply = 179700L * Blockchain.OneCoin;
        public const long PurchaseAmount = 6L * Blockchain.OneCoin;
        public const int MaximumPurchases = 29950;
        public const int FundingDecimals = 6;
        public const long FundingUnit = 1000000L;
        // Protocol-reserved local assets. These are not bridged or externally backed stablecoins.
        public const string UsdcTokenId = "d0c0ff968d7093a5b3e4b9a617112e66e4d62d5765833d0852387b5ea7d1ab98";
        public const string UsdtTokenId = "ab4232725e9910e3140520ca7b7c0567fb4af5aa261a712244ab0e40b03e9716";
        public const long WalletUsdcReward = 100L * FundingUnit;
        public const int UsdcRewardedWalletLimit = 10000;
        public const long FundingTokenSupply = 1000000L * FundingUnit;
        public static TokenDefinition[] FundingTokens() => new[] {
            new TokenDefinition { Id = UsdcTokenId, Name = "USDC local POVIX (sem ponte)", Symbol = "USDC", Decimals = FundingDecimals, Supply = FundingTokenSupply },
            new TokenDefinition { Id = UsdtTokenId, Name = "USDT local POVIX (sem ponte)", Symbol = "USDT", Decimals = FundingDecimals, Supply = FundingTokenSupply } };
        // Public key only; no private key is retained or needed for ownerless reserves.
        public const string ProviderPublicKey = "<RSAKeyValue><Modulus>kLiPAu2JNZOXjm3KlSO5W4zq0jxqykRwaw+K/Ey4FLedeHJ4DACJS3RH3nMCw4yUwum7gL5qjAwLkpucGN2dEV4+pK3hXysCt530tl247mGCoPOEtQC7CfQiK314/vRuh4Dx8fi/zJ5ZYRq/+7zxxt7UxKlAlmN9DP82HGeKxHKgl3K8VaUh7I8r3zmv3i43XqDhXbtsImqxwgTnmvZeMG/06aV5ekWkZIndGEIP4xR8sKfetbjEWa4u8Yh+tcGftMwHnd6CnBU3J4nSv0SXGaNmk3DRF/oyanu3Sjm7E1N2x0UPKAxOPa1NBhWnK43/SsIDipBchtOOCaNo/RCcLw==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
        internal static string WalletAddress(Transaction tx) => tx.Outputs[tx.Kind == TransactionKind.InitialLaunchCreate ? 4 : 0].OneTimeAddress;
        internal static long WalletReward(Transaction tx) => tx.Outputs[tx.Kind == TransactionKind.InitialLaunchCreate ? 4 : 0].Amount;
        internal static Transaction CreateAutomatic(string recipient, long timestamp)
        {
            string provider = Crypto.Sha256(ProviderPublicKey);
            var tx = new Transaction { Kind = TransactionKind.InitialLaunchCreate, TimestampUtcTicks = timestamp, Outputs = new List<TransactionOutput> {
                new TransactionOutput { Amount = ReserveSupply, OneTimeAddress = provider },
                new TransactionOutput { AssetId = UsdcTokenId, Amount = 0, OneTimeAddress = provider },
                new TransactionOutput { AssetId = UsdcTokenId, Amount = WalletUsdcReward, OneTimeAddress = recipient },
                new TransactionOutput { AssetId = UsdtTokenId, Amount = FundingTokenSupply, OneTimeAddress = recipient },
                new TransactionOutput { Amount = Blockchain.WalletCreationReward, OneTimeAddress = recipient } } };
            tx.Id = tx.CalculateId(); return tx;
        }
        internal static long NextWalletUsdcReward(IDictionary<string, UnspentOutput> utxo)
        {
            var state = TokenGovernance.State(utxo, UsdcTokenId);
            return state != null && state.Issued < FundingTokenSupply ? WalletUsdcReward : 0;
        }
        internal static void ValidateWalletUsdcReward(Transaction tx, IDictionary<string, UnspentOutput> utxo)
        {
            if (tx.Kind != TransactionKind.WalletCreate || tx.WalletDistributionId != null) return;
            if (utxo.ContainsKey(RegistrationPrefix + WalletAddress(tx)))
                throw new InvalidOperationException("Wallet already received its registration reward.");
            long amount = NextWalletUsdcReward(utxo);
            if (tx.Outputs.Count != (amount > 0 ? 2 : 1)) throw new InvalidOperationException("Invalid USDC registration reward outputs.");
            if (amount > 0) {
                var output = tx.Outputs[1];
                if (output.AssetId != UsdcTokenId || output.Amount != amount || output.OneTimeAddress != WalletAddress(tx) ||
                    output.InitialLaunchLocked || output.UnlockHeight != 0)
                    throw new InvalidOperationException("Invalid USDC registration reward.");
            }
        }
        private const string StateKey = "@initial-launch-state";
        private const string RegistrationPrefix = "@initial-launch-wallet:";

        public static bool IsOperation(TransactionKind kind) => kind == TransactionKind.InitialLaunchCreate || kind == TransactionKind.InitialLaunchPurchase;
        public static bool IsFundingAsset(string id) => TokenPolicy.Address(id) && (id == UsdcTokenId || id == UsdtTokenId);
        internal static InitialLaunchStatus State(IDictionary<string, UnspentOutput> utxo)
        {
            UnspentOutput item;
            return utxo.TryGetValue(StateKey, out item) ? item.InitialLaunchState : null;
        }
        internal static bool IsUnlocked(UnspentOutput item, IDictionary<string, UnspentOutput> utxo) =>
            !item.Output.InitialLaunchLocked || State(utxo)?.Active == true;

        internal static void ValidateEnvelope(Transaction tx, IDictionary<string, UnspentOutput> utxo, int version)
        {
            if (version < 24 && (IsOperation(tx.Kind) || tx.InitialLaunchIdentity != null || tx.Outputs.Any(output => output.InitialLaunchLocked) || tx.PoolParticipation?.LockedShares > 0))
                throw new InvalidOperationException("Initial POVIX launch requires consensus v24.");
            if (tx.InitialLaunchIdentity != null && tx.Kind != TransactionKind.InitialLaunchPurchase)
                throw new InvalidOperationException("Only an initial launch purchase may carry its wallet identity proof.");
            for (int i = 0; i < tx.Outputs.Count; i++)
                if (tx.Outputs[i].InitialLaunchLocked && (tx.Kind != TransactionKind.InitialLaunchPurchase || i != 2))
                    throw new InvalidOperationException("Only purchased initial POVIX can use the launch lock.");
            foreach (var input in tx.Inputs)
            {
                UnspentOutput source;
                if (!utxo.TryGetValue(input.TransactionId + ":" + input.OutputIndex.ToString(CultureInfo.InvariantCulture), out source)) continue;
                if (source.OutputIndex < 0) throw new InvalidOperationException("Protocol state cannot be spent.");
                if (!IsUnlocked(source, utxo)) throw new InvalidOperationException("Purchased POVIX remain locked until the launch target is reached.");
            }
        }

        internal static void Validate(Transaction tx, IDictionary<string, UnspentOutput> utxo, UnspentOutput[] sources)
        {
            if (tx.Launch != null || tx.Token != null || tx.PoolBurnEnabled || tx.LiquiditySwap != null || tx.LiquidityChange != null ||
                tx.Outputs.Count < 2 || sources.Any(source => source == null) ||
                tx.Outputs[0].AssetId != null || !IsFundingAsset(tx.Outputs[1].AssetId) ||
                tx.Outputs[0].OneTimeAddress != tx.Outputs[1].OneTimeAddress || tx.Kind != TransactionKind.InitialLaunchCreate && (tx.Fee < Blockchain.TransferFeeStep || tx.Fee > Blockchain.MaximumTransferFee))
                throw new InvalidOperationException("Invalid initial launch transaction or unauthorized local funding asset.");
            var state = State(utxo);
            string provider = tx.Outputs[0].OneTimeAddress;
            if (tx.Kind == TransactionKind.InitialLaunchCreate)
            {
                long rewarded = utxo.Values.Where(item => item.WalletRegistration).Sum(item => item.WalletRegistrationReward);
                if (state != null || tx.InitialLaunchIdentity != null || tx.PoolParticipation != null || tx.Inputs.Count != 0 || tx.Fee != 0 ||
                    rewarded != Blockchain.DistributionSupply - Blockchain.WalletCreationReward ||
                    tx.Outputs.Count != 5 || tx.Outputs[1].AssetId != UsdcTokenId || provider != Crypto.Sha256(ProviderPublicKey) ||
                    string.IsNullOrWhiteSpace(tx.Outputs[4].OneTimeAddress) ||
                    FundingTokens().Any(token => TokenGovernance.State(utxo, token.Id) != null) ||
                    tx.Id != CreateAutomatic(tx.Outputs[4].OneTimeAddress, tx.TimestampUtcTicks).Id)
                    throw new InvalidOperationException("The first rewarded registration must atomically create the canonical initial pool.");
                return;
            }
            var funding = TokenGovernance.State(utxo, tx.Outputs[1].AssetId);
            if (funding == null || funding.Token.Decimals != FundingDecimals) throw new InvalidOperationException("Local funding asset is missing.");
            if (state == null || state.Active || sources.Length < 3 || sources.Count(source => source.IsPoolReserve) != 2)
                throw new InvalidOperationException("The initial launch is not accepting purchases.");
            var a = sources[sources.Length - 2]; var b = sources[sources.Length - 1];
            if (!a.InitialLaunchCollecting || !b.InitialLaunchCollecting || a.TransactionId != state.ReserveTransactionId || b.TransactionId != a.TransactionId ||
                a.OutputIndex != 0 || b.OutputIndex != 1 || a.Output.AssetId != null || b.Output.AssetId != state.FundingAssetId ||
                provider != a.Output.OneTimeAddress || provider != b.Output.OneTimeAddress || provider != Crypto.Sha256(state.ProviderPublicKey) ||
                a.Output.Amount != state.Remaining || b.Output.Amount != state.Purchases * FundingUnit ||
                tx.Outputs[0].Amount != state.Remaining - PurchaseAmount || tx.Outputs[1].AssetId != state.FundingAssetId ||
                tx.Outputs[1].Amount != (state.Purchases + 1L) * FundingUnit)
                throw new InvalidOperationException("Initial launch reserves must decrease by six POVIX and receive exactly one funding unit.");
            var proof = tx.InitialLaunchIdentity;
            if (proof == null || string.IsNullOrWhiteSpace(proof.PublicKey) || string.IsNullOrWhiteSpace(proof.Signature))
                throw new InvalidOperationException("The registered wallet must sign its purchase identity.");
            string identity = Crypto.Sha256(proof.PublicKey);
            UnspentOutput registration;
            if (!utxo.TryGetValue(RegistrationPrefix + identity, out registration) || !registration.WalletRegistration || registration.InitialLaunchPurchased)
                throw new InvalidOperationException("Each registered wallet identity may purchase only once.");
            if (tx.Outputs.Count < 3 || tx.Outputs[2].AssetId != null || tx.Outputs[2].Amount != PurchaseAmount - tx.Fee ||
                tx.Outputs[2].OneTimeAddress != identity || !tx.Outputs[2].InitialLaunchLocked)
                throw new InvalidOperationException("The six-POVIX purchase minus the fee must be assigned to the signing registered wallet.");
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.FromXmlString(proof.PublicKey);
                if (!rsa.VerifyData(Encoding.UTF8.GetBytes(tx.SigningPayload()), CryptoConfig.MapNameToOID(Crypto.SignatureHashAlgorithm), Convert.FromBase64String(proof.Signature)))
                    throw new InvalidOperationException("Invalid initial launch identity signature.");
            }
            var expected = state.Purchases + 1 == MaximumPurchases ? new PoolParticipation { LockedShares = PrincipalSupply } : null;
            if (tx.PoolParticipation?.Canonical() != expected?.Canonical())
                throw new InvalidOperationException("The final purchase must permanently lock all principal liquidity shares.");
        }

        internal static void Record(Transaction tx, IDictionary<string, UnspentOutput> utxo)
        {
            if ((tx.Kind == TransactionKind.WalletCreate || tx.Kind == TransactionKind.InitialLaunchCreate) && tx.WalletDistributionId == null)
            {
                string key = RegistrationPrefix + WalletAddress(tx);
                utxo[key] = Marker(key);
                utxo[key].WalletRegistration = true;
                utxo[key].WalletRegistrationReward = WalletReward(tx);
            }
            if (!IsOperation(tx.Kind)) return;
            var next = tx.Kind == TransactionKind.InitialLaunchCreate ? new InitialLaunchStatus {
                Created = true, FundingAssetId = tx.Outputs[1].AssetId, RootTransactionId = tx.Id,
                ProviderPublicKey = ProviderPublicKey
            } : State(utxo).Copy();
            next.ReserveTransactionId = tx.Id;
            if (tx.Kind == TransactionKind.InitialLaunchPurchase)
            {
                next.Purchases++;
                string key = RegistrationPrefix + Crypto.Sha256(tx.InitialLaunchIdentity.PublicKey);
                var prior = utxo[key];
                var marker = Marker(key);
                marker.WalletRegistration = true;
                marker.WalletRegistrationReward = prior.WalletRegistrationReward;
                marker.InitialLaunchPurchased = true;
                utxo[key] = marker;
            }
            var stateMarker = Marker(StateKey); stateMarker.InitialLaunchState = next; utxo[StateKey] = stateMarker;
        }
        private static UnspentOutput Marker(string key) => new UnspentOutput { TransactionId = key, OutputIndex = -1,
            Output = new TransactionOutput { Amount = 0, OneTimeAddress = "@protocol-state" } };
    }

    public static partial class TokenExchange
    {
        public static UnsignedTokenExchange PrepareInitialLaunchPurchase(Blockchain chain, IEnumerable<Transaction> pending,
            IEnumerable<string> publicKeys, UnspentOutput reserveA, UnspentOutput reserveB, string providerKey,
            string identityPublicKey, string address, long fee)
        {
            if (reserveA == null || reserveB == null || !reserveA.InitialLaunchCollecting || !reserveB.InitialLaunchCollecting ||
                reserveA.TransactionId != reserveB.TransactionId || reserveA.OutputIndex != 0 || reserveB.OutputIndex != 1 ||
                reserveA.Output.OneTimeAddress != Crypto.Sha256(providerKey) || reserveB.Output.OneTimeAddress != Crypto.Sha256(providerKey) ||
                reserveA.Output.Amount <= InitialLaunch.PrincipalSupply)
                throw new InvalidOperationException("The initial launch reserves are unavailable.");
            if (fee >= InitialLaunch.PurchaseAmount) throw new ArgumentOutOfRangeException(nameof(fee));
            var queue = pending.ToArray(); var keys = Keys(publicKeys, address, fee);
            if (!keys.ContainsKey(Crypto.Sha256(identityPublicKey))) throw new InvalidOperationException("Use this wallet's original registered identity.");
            var selected = new List<UnspentOutput>();
            var outputs = new List<TransactionOutput> { Output(null, reserveA.Output.Amount - InitialLaunch.PurchaseAmount, reserveA.Output.OneTimeAddress),
                Output(reserveB.Output.AssetId, checked(reserveB.Output.Amount + InitialLaunch.FundingUnit), reserveB.Output.OneTimeAddress),
                new TransactionOutput { Amount = InitialLaunch.PurchaseAmount - fee, OneTimeAddress = Crypto.Sha256(identityPublicKey), InitialLaunchLocked = true } };
            Fund(chain, queue, keys, new Dictionary<string, long> { { reserveB.Output.AssetId, InitialLaunch.FundingUnit } }, address, 0, selected, outputs,
                new HashSet<string>(new[] { Key(reserveA), Key(reserveB) }));
            int count = selected.Count; selected.Add(reserveA); selected.Add(reserveB); keys[Crypto.Sha256(providerKey)] = providerKey;
            var draft = Build(selected, keys, outputs, count, fee, TransactionKind.InitialLaunchPurchase,
                participation: outputs[0].Amount == InitialLaunch.PrincipalSupply ? new PoolParticipation { LockedShares = InitialLaunch.PrincipalSupply } : null);
            draft.SetInitialLaunchIdentity(identityPublicKey);
            return draft;
        }
    }
}
