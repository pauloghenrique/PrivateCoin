using PrivateCoin.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PrivateCoin.Desktop
{
    internal static class TokenWalletOperations
    {
        public static IReadOnlyList<TokenBalance> GetBalances(Blockchain chain, Wallet wallet, bool onlyOwned = true)
        {
            return GetBalances(chain, wallet, new Transaction[0], onlyOwned);
        }

        public static IReadOnlyList<TokenBalance> GetBalances(Blockchain chain, Wallet wallet, IEnumerable<Transaction> pending, bool onlyOwned = true)
        {
            Transaction[] queue = pending.ToArray();
            return chain.GetTokenBalances(wallet?.OwnedOneTimeAddresses, queue)
                .Where(token => !onlyOwned || GetAvailableBalance(chain, wallet, queue, token.Id) > 0).ToArray();
        }

        public static long GetAvailableBalance(Blockchain chain, Wallet wallet, IEnumerable<Transaction> pending, string tokenId)
        {
            return chain.GetSpendableTokenOutputs(wallet?.OwnedOneTimeAddresses, pending, tokenId)
                .Aggregate(0L, (total, output) => checked(total + output.Output.Amount));
        }

        public static TokenBalance ValidateTransfer(Blockchain chain, Wallet wallet, IEnumerable<Transaction> pending,
            string tokenId, string destination, long amount, long fee)
        {
            if (wallet == null) throw new InvalidOperationException("Selecione uma carteira.");
            Transaction[] queue = pending.ToArray();
            TokenBalance token = GetBalances(chain, wallet, queue, false).SingleOrDefault(item => item.Id == tokenId);
            if (token == null) throw new InvalidOperationException("Selecione um token confirmado com saldo nesta carteira.");
            if (destination == null || destination.Length != 64 || destination.Any(c =>
                !(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')))
                throw new InvalidOperationException("Informe um endereço de destino válido, com 64 caracteres hexadecimais minúsculos.");
            if (amount <= 0) throw new InvalidOperationException("Informe uma quantidade de tokens maior que zero.");
            if (fee < Blockchain.TransferFeeStep || fee > Blockchain.MaximumTransferFee)
                throw new InvalidOperationException("Selecione uma taxa POVIX válida.");
            if (amount > GetAvailableBalance(chain, wallet, queue, tokenId))
                throw new InvalidOperationException("Saldo disponível do token insuficiente. Valores reservados por transações pendentes não podem ser enviados novamente.");
            if (fee > chain.GetSpendableBalance(wallet.OwnedOneTimeAddresses, queue))
                throw new InvalidOperationException("Saldo POVIX disponível insuficiente para pagar a taxa nesta carteira.");
            return token;
        }

        public static Transaction CreateTransfer(Blockchain chain, Wallet wallet, IEnumerable<Transaction> pending,
            string tokenId, string destination, long amount, long fee)
        {
            Transaction[] queue = pending.ToArray();
            ValidateTransfer(chain, wallet, queue, tokenId, destination, amount, fee);
            Transaction transaction = wallet.CreateTokenTransferTransaction(chain, queue, tokenId, destination, amount, fee);
            chain.ValidatePendingTransactions(queue.Concat(new[] { transaction }));
            return transaction;
        }
    }
}
