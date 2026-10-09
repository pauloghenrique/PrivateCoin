using System;
using System.IO;
using System.Runtime.Serialization.Json;

namespace PrivateCoin.Core
{
    /// <summary>Atomic storage of public blocks and verified finality evidence.</summary>
    public static class BlockchainStateStore
    {
        public static Blockchain Load(string path, string localNodeId)
        {
            BlockchainSnapshot snapshot;
            using (var input = File.OpenRead(path))
                snapshot = (BlockchainSnapshot)new DataContractJsonSerializer(typeof(BlockchainSnapshot)).ReadObject(input);
            if (snapshot == null || snapshot.Finality == null) throw new InvalidOperationException("Missing stored finality checkpoint.");
            return new Blockchain(snapshot.Blocks, localNodeId, snapshot.Finality);
        }

        public static void Save(string path, Blockchain chain)
        {
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var fileLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                BlockchainSnapshot snapshot = chain.GetSnapshot();
                if (File.Exists(path))
                {
                    Blockchain saved = Load(path, chain.LocalNodeId);
                    if (snapshot.Finality.FinalizedHeight < saved.FinalizedHeight || snapshot.Blocks.Length <= saved.FinalizedHeight ||
                        snapshot.Blocks[saved.FinalizedHeight].Hash != saved.FinalizedHash)
                        throw new InvalidOperationException("A stored finalized checkpoint cannot regress or change history.");
                }
                string temporary = path + "." + Crypto.NewId() + ".tmp";
                try
                {
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        new DataContractJsonSerializer(typeof(BlockchainSnapshot)).WriteObject(output, snapshot);
                        output.Flush(true);
                    }
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
    }
}
