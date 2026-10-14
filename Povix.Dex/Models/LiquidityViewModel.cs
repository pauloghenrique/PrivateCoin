using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using PrivateCoin.Core;

namespace Povix.Dex.Models
{
    public class ExchangeFeeModel : IValidatableObject
    {
        public int FeePriority { get; set; } = 2;
        [Range(1, Blockchain.MaximumTransferFee)] public long? FeeAtomic { get; set; }
        public IEnumerable<ValidationResult> Validate(ValidationContext context)
        {
            if (FeePriority != 1 && FeePriority != 2 && FeePriority != 4)
                yield return new ValidationResult("Selecione uma prioridade válida.", new[] { "FeePriority" });
        }
    }
    public sealed class CreatePoolViewModel : ExchangeFeeModel
    {
        [Required, RegularExpression("(povix|[0-9a-f]{64})")] public string AssetA { get; set; }
        [Required, RegularExpression("(povix|[0-9a-f]{64})")] public string AssetB { get; set; }
        [Required, StringLength(30), RegularExpression("[0-9]+([.,][0-9]+)?")] public string AmountA { get; set; }
        [Required, StringLength(30), RegularExpression("[0-9]+([.,][0-9]+)?")] public string AmountB { get; set; }
    }
    public sealed class SwapViewModel : ExchangeFeeModel
    {
        [Required, RegularExpression("[0-9a-f]{64}")] public string PoolId { get; set; }
        [Range(0, 1)] public int InputSide { get; set; }
        [Required, RegularExpression("(povix|[0-9a-f]{64})")] public string InputAssetId { get; set; }
        [Required, RegularExpression("(povix|[0-9a-f]{64})")] public string OutputAssetId { get; set; }
        [Required, StringLength(30), RegularExpression("[0-9]+([.,][0-9]+)?")] public string Amount { get; set; }
        [Required, StringLength(30), RegularExpression("[0-9]+([.,][0-9]+)?")] public string MinimumReceive { get; set; }
    }
    public sealed class ExchangeReceiptViewModel
    {
        public string TransactionId { get; set; }
        public string PoolId { get; set; }
        public string Operation { get; set; }
        public string Status { get; set; }
        public bool PoolAvailable { get; set; }
        public string PoolStatusMessage { get; set; }
        public long Fee { get; set; }
        public int? BlockHeight { get; set; }
        public string BlockHash { get; set; }
        public int ValidationCount { get; set; }
        public int Confirmations { get; set; }
        public int PeerCount { get; set; }
        public ExchangeOutputViewModel[] Outputs { get; set; }
    }
    public sealed class ExchangeOutputViewModel
    {
        public long Amount { get; set; }
        public string Symbol { get; set; }
        public int Decimals { get; set; }
        public string Address { get; set; }
        public string Purpose { get; set; }
    }
}
