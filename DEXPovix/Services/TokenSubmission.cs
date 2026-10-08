using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using DEXPovix.Models;
using PrivateCoin.Core;

namespace DEXPovix.Services
{
    public static class TokenSubmission
    {
        public static Transaction Parse(CreateTokenViewModel model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.SignedTransaction) || model.SignedTransaction.Length > 131072)
                throw new InvalidOperationException("Informe uma transação assinada válida.");
            Transaction tx;
            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(model.SignedTransaction)))
                    tx = (Transaction)new DataContractJsonSerializer(typeof(Transaction)).ReadObject(stream);
            }
            catch (Exception ex) when (ex is System.Runtime.Serialization.SerializationException || ex is System.Xml.XmlException || ex is ArgumentException)
            { throw new InvalidOperationException("O JSON da transação é inválido."); }
            if (tx == null || tx.Kind != TransactionKind.TokenCreate || tx.Token == null ||
                tx.Inputs == null || tx.Inputs.Count == 0 || tx.Inputs.Any(i => i == null || string.IsNullOrEmpty(i.PublicKey) || string.IsNullOrEmpty(i.Signature) || string.IsNullOrEmpty(i.TransactionId)) ||
                tx.Outputs == null || tx.Outputs.Count == 0 || tx.Outputs.Any(o => o == null))
                throw new InvalidOperationException("Envie uma transação de criação de token assinada pela carteira.");
            if (tx.Token.Name != model.Name || tx.Token.Symbol != model.Symbol ||
                tx.Token.Decimals != model.Decimals || tx.Token.Supply != model.Supply ||
                tx.Outputs.Count(o => o.AssetId == tx.Token.Id) != 1 ||
                !tx.Outputs.Any(o => o.AssetId == tx.Token.Id && o.Amount == model.Supply && o.OneTimeAddress == model.Destination))
                throw new InvalidOperationException("Os dados do cadastro não correspondem à transação assinada.");
            return tx;
        }
    }
}
