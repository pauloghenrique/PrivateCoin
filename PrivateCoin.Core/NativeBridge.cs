using System;
using System.Globalization;
using System.Linq;

namespace PrivateCoin.Core
{
    /// <summary>One-time governance binding for protocol pUSDT; external deposit verification is federated.</summary>
    public static class NativeBridge
    {
        public static bool IsConfiguration(Transaction tx) => tx?.Kind == TransactionKind.TokenManage &&
            tx.TokenCommand?.TokenId == InitialLaunch.PusdtTokenId && tx.TokenCommand.Operation == "bridge-configure";
        public static bool IsFeeFree(Transaction tx) => tx?.Kind == TransactionKind.TokenManage && tx.Fee == 0 &&
            tx.TokenCommand?.TokenId == InitialLaunch.PusdtTokenId &&
            (tx.TokenCommand.Operation == "burn" ? tx.Inputs.Count > 0 : tx.Inputs.Count == 0 &&
                (tx.TokenCommand.Operation == "mint" || tx.TokenCommand.Operation == "pause" || tx.TokenCommand.Operation == "resume" || tx.TokenCommand.Operation == "bridge-configure"));
        public static string Binding(long chainId, string asset, string vault)
        {
            if ((chainId != 97 && chainId != 56 && chainId != 31337) || !BscAddress(asset) || !BscAddress(vault))
                throw new ArgumentException("Invalid BNB bridge binding.");
            return chainId.ToString(CultureInfo.InvariantCulture) + ":" + asset + ":" + vault;
        }
        private static bool BscAddress(string value) => value != null && value.Length == 42 && value.StartsWith("0x", StringComparison.Ordinal) &&
            value.Skip(2).All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f') && value.Skip(2).Any(c => c != '0');
        internal static void ValidatePolicy(TokenPolicy policy)
        {
            if (policy == null) throw new InvalidOperationException("Missing pUSDT bridge policy.");
            policy.Validate(0);
            if (!policy.Mintable || !policy.Burnable || !policy.Pausable || policy.MaximumSupply <= 0 || policy.HasAutomaticBurn ||
                policy.VestingTranches != 0 || policy.Administrators.Count < 3 || policy.RequiredSignatures <= policy.Administrators.Count / 2)
                throw new InvalidOperationException("pUSDT requires a majority of at least three administrators and no transfer burn or vesting.");
            var parts = (policy.BridgeBinding ?? "").Split(':'); long chainId;
            if (parts.Length != 3 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out chainId) || Binding(chainId, parts[1], parts[2]) != policy.BridgeBinding)
                throw new InvalidOperationException("pUSDT must be bound to a BNB asset and vault.");
        }
    }
}
