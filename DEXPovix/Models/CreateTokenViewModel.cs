using System.ComponentModel.DataAnnotations;

namespace DEXPovix.Models
{
    public sealed class CreateTokenViewModel
    {
        [Required, StringLength(64), Display(Name = "Nome do token")]
        public string Name { get; set; }
        [Required, RegularExpression("^[A-Z]{1,10}$"), Display(Name = "Símbolo (A–Z)")]
        public string Symbol { get; set; }
        [Range(0, 8), Display(Name = "Casas decimais")]
        public int Decimals { get; set; }
        [Range(typeof(long), "1", "9223372036854775807"), Display(Name = "Oferta em unidades atômicas")]
        public long Supply { get; set; }
        [Required, RegularExpression("^[0-9a-f]{64}$"), Display(Name = "Endereço destinatário")]
        public string Destination { get; set; }
        [System.Web.Mvc.AllowHtml, Required, StringLength(131072), Display(Name = "Transação assinada (JSON)")]
        public string SignedTransaction { get; set; }
    }
}
