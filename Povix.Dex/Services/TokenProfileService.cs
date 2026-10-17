using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using Povix.Dex.Models;
using PrivateCoin.Core;

namespace Povix.Dex.Services
{
    public sealed partial class TokenNetworkService
    {
        private Dictionary<string, TokenProfile> profiles = new Dictionary<string, TokenProfile>(StringComparer.Ordinal);
        private readonly Dictionary<string, ProfileDraft> profileDrafts = new Dictionary<string, ProfileDraft>();
        private string ProfilePath => Path.Combine(Path.GetDirectoryName(statePath), "dex-token-profiles.json");

        private bool CreatorRenounced(string tokenId, string providerKey)
        {
            TokenProfile profile;
            if (tokenId == "povix" || !profiles.TryGetValue(tokenId, out profile) || !profile.Renounced)
                return false;
            var creation = FindCreation(tokenId);
            return creation != null && profile.CreationId == creation.Id &&
                profile.CreatorKey == creation.Inputs[0].PublicKey && profile.CreatorKey == providerKey;
        }

        private void LoadProfiles()
        {
            if (!File.Exists(ProfilePath)) return;
            using (var stream = File.OpenRead(ProfilePath))
            {
                var records = (List<TokenProfile>)new DataContractJsonSerializer(typeof(List<TokenProfile>)).ReadObject(stream);
                foreach (var record in records)
                {
                    if (record == null || record.Revision < 1 || record.NetworkId != Blockchain.NetworkId ||
                        !VerifyProfileSignature(record)) throw new InvalidOperationException("O arquivo de perfis de tokens é inválido.");
                    profiles.Add(record.TokenId, record);
                }
            }
        }

        public object GetTokenProfiles(string[] publicKeys)
        {
            lock (sync)
            {
                return blockchain.Blocks.SelectMany(block => block.Transactions)
                    .Where(tx => tx.Kind == TransactionKind.TokenCreate && publicKeys.Contains(tx.Inputs[0].PublicKey, StringComparer.Ordinal))
                    .Select(tx => {
                        TokenProfile profile;
                        profiles.TryGetValue(tx.Token.Id, out profile);
                        // A profile is bound to the original creation and its creator, including after a reorganization.
                        if (profile != null && (profile.CreationId != tx.Id || profile.CreatorKey != tx.Inputs[0].PublicKey)) profile = null;
                        return new { tokenId = tx.Token.Id, name = profile?.Name ?? tx.Token.Name,
                            symbol = profile?.Symbol ?? tx.Token.Symbol, description = profile?.Description ?? tx.Token.Policy?.Description ?? "",
                            website = profile?.Website ?? tx.Token.Policy?.Website ?? "", renounced = profile?.Renounced ?? false,
                            revision = profile?.Revision ?? 0, creatorAddress = AddressFor(tx.Inputs[0].PublicKey),
                            chainName = tx.Token.Name, chainSymbol = tx.Token.Symbol, decimals = tx.Token.Decimals,
                            supplyAtomic = tx.Token.Supply.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                    }).ToArray();
            }
        }

        public object PrepareProfile(EditTokenViewModel model, string[] publicKeys, string owner)
        {
            lock (sync)
            {
                RequireReady();
                var creation = FindCreation(model.TokenId);
                if (creation == null) throw new TokenOperationException("token_unconfirmed", "O token precisa estar confirmado na blockchain.");
                if (!publicKeys.Contains(creation.Inputs[0].PublicKey, StringComparer.Ordinal))
                    throw new TokenOperationException("creator_required", "Somente a carteira do criador pode editar este perfil.");
                TokenProfile previous;
                profiles.TryGetValue(model.TokenId, out previous);
                if (previous != null && (previous.CreationId != creation.Id || previous.CreatorKey != creation.Inputs[0].PublicKey))
                    throw new TokenOperationException("creation_changed", "A criação mudou na cadeia. O perfil anterior não pode ser reutilizado.");
                if (previous?.Renounced == true) throw new TokenOperationException("editing_renounced", "A permissão de edição deste perfil foi renunciada.");
                string name = model.Name?.Trim(), symbol = model.Symbol;
                string website = model.Website ?? "", description = model.Description ?? "";
                Uri uri;
                if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || symbol == null ||
                    !System.Text.RegularExpressions.Regex.IsMatch(symbol, @"\A[A-Z]{1,10}\z") || description.Length > 1000 || website.Length > 300 ||
                    (website.Length > 0 && (!Uri.TryCreate(website, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host))))
                    throw new TokenOperationException("fields_invalid", "Confira nome, símbolo, descrição e site HTTPS.");
                foreach (var id in profileDrafts.Where(item => item.Value.ExpiresUtc <= DateTime.UtcNow).Select(item => item.Key).ToArray()) profileDrafts.Remove(id);
                if (profileDrafts.Count >= 200 || profileDrafts.Values.Count(item => item.Owner == owner) >= 5)
                    throw new TokenOperationException("draft_limit", "Há preparações em andamento. Aguarde e tente novamente.");
                var profile = new TokenProfile { TokenId = model.TokenId, CreationId = creation.Id,
                    CreatorKey = creation.Inputs[0].PublicKey, NetworkId = Blockchain.NetworkId,
                    Revision = checked((previous?.Revision ?? 0) + 1), DraftId = Guid.NewGuid().ToString("N"),
                    Name = name, Symbol = symbol, Description = description, Website = website, Renounced = model.Renounce };
                profileDrafts.Add(profile.DraftId, new ProfileDraft { Profile = profile, Owner = owner, ExpiresUtc = DateTime.UtcNow.AddMinutes(15) });
                return new { draftId = profile.DraftId, tokenId = profile.TokenId, creationId = profile.CreationId,
                    creatorAddress = AddressFor(profile.CreatorKey), networkId = profile.NetworkId, revision = profile.Revision,
                    name = profile.Name, symbol = profile.Symbol, description = profile.Description, website = profile.Website,
                    renounced = profile.Renounced, authorizationPayload = Convert.ToBase64String(ProfilePayload(profile)) };
            }
        }

        public void SubmitProfile(string draftId, string signature, string owner)
        {
            lock (sync)
            {
                ProfileDraft draft;
                if (draftId == null || !profileDrafts.TryGetValue(draftId, out draft)) throw new TokenOperationException("draft_missing", "Prepare a edição novamente.");
                if (draft.Owner != owner) throw new TokenOperationException("draft_session_changed", "A sessão mudou. Prepare novamente.");
                if (draft.ExpiresUtc <= DateTime.UtcNow) throw new TokenOperationException("draft_expired", "A preparação expirou. Prepare novamente.");
                var profile = draft.Profile;
                // Authenticate retries too, before returning an already persisted result.
                var signed = profile.CopyWithSignature(signature);
                if (!VerifyProfileSignature(signed)) throw new TokenOperationException("creator_signature_invalid", "A assinatura não autoriza esta edição.");
                TokenProfile previous;
                profiles.TryGetValue(profile.TokenId, out previous);
                if (previous?.DraftId == draftId) return;
                RequireReady();
                var creation = FindCreation(profile.TokenId);
                if (creation == null || creation.Id != profile.CreationId || creation.Inputs[0].PublicKey != profile.CreatorKey)
                    throw new TokenOperationException("creation_changed", "A criação mudou na cadeia. Prepare novamente.");
                if (previous?.Renounced == true) throw new TokenOperationException("editing_renounced", "A edição deste perfil foi renunciada.");
                if ((previous?.Revision ?? 0) != profile.Revision - 1) throw new TokenOperationException("profile_changed", "O perfil mudou. Atualize e prepare novamente.");
                var next = new Dictionary<string, TokenProfile>(profiles, StringComparer.Ordinal) { [profile.TokenId] = signed };
                using (var stream = new MemoryStream())
                {
                    new DataContractJsonSerializer(typeof(List<TokenProfile>)).WriteObject(stream, next.Values.ToList());
                    AtomicFile.Write(ProfilePath, stream.ToArray());
                }
                profiles = next;
            }
        }

        private static string ProfileField(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        private static byte[] ProfilePayload(TokenProfile profile) => Encoding.UTF8.GetBytes(string.Join("|", new[] {
            "povix-dex-token-profile-v1", profile.NetworkId, profile.DraftId, profile.TokenId, profile.CreationId,
            AddressFor(profile.CreatorKey), profile.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            profile.Renounced ? "1" : "0", ProfileField(profile.Name), ProfileField(profile.Symbol), ProfileField(profile.Description), ProfileField(profile.Website) }));
        private static bool VerifyProfileSignature(TokenProfile profile)
        {
            try
            {
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.PersistKeyInCsp = false; rsa.FromXmlString(profile.CreatorKey);
                    return rsa.VerifyData(ProfilePayload(profile), CryptoConfig.MapNameToOID("SHA256"), Convert.FromBase64String(profile.Signature ?? ""));
                }
            }
            catch (Exception error) when (IsInvalidData(error)) { return false; }
        }
        private sealed class ProfileDraft
        {
            public TokenProfile Profile { get; set; }
            public string Owner { get; set; }
            public DateTime ExpiresUtc { get; set; }
        }
        [DataContract]
        private sealed class TokenProfile
        {
            [DataMember] public string TokenId { get; set; }
            [DataMember] public string CreationId { get; set; }
            [DataMember] public string CreatorKey { get; set; }
            [DataMember] public string NetworkId { get; set; }
            [DataMember] public string DraftId { get; set; }
            [DataMember] public int Revision { get; set; }
            [DataMember] public string Name { get; set; }
            [DataMember] public string Symbol { get; set; }
            [DataMember] public string Description { get; set; }
            [DataMember] public string Website { get; set; }
            [DataMember] public bool Renounced { get; set; }
            [DataMember] public string Signature { get; set; }
            public TokenProfile CopyWithSignature(string signature)
            { var copy = (TokenProfile)MemberwiseClone(); copy.Signature = signature; return copy; }
        }
    }
}
