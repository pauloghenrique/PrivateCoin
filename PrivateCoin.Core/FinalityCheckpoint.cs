using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace PrivateCoin.Core
{
    [DataContract]
    public sealed class CheckpointValidator
    {
        [DataMember(Order = 1)] public string ValidatorId { get; private set; }
        [DataMember(Order = 2)] public string PublicKey { get; private set; }
        [DataMember(Order = 3)] public string RewardAddress { get; private set; }
        // String encoding preserves exact atomic amounts in JSON consumers.
        [DataMember(Order = 4)] public string LockedAmountAtomic { get; private set; }
        internal CheckpointValidator(ValidatorStake stake)
        {
            ValidatorId = stake.ValidatorId; PublicKey = stake.PublicKey;
            RewardAddress = stake.RewardAddress;
            LockedAmountAtomic = stake.LockedAmount.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>A public migration candidate, not a quorum certificate or evidence of agreement.</summary>
    [DataContract]
    public sealed class FinalityCheckpoint
    {
        public const string CandidateFormat = "privatecoin-checkpoint-candidate-v1";
        [DataMember(Order = 1)] public string Format { get; private set; }
        [DataMember(Order = 2)] public string NetworkId { get; private set; }
        [DataMember(Order = 3)] public string GenesisHash { get; private set; }
        [DataMember(Order = 4)] public int ConsensusVersion { get; private set; }
        [DataMember(Order = 5)] public int Height { get; private set; }
        [DataMember(Order = 6)] public string BlockHash { get; private set; }
        [DataMember(Order = 7)] public string PolicyId { get; private set; }
        [DataMember(Order = 8)] private List<CheckpointValidator> Validators { get; set; }
        public IReadOnlyList<CheckpointValidator> Committee => Validators.ToArray();
        private FinalityCheckpoint() { }

        public static FinalityCheckpoint Create(Blockchain chain, int? selectedHeight = null)
        {
            if (chain == null) throw new ArgumentNullException(nameof(chain));
            Block[] snapshot = FinalityPolicy.CopyBlocks(chain.Blocks);
            new Blockchain(snapshot, chain.Finality); // Validate the complete source, not only the selected prefix.
            int height = selectedHeight ?? (chain.Finality?.AnchorHeight ?? snapshot.Length - 1);
            if (height < 1 || height >= snapshot.Length)
                throw new InvalidOperationException("Selecione um bloco existente após o gênese.");
            if (chain.Finality != null && height != chain.Finality.AnchorHeight)
                throw new InvalidOperationException("A política ativa já possui um checkpoint fixo; exporte essa mesma referência.");
            var prefix = new Blockchain(snapshot.Take(height + 1), chain.Finality);
            ValidatorStake[] committee = FinalityPolicy.Committee(prefix.Blocks);
            var policy = new FinalityPolicy(height, prefix.Blocks.Last().Hash);
            return new FinalityCheckpoint
            {
                Format = CandidateFormat, NetworkId = Blockchain.NetworkId,
                GenesisHash = Blockchain.GenesisHash, ConsensusVersion = Blockchain.ConsensusVersion,
                Height = height, BlockHash = policy.AnchorHash, PolicyId = policy.Id,
                Validators = committee.OrderBy(v => v.ValidatorId, StringComparer.Ordinal).Select(v => new CheckpointValidator(v)).ToList()
            };
        }

        public FinalityPolicy ToPolicy()
        {
            ValidateStructure();
            return new FinalityPolicy(Height, BlockHash);
        }

        public void ValidateAgainst(Blockchain local)
        {
            ValidateStructure();
            FinalityCheckpoint expected = Create(local, Height);
            if (BlockHash != expected.BlockHash || PolicyId != expected.PolicyId ||
                Validators.Count != expected.Validators.Count)
                throw new InvalidOperationException("O checkpoint não corresponde à cadeia local; sincronize e confira a referência com os validadores.");
            CheckpointValidator[] supplied = Validators.OrderBy(v => v.ValidatorId, StringComparer.Ordinal).ToArray();
            for (int i = 0; i < supplied.Length; i++)
            {
                CheckpointValidator registered = expected.Validators[i];
                if (supplied[i].ValidatorId != registered.ValidatorId || supplied[i].PublicKey != registered.PublicKey ||
                    supplied[i].RewardAddress != registered.RewardAddress || supplied[i].LockedAmountAtomic != registered.LockedAmountAtomic)
                    throw new InvalidOperationException("O conjunto de validadores do checkpoint não corresponde aos stakes registrados nesse bloco.");
            }
        }

        private void ValidateStructure()
        {
            if (Format != CandidateFormat || NetworkId != Blockchain.NetworkId || GenesisHash != Blockchain.GenesisHash ||
                ConsensusVersion != Blockchain.ConsensusVersion || Height < 1 || !FinalityPolicy.IsHash(BlockHash) ||
                Validators == null || Validators.Count < 2 || Validators.Count > 4096 || Validators.Any(v => v == null))
                throw new InvalidOperationException("Formato, rede ou dados de checkpoint inválidos.");
            var policy = new FinalityPolicy(Height, BlockHash);
            if (PolicyId != policy.Id) throw new InvalidOperationException("A identidade da política não corresponde ao checkpoint.");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (CheckpointValidator validator in Validators)
            {
                long amount;
                if (string.IsNullOrWhiteSpace(validator.PublicKey) || string.IsNullOrWhiteSpace(validator.RewardAddress) ||
                    validator.ValidatorId != Crypto.Sha256(validator.PublicKey) ||
                    !long.TryParse(validator.LockedAmountAtomic, NumberStyles.None, CultureInfo.InvariantCulture, out amount) || amount <= 0 ||
                    validator.LockedAmountAtomic != amount.ToString(CultureInfo.InvariantCulture) ||
                    !keys.Add(FinalityPolicy.KeyIdentity(validator.PublicKey)))
                    throw new InvalidOperationException("Validador duplicado, identidade ou stake inválido no checkpoint.");
            }
        }

        public byte[] ToJson()
        {
            ValidateStructure();
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(FinalityCheckpoint)).WriteObject(stream, this);
                byte[] json = stream.ToArray();
                if (json.Length > 1024 * 1024) throw new InvalidDataException("Checkpoint excede o limite de 1 MiB.");
                return json;
            }
        }

        public static FinalityCheckpoint FromJson(byte[] json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            if (json.Length > 1024 * 1024) throw new InvalidDataException("Checkpoint excede o limite de 1 MiB.");
            using (var stream = new MemoryStream(json))
            {
                var checkpoint = (FinalityCheckpoint)new DataContractJsonSerializer(typeof(FinalityCheckpoint)).ReadObject(stream);
                if (checkpoint == null) throw new InvalidDataException("Checkpoint vazio.");
                checkpoint.ValidateStructure();
                return checkpoint;
            }
        }

        public string ConfigurationSnippet()
        {
            ValidateStructure();
            return "<add key=\"FinalityAnchorHeight\" value=\"" + Height.ToString(CultureInfo.InvariantCulture) + "\" />\n" +
                "<add key=\"FinalityAnchorHash\" value=\"" + BlockHash + "\" />\n" +
                "<add key=\"RequireFinality\" value=\"true\" />";
        }
    }
}
