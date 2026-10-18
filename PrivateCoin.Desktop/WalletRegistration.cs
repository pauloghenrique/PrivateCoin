using PrivateCoin.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PrivateCoin.Desktop
{
    /// <summary>Registration and presentation rules shared by both Desktop wallet entry points.</summary>
    internal static class WalletRegistration
    {
        internal static bool IsRegistration(Transaction tx) => tx.Kind == TransactionKind.WalletCreate || tx.Kind == TransactionKind.InitialLaunchCreate;
        private static TransactionOutput Reward(Transaction tx) => tx.Outputs[tx.Kind == TransactionKind.InitialLaunchCreate ? 4 : 0];
        internal static bool HasRegistration(Blockchain chain, Wallet wallet, IEnumerable<Transaction> pending)
        {
            if (wallet == null) return false;
            var addresses = new HashSet<string>(wallet.OwnedOneTimeAddresses, StringComparer.Ordinal);
            return chain.Blocks.SelectMany(block => block.Transactions).Concat(pending)
                .Any(tx => IsRegistration(tx) && addresses.Contains(Reward(tx).OneTimeAddress));
        }
        internal static Transaction Prepare(Blockchain chain, Wallet wallet, IEnumerable<Transaction> pending)
        {
            if (wallet == null) throw new InvalidOperationException("Selecione uma carteira para registrar.");
            var queue = pending.ToArray();
            if (HasRegistration(chain, wallet, queue)) throw new InvalidOperationException("Esta carteira já tem um registro confirmado ou aguardando confirmação.");
            string address = wallet.OwnedOneTimeAddresses.FirstOrDefault() ?? wallet.CreateReceiveAddress();
            return chain.CreateWalletCreationTransaction(address, queue);
        }
        internal static string Describe(Transaction tx)
        {
            if (!IsRegistration(tx)) throw new ArgumentException("Operação não é um registro de carteira.");
            long reward = Reward(tx).Amount;
            if (tx.Kind == TransactionKind.InitialLaunchCreate)
                return "A primeira carteira receberá " + Coins(reward) + " POVIX após a confirmação. A mesma operação cria a pool com " +
                    Coins(InitialLaunch.ReserveSupply) + " POVIX e credita 1.000.000 de USDC local e 1.000.000 de USDT local nesta carteira.";
            return reward == 0 ? "Registro sem recompensa inicial: os 600 POVIX são exclusivos da primeira carteira confirmada." :
                "Recompensa de " + Coins(reward) + " POVIX aguardando confirmação em bloco.";
        }
        internal static string LaunchStatus(Blockchain chain, IEnumerable<Transaction> pending)
        {
            var state = chain.GetInitialLaunchStatus();
            if (!state.Created) return pending.Any(tx => tx.Kind == TransactionKind.InitialLaunchCreate) ?
                "600 POVIX e criação da pool aguardando confirmação." : "Primeira carteira: 600 POVIX e criação automática da pool.";
            if (state.Active) return "Pool aberta: negociação liberada; liquidez principal permanente.";
            return "Pool criada · " + state.Purchases.ToString("N0", CultureInfo.CurrentCulture) + "/" + InitialLaunch.MaximumPurchases.ToString("N0", CultureInfo.CurrentCulture) +
                " compras\nReserva de lançamento: " + Coins(state.Remaining) + " POVIX";
        }
        private static string Coins(long amount) => ((decimal)amount / Blockchain.OneCoin).ToString("N0", CultureInfo.CurrentCulture);
    }
}
