using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Povix.Dex.Models;
using Povix.Dex.Services;
using PrivateCoin.Core;

internal static partial class PovixDexRegression
{
    private static Dictionary<string, object> ProfileData(object value) =>
        (Dictionary<string, object>)Json.DeserializeObject(Json.Serialize(value));
    private static void RejectProfile(Action action, string code)
    {
        try { action(); throw new Exception("Profile action was accepted: " + code); }
        catch (TokenOperationException error) { Check(error.Code == code, "profile rejects " + code); }
    }
    private static void RunProfiles(string directory, string nodeExecutable, Wallet issuer, Blockchain chain, TokenNetworkService service)
    {
        var creation = chain.Blocks.SelectMany(block => block.Transactions).First(tx => tx.Kind == TransactionKind.TokenCreate);
        var model = new EditTokenViewModel { TokenId = creation.Token.Id, Name = "Perfil | edição 🌙", Symbol = "EDIT", Description = "Descrição <script> & | 🌙", Website = "https://example.com/token" };
        var keys = PublicKeys(issuer);
        using (var outsider = new Wallet())
        {
            outsider.CreateReceiveAddress();
            RejectProfile(() => service.PrepareProfile(model, PublicKeys(outsider), "profile"), "creator_required");
        }
        model.Website = "javascript:alert(1)";
        RejectProfile(() => service.PrepareProfile(model, keys, "profile"), "fields_invalid");
        model.Website = "https://example.com/token";
        var draft = ProfileData(service.PrepareProfile(model, keys, "profile"));
        File.WriteAllText(Path.Combine(directory, "profile-fixture.json"), Json.Serialize(new {
            wallet = EncryptedWallet(issuer), draft, networkId = Blockchain.NetworkId,
            expected = new { tokenId = model.TokenId, creatorAddress = draft["creatorAddress"], revision = 0,
                name = model.Name, symbol = model.Symbol, description = model.Description, website = model.Website, renounced = false }
        }));
        using (var process = Process.Start(new ProcessStartInfo(nodeExecutable) { UseShellExecute = false,
            Arguments = "Tests/PovixDexProfileWalletRegression.js " + directory }))
        { process.WaitForExit(); Check(process.ExitCode == 0, "profile authorization is locally reconstructed and signed by Web Crypto"); }
        var browser = ProfileData(Json.DeserializeObject(File.ReadAllText(Path.Combine(directory, "profile-browser-result.json"))));
        var signature = (string)browser["signature"];
        string id = (string)draft["draftId"];
        RejectProfile(() => service.SubmitProfile(id, signature, "wrong-session"), "draft_session_changed");
        RejectProfile(() => service.SubmitProfile(id, Convert.ToBase64String(new byte[256]), "profile"), "creator_signature_invalid");
        service.SubmitProfile(id, signature, "profile");
        service.SubmitProfile(id, signature, "profile");
        var listed = (object[])Json.DeserializeObject(Json.Serialize(service.GetTokenProfiles(keys)));
        var profile = (Dictionary<string, object>)listed.First(item => (string)((Dictionary<string, object>)item)["tokenId"] == model.TokenId);
        Check((string)profile["name"] == model.Name && (string)profile["chainName"] == creation.Token.Name && (int)profile["revision"] == 1,
            "signed profile persists and idempotent retries preserve original blockchain metadata");
        var stale = ProfileData(service.PrepareProfile(model, keys, "profile"));
        model.Renounce = true;
        var renounce = ProfileData(service.PrepareProfile(model, keys, "profile"));
        string authorization = SignBytes(issuer, (string)renounce["creatorAddress"], Convert.FromBase64String((string)renounce["authorizationPayload"]));
        service.SubmitProfile((string)renounce["draftId"], authorization, "profile");
        string staleSignature = SignBytes(issuer, (string)stale["creatorAddress"], Convert.FromBase64String((string)stale["authorizationPayload"]));
        RejectProfile(() => service.SubmitProfile((string)stale["draftId"], staleSignature, "profile"), "editing_renounced");
        RejectProfile(() => service.PrepareProfile(model, keys, "profile"), "editing_renounced");
        model.Renounce = false;
        RejectProfile(() => service.PrepareProfile(model, keys, "profile"), "editing_renounced");
        RejectProfile(() => service.SubmitProfile(id, signature, "profile"), "editing_renounced");
        var lockedProfiles = (object[])Json.DeserializeObject(Json.Serialize(service.GetTokenProfiles(keys)));
        var lockedProfile = (Dictionary<string, object>)lockedProfiles.First(item => (string)((Dictionary<string, object>)item)["tokenId"] == model.TokenId);
        Check((bool)lockedProfile["renounced"] && (int)lockedProfile["revision"] == 2,
            "creator cannot restore editing by clearing renunciation or replaying a previously accepted signature");
        string saved = File.ReadAllText(Path.Combine(directory, "dex-token-profiles.json"));
        Check(!saved.Contains("<D>") && !saved.Contains("<P>"), "profile persistence contains no private key material");
        string restart = Path.Combine(directory, "profile-restart"); Directory.CreateDirectory(restart);
        File.Copy(Path.Combine(directory, "dex-network.json"), Path.Combine(restart, "dex-network.json"));
        File.WriteAllText(Path.Combine(restart, "dex-token-profiles.json"), saved);
        using (var reopened = OpenOfflineService(restart, Port()))
        {
            var restored = (object[])Json.DeserializeObject(Json.Serialize(reopened.GetTokenProfiles(keys)));
            Check((bool)((Dictionary<string, object>)restored.First(item => (string)((Dictionary<string, object>)item)["tokenId"] == model.TokenId))["renounced"],
                "renunciation survives a service restart with its verified signature");
        }
        File.WriteAllText(Path.Combine(restart, "dex-token-profiles.json"), saved.Replace("EDIT", "FAKE"));
        try { using (var invalid = OpenOfflineService(restart, Port())) { } throw new Exception("Tampered profile accepted"); }
        catch (InvalidOperationException) { Check(true, "tampered persisted profile prevents service startup"); }
    }
}
