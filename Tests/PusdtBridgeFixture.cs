using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PrivateCoin.Core;

// Test-only quorum attestations; real deposits are exercised by Povix.Bridge/test.
internal static class PusdtBridgeFixture
{
    private static void Operation(Blockchain chain, Wallet[] wallets, string operation, long amount, string destination, TokenPolicy policy = null)
    {
        var keys = new Dictionary<string, string>(); var publicKeys = new List<string>();
        foreach (var wallet in wallets) foreach (string xml in wallet.ExportPrivateKeys()) using (var rsa = new RSACryptoServiceProvider()) {
            rsa.PersistKeyInCsp = false; rsa.FromXmlString(xml); string pub = rsa.ToXmlString(false);
            using (var sha = SHA256.Create()) keys[string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(pub)).Select(b => b.ToString("x2")))] = xml;
            publicKeys.Add(pub);
        }
        var draft = TokenManagement.Prepare(chain, new Transaction[0], publicKeys, InitialLaunch.PusdtTokenId,
            operation, amount, destination, wallets[0].OwnedOneTimeAddresses.First(), 0, null, policy);
        Func<string, string> sign = address => { using (var rsa = new RSACryptoServiceProvider()) { rsa.PersistKeyInCsp = false; rsa.FromXmlString(keys[address]); return Convert.ToBase64String(rsa.SignData(draft.SigningPayload, "SHA256")); } };
        chain.AddBlock(new[] { draft.Complete(draft.InputAddresses.Select(sign), draft.AdministratorAddresses.Select(sign)) });
    }
    internal static void Seed(Blockchain chain, params Wallet[] wallets)
    {
        var policy = new TokenPolicy { Mintable = true, Burnable = true, Pausable = true, MaximumSupply = 1000000000000L,
            Administrators = wallets.Select(wallet => wallet.OwnedOneTimeAddresses.First()).ToList(), RequiredSignatures = 2,
            BridgeBinding = NativeBridge.Binding(97, "0x" + new string('1', 40), "0x" + new string('2', 40)) };
        Operation(chain, wallets, "bridge-configure", 0, null, policy);
        foreach (var wallet in wallets.Skip(1)) Mint(chain, wallets, wallet.OwnedOneTimeAddresses.First(), 2 * InitialLaunch.FundingUnit);
        if (chain.GetTokens().Count != 1 || chain.GetTokenRuntime(InitialLaunch.PusdtTokenId).Supply != 4 * InitialLaunch.FundingUnit || Blockchain.GetRewardedBlockCount(chain.Blocks) != 0)
            throw new Exception("Invalid bridged test fixture supply or native reward index.");
    }
    internal static void Mint(Blockchain chain, Wallet[] administrators, string destination, long amount) => Operation(chain, administrators, "mint", amount, destination);
}
