using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using PrivateCoin.Core;

namespace Povix.Dex.Models
{
    public sealed class TokenTransferViewModel : IValidatableObject
    {
        [Required, RegularExpression("[0-9a-f]{64}")] public string TokenId { get; set; }
        [Required, StringLength(30), RegularExpression("[0-9]+([.,][0-9]+)?")] public string Amount { get; set; }
        [Required, RegularExpression("[0-9a-f]{64}")] public string DestinationAddress { get; set; }
        public int FeePriority { get; set; } = 2;
        [Range(1, Blockchain.MaximumTransferFee)] public long? FeeAtomic { get; set; }
        public TokenNetworkViewModel Network { get; set; } = new TokenNetworkViewModel();
        public IEnumerable<ValidationResult> Validate(ValidationContext context)
        {
            if (FeePriority != 1 && FeePriority != 2 && FeePriority != 4)
                yield return new ValidationResult("Selecione uma prioridade de taxa válida.", new[] { "FeePriority" });
        }
    }

    public sealed class TokenTransferReceiptViewModel
    {
        public string TransactionId { get; set; }
        public TokenDefinition Token { get; set; }
        public long Amount { get; set; }
        public long Fee { get; set; }
        public string DestinationAddress { get; set; }
        public TransactionOutput[] ChangeOutputs { get; set; } = new TransactionOutput[0];
        public string Status { get; set; }
        public int? BlockHeight { get; set; }
        public string BlockHash { get; set; }
        public int Confirmations { get; set; }
        public int PeerCount { get; set; }
    }
}
