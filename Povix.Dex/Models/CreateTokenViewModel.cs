using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using PrivateCoin.Core;

namespace Povix.Dex.Models
{
    public sealed class CreateTokenViewModel : IValidatableObject
    {
        [Required(ErrorMessage = "Informe o nome do token.")]
        [StringLength(64, ErrorMessage = "Use até 64 caracteres.")]
        [Display(Name = "Nome do token")]
        public string Name { get; set; }

        [Required(ErrorMessage = "Informe o símbolo.")]
        [RegularExpression("^[A-Z]{1,10}$", ErrorMessage = "Use de 1 a 10 letras maiúsculas, de A a Z.")]
        [Display(Name = "Símbolo")]
        public string Symbol { get; set; }

        [Range(0, 8, ErrorMessage = "Escolha entre 0 e 8 casas decimais.")]
        [Display(Name = "Casas decimais")]
        public int Decimals { get; set; } = 8;

        [Required(ErrorMessage = "Informe a quantidade total.")]
        [StringLength(30)]
        [Display(Name = "Quantidade total")]
        public string Supply { get; set; }

        [Required(ErrorMessage = "Informe o endereço que receberá os tokens.")]
        [RegularExpression("^[0-9a-f]{64}$", ErrorMessage = "Informe um endereço POVIX de 64 caracteres hexadecimais minúsculos.")]
        [Display(Name = "Endereço de recebimento")]
        public string DestinationAddress { get; set; }

        public int FeePriority { get; set; } = 2;
        [Range(1, Blockchain.MaximumTransferFee)] public long? FeeAtomic { get; set; }
        public TokenNetworkViewModel Network { get; set; }

        public IEnumerable<ValidationResult> Validate(ValidationContext context)
        {
            long amount;
            if (!TryParseSupply(Supply, Decimals, out amount))
                yield return new ValidationResult("Informe uma quantidade positiva, sem separador de milhar, compatível com as casas decimais e o limite do token.", new[] { "Supply" });
            if (Name != null && Name.Any(char.IsControl))
                yield return new ValidationResult("O nome não pode conter caracteres de controle.", new[] { "Name" });
            if (FeePriority != 1 && FeePriority != 2 && FeePriority != 4)
                yield return new ValidationResult("Escolha uma opção de taxa válida.", new[] { "FeePriority" });
        }

        // Convert directly to atomic units: no floating point or rounding of supply.
        public static bool TryParseSupply(string value, int decimals, out long amount)
        {
            amount = 0;
            if (decimals < 0 || decimals > 8 || value == null || value.Length > 30 ||
                !Regex.IsMatch(value, @"\A[0-9]+([.,][0-9]+)?\z")) return false;
            string[] parts = value.Replace(',', '.').Split('.');
            string fraction = parts.Length == 2 ? parts[1] : string.Empty;
            if (fraction.Length > decimals) return false;
            return long.TryParse(parts[0] + fraction.PadRight(decimals, '0'), NumberStyles.None,
                CultureInfo.InvariantCulture, out amount) && amount > 0;
        }
    }

    public sealed class TokenNetworkViewModel
    {
        public int Height { get; set; }
        public int PeerCount { get; set; }
        public bool Synchronized { get; set; }
        public long[] Fees { get; set; } = new long[3];
        public string Error { get; set; }
        public bool CanCreate => PeerCount > 0 && Synchronized && string.IsNullOrEmpty(Error);
        public string NetworkId => Blockchain.NetworkId;
        public int ConsensusVersion => Blockchain.ConsensusVersion;
        public static string FormatPovix(long amount) => (amount / (decimal)Blockchain.OneCoin).ToString("0.00000000", CultureInfo.GetCultureInfo("pt-BR"));
        public static string FormatToken(long amount, int decimals)
        {
            decimal divisor = 1;
            for (int i = 0; i < decimals; i++) divisor *= 10;
            return (amount / divisor).ToString("N" + decimals, CultureInfo.GetCultureInfo("pt-BR"));
        }
    }

    public sealed class TokenRegistrationViewModel
    {
        public string TransactionId { get; set; }
        public TokenDefinition Token { get; set; }
        public string DestinationAddress { get; set; }
        public long Fee { get; set; }
        public TransactionOutput[] PovixOutputs { get; set; } = new TransactionOutput[0];
        public string Status { get; set; }
        public string WaitingReason { get; set; }
        public int? BlockHeight { get; set; }
        public string BlockHash { get; set; }
        public int Confirmations { get; set; }
        public int ValidationCount { get; set; }
        public int PeerCount { get; set; }
        public string SupplyDisplay => (Token.Supply / Pow10(Token.Decimals)).ToString("N" + Token.Decimals, CultureInfo.GetCultureInfo("pt-BR"));
        private static decimal Pow10(int decimals) { decimal value = 1; for (int i = 0; i < decimals; i++) value *= 10; return value; }
    }
}
