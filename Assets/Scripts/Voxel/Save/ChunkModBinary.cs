using System;
using System.IO;
using TerraVoxel.Voxel.Core;

namespace TerraVoxel.Voxel.Save
{
    public static class ChunkModBinary
    {
        public const uint Magic = 0x5456584D; // "TVXM"
        public const ushort Version = 3;

        [Flags]
        public enum ModFlags : ushort
        {
            None = 0,
            Compressed = 1 << 0,
            CompressionLz4 = 1 << 1,
            Materials16 = 1 << 2
        }

        public struct ModEntry
        {
            public int Index;
            public ushort Material;
        }

        public struct Payload
        {
            public ChunkCoord Coord;
            public int ChunkSize;
            public ModEntry[] Entries;
            public ChunkMeta Meta;
        }

        public static byte[] Serialize(Payload payload, bool compress)
        {
            int entryCount = payload.Entries != null ? payload.Entries.Length : 0;
            int entrySize = 6;
            int rawLength = entryCount * entrySize;

            var body = new byte[rawLength];
            using (var ms = new MemoryStream(body))
            using (var bw = new BinaryWriter(ms))
            {
                for (int i = 0; i < entryCount; i++)
                {
                    bw.Write(payload.Entries[i].Index);
                    bw.Write(payload.Entries[i].Material);
                }
            }

            uint crc32 = Crc32.Compute(body);
            byte[] bodyOut = compress ? Lz4Codec.Compress(body) : body;

            ChunkMeta meta = payload.Meta;
            meta.SaveMode = ChunkSaveMode.DeltaBacked;
            meta.DeltaCount = entryCount;

            ModFlags flags = ModFlags.None;
            if (compress) flags |= ModFlags.Compressed | ModFlags.CompressionLz4;
            flags |= ModFlags.Materials16;

            using var outStream = new MemoryStream(64 + bodyOut.Length);
            using var writer = new BinaryWriter(outStream);
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write((ushort)flags);
            writer.Write(payload.ChunkSize);
            writer.Write(payload.Coord.X);
            writer.Write(payload.Coord.Y);
            writer.Write(payload.Coord.Z);
            writer.Write((byte)meta.SaveMode);
            writer.Write(PackMetaFlags(meta));
            writer.Write(meta.GeneratorVersion);
            writer.Write(meta.LastSimTick);
            writer.Write(meta.DeltaCount);
            writer.Write(entryCount);
            writer.Write(rawLength);
            writer.Write(bodyOut.Length);
            writer.Write(crc32);
            writer.Write(bodyOut);
            return outStream.ToArray();
        }

        public static bool TryDeserialize(byte[] bytes, out Payload payload)
        {
            payload = default;
            if (bytes == null || bytes.Length < 32) return false;

            using var ms = new MemoryStream(bytes);
            using var br = new BinaryReader(ms);
            if (br.ReadUInt32() != Magic) return false;

            ushort version = br.ReadUInt16();
            if (version != 1 && version != 2 && version != Version) return false;
            int minHeader = version >= 3 ? 54 : (version >= 2 ? 50 : 36);
            if (bytes.Length < minHeader) return false;

            var flags = (ModFlags)br.ReadUInt16();
            int chunkSize = br.ReadInt32();
            int x = br.ReadInt32();
            int y = br.ReadInt32();
            int z = br.ReadInt32();

            ChunkMeta meta = ChunkMeta.Default(ChunkSaveMode.DeltaBacked, 0);
            if (version >= 2)
            {
                meta.SaveMode = (ChunkSaveMode)br.ReadByte();
                byte metaFlags = br.ReadByte();
                meta.GeneratorVersion = br.ReadInt32();
                meta.LastSimTick = br.ReadInt32();
                meta.DeltaCount = br.ReadInt32();
                UnpackMetaFlags(metaFlags, ref meta);
            }
            int entryCount = br.ReadInt32();
            int rawLength = br.ReadInt32();
            int bodyLength = br.ReadInt32();
            uint crc32 = 0;
            bool hasCrc = version >= 3;
            if (hasCrc)
            {
                if (ms.Length - ms.Position < 4) return false;
                crc32 = br.ReadUInt32();
            }

            if (version == 1)
                meta.DeltaCount = entryCount;

            if (entryCount < 0 || rawLength < 0 || bodyLength < 0) return false;
            int entrySize = 5;
            bool materials16 = version >= 3 && (flags & ModFlags.Materials16) != 0;
            if (materials16) entrySize = 6;
            if (rawLength != entryCount * entrySize) return false;
            if (ms.Length - ms.Position < bodyLength) return false;

            byte[] body = br.ReadBytes(bodyLength);
            byte[] uncompressed = body;
            if ((flags & ModFlags.Compressed) != 0)
            {
                if ((flags & ModFlags.CompressionLz4) == 0) return false;
                try
                {
                    uncompressed = Lz4Codec.Decompress(body, rawLength);
                }
                catch
                {
                    return false;
                }
            }

            if (uncompressed.Length != rawLength) return false;
            if (hasCrc)
            {
                uint computed = Crc32.Compute(uncompressed, 0, rawLength);
                if (computed != crc32) return false;
            }

            var entries = new ModEntry[entryCount];
            using (var bodyStream = new MemoryStream(uncompressed))
            using (var bodyReader = new BinaryReader(bodyStream))
            {
                for (int i = 0; i < entryCount; i++)
                {
                    int needed = materials16 ? 6 : 5;
                    if (bodyStream.Position + needed > bodyStream.Length) return false;
                    entries[i] = new ModEntry
                    {
                        Index = bodyReader.ReadInt32(),
                        Material = materials16 ? bodyReader.ReadUInt16() : bodyReader.ReadByte()
                    };
                }
            }

            payload = new Payload
            {
                Coord = new ChunkCoord(x, y, z),
                ChunkSize = chunkSize,
                Entries = entries,
                Meta = meta
            };
            return true;
        }

        static byte PackMetaFlags(ChunkMeta meta)
        {
            byte flags = 0;
            if (meta.HasSimulatedData) flags |= 1 << 0;
            if (meta.IsStructurallyInvalid) flags |= 1 << 1;
            return flags;
        }

        static void UnpackMetaFlags(byte flags, ref ChunkMeta meta)
        {
            meta.HasSimulatedData = (flags & (1 << 0)) != 0;
            meta.IsStructurallyInvalid = (flags & (1 << 1)) != 0;
        }
    }
}

