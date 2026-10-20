using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PrivateCoin.Core
{
    public static class TokenManagement
    {
        public static UnsignedTokenManagement Prepare(Blockchain chain, IEnumerable<Transaction> pending,
            IEnumerable<string> publicKeys, string tokenId, string operation, long amount, string destination,
            string changeAddress, long fee, IEnumerable<string> excludedInputs = null, TokenPolicy bridgePolicy = null, string bridgeDepositId = null, string bridgeBlockHash = null)
        {
            if (chain == null || pending == null || publicKeys == null) throw new ArgumentNullException();
            if ((fee < Blockchain.TransferFeeStep && !(NativeBridge.IsAssetId(tokenId) && fee == 0)) || fee > Blockchain.MaximumTransferFee || !TokenPolicy.Address(changeAddress))
                throw new ArgumentException("Invalid fee or change address.");
            var state = chain.GetTokenRuntime(tokenId);
            if (state?.Token.Policy == null) throw new InvalidOperationException("Token has no management policy.");
            var rules = state.Token.Policy;
            if (operation != "mint" && operation != "burn" && operation != "pause" && operation != "resume" && operation != "bridge-configure") throw new ArgumentException("Unknown operation.");
            if ((operation == "mint" || operation == "burn") ? amount <= 0 : amount != 0) throw new ArgumentException("Invalid amount.");
            if (operation == "bridge-configure") {
                if (tokenId != InitialLaunch.PusdtTokenId || state.Revision != 0 || state.Supply != 0 || state.Issued != 0 || rules.Mintable || fee != 0)
                    throw new InvalidOperationException("Bridge configuration is permitted once, before issuance.");
                NativeBridge.ValidatePolicy(bridgePolicy);
            } else if (bridgePolicy != null) throw new ArgumentException("Unexpected bridge policy.");
            if (operation == "mint" && (!rules.Mintable || state.Paused || amount > rules.MaximumSupply - state.Issued || !TokenPolicy.Address(destination)))
                throw new InvalidOperationException("Mint not allowed by token policy.");
            if (operation == "burn" && (!rules.Burnable || state.Paused || amount > state.Supply)) throw new InvalidOperationException("Burn not allowed by token policy.");
            if ((operation == "pause" || operation == "resume") && (!rules.Pausable || state.Paused == (operation == "pause"))) throw new InvalidOperationException("Invalid pause transition.");
            var queue = pending.ToArray();
            if (queue.Any(candidate => candidate.TokenCommand?.TokenId == tokenId)) throw new InvalidOperationException("A token operation is already pending.");
            var keys = publicKeys.Distinct(StringComparer.Ordinal).ToDictionary(Crypto.Sha256, value => value, StringComparer.Ordinal);
            if (!keys.ContainsKey(changeAddress)) throw new ArgumentException("Change must belong to signing wallet.");
            var admins = operation == "burn" ? new string[0] : keys.Keys.Where(address => rules.Administrators.Contains(address)).OrderBy(address => address, StringComparer.Ordinal).Take(rules.RequiredSignatures).ToArray();
            if (operation != "burn" && admins.Length != rules.RequiredSignatures) throw new InvalidOperationException("Connect the required administrator keys to co-sign this operation.");
            var excluded = new HashSet<string>(excludedInputs ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            Func<UnspentOutput, bool> available = output => !excluded.Contains(output.TransactionId + ":" + output.OutputIndex);
            var funding = fee == 0 ? new List<UnspentOutput>() : Select(chain.GetSpendableOutputs(keys.Keys, queue).Where(available), fee);
            var selected = new List<UnspentOutput>(funding);
            var tx = new Transaction { Kind = TransactionKind.TokenManage, TimestampUtcTicks = DateTime.UtcNow.Ticks, Fee = fee,
                TokenCommand = new TokenCommand { TokenId = tokenId, Operation = operation, Amount = amount, Revision = state.Revision, BridgePolicy = bridgePolicy?.Copy(), BridgeDepositId = bridgeDepositId, BridgeBlockHash = bridgeBlockHash },
                TokenAuthorizations = admins.Select(address => new TokenAuthorization { PublicKey = keys[address] }).ToList() };
            if (operation == "mint") tx.Outputs.Add(new TransactionOutput { AssetId = tokenId, Amount = amount, OneTimeAddress = destination });
            if (operation == "burn")
            {
                var tokens = Select(chain.GetSpendableTokenOutputs(keys.Keys, queue, tokenId).Where(available), amount);
                selected.AddRange(tokens);
                long change = checked(tokens.Sum(output => output.Output.Amount) - amount);
                if (change > 0) tx.Outputs.Add(new TransactionOutput { AssetId = tokenId, Amount = change, OneTimeAddress = changeAddress });
            }
            long nativeChange = checked(funding.Sum(output => output.Output.Amount) - fee);
            if (nativeChange > 0) tx.Outputs.Add(new TransactionOutput { Amount = nativeChange, OneTimeAddress = changeAddress });
            tx.Inputs.AddRange(selected.Select(output => new TransactionInput { TransactionId = output.TransactionId,
                OutputIndex = output.OutputIndex, PublicKey = keys[output.Output.OneTimeAddress] }));
            return new UnsignedTokenManagement(tx, selected.Select(output => output.Output.OneTimeAddress).ToArray(), admins);
        }
        private static List<UnspentOutput> Select(IEnumerable<UnspentOutput> available, long amount)
        {
            var selected = new List<UnspentOutput>(); long total = 0;
            foreach (var output in available) { selected.Add(output); total = checked(total + output.Output.Amount); if (total >= amount) break; }
            if (total < amount) throw new InvalidOperationException("Insufficient confirmed unlocked funds.");
            return selected;
        }
    }

    public sealed class UnsignedTokenManagement
    {
        private readonly Transaction transaction;
        public IReadOnlyList<string> InputAddresses { get; }
        public IReadOnlyList<string> AdministratorAddresses { get; }
        public byte[] SigningPayload => Encoding.UTF8.GetBytes(transaction.SigningPayload());
        public Transaction Preview => Copy();
        internal UnsignedTokenManagement(Transaction tx, string[] inputs, string[] administrators)
        { transaction = tx; InputAddresses = Array.AsReadOnly(inputs); AdministratorAddresses = Array.AsReadOnly(administrators); }
        private Transaction Copy() => new Transaction { Kind = transaction.Kind, Fee = transaction.Fee, TimestampUtcTicks = transaction.TimestampUtcTicks,
            TokenCommand = new TokenCommand { TokenId = transaction.TokenCommand.TokenId, Operation = transaction.TokenCommand.Operation,
                Amount = transaction.TokenCommand.Amount, Revision = transaction.TokenCommand.Revision, BridgePolicy = transaction.TokenCommand.BridgePolicy?.Copy(),
                BridgeDepositId = transaction.TokenCommand.BridgeDepositId, BridgeBlockHash = transaction.TokenCommand.BridgeBlockHash },
            Inputs = transaction.Inputs.Select(input => new TransactionInput { TransactionId = input.TransactionId, OutputIndex = input.OutputIndex, PublicKey = input.PublicKey }).ToList(),
            Outputs = transaction.Outputs.Select(output => new TransactionOutput { Amount = output.Amount, AssetId = output.AssetId, OneTimeAddress = output.OneTimeAddress }).ToList(),
            TokenAuthorizations = transaction.TokenAuthorizations.Select(proof => new TokenAuthorization { PublicKey = proof.PublicKey }).ToList() };
        public Transaction Complete(IEnumerable<string> signatures, IEnumerable<string> administratorSignatures)
        {
            var inputs = signatures?.ToArray(); var admins = administratorSignatures?.ToArray();
            if (inputs == null || inputs.Length != InputAddresses.Count || inputs.Any(string.IsNullOrWhiteSpace) ||
                admins == null || admins.Length != AdministratorAddresses.Count || admins.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Missing signatures.");
            var tx = Copy();
            for (int i = 0; i < inputs.Length; i++) tx.Inputs[i].Signature = inputs[i];
            for (int i = 0; i < admins.Length; i++) tx.TokenAuthorizations[i].Signature = admins[i];
            tx.Id = tx.CalculateId(); return tx;
        }
    }
}
