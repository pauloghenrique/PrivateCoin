using System.ComponentModel.DataAnnotations;

namespace Povix.Dex.Models
{
    public sealed class EditTokenViewModel
    {
        [Required, RegularExpression("[0-9a-f]{64}")] public string TokenId { get; set; }
        [Required, StringLength(64)] public string Name { get; set; }
        [Required, RegularExpression("[A-Z]{1,10}")] public string Symbol { get; set; }
        [StringLength(1000)] public string Description { get; set; }
        [StringLength(300)] public string Website { get; set; }
        public bool Renounce { get; set; }
    }
}
