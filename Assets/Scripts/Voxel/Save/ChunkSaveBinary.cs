using System;
using System.IO;
using System.IO.Compression;
using TerraVoxel.Voxel.Core;

namespace TerraVoxel.Voxel.Save
{
    public static class ChunkSaveBinary
    {
        public const uint Magic = 0x54565843; // "TVXC"
        public const ushort Version = 4;

        [Flags]
        public enum ChunkFlags : ushort
        {
            None = 0,
            Compressed = 1 << 0,
            HasDensity = 1 << 1,
            CompressionLz4 = 1 << 2,
            Materials16 = 1 << 3
        }

        public struct Payload
        {
            public ChunkCoord Coord;
            public int ChunkSize;
            public ushort[] Materials;
            public byte[] DensityBytes;
            public ChunkMeta Meta;
        }

        public static byte[] Serialize(Payload payload, bool compress, CompressionLevel level)
        {
            if (payload.Materials == null)
                throw new ArgumentNullException(nameof(payload.Materials));

            int materialsCount = payload.Materials.Length;
            int materialsLength = materialsCount * sizeof(ushort);
            int densityLength = payload.DensityBytes != null ? payload.DensityBytes.Length : 0;
            int bodyUncompressedLength = materialsLength + densityLength;

            var body = new byte[bodyUncompressedLength];
            Buffer.BlockCopy(payload.Materials, 0, body, 0, materialsLength);
            if (densityLength > 0)
                Buffer.BlockCopy(payload.DensityBytes, 0, body, materialsLength, densityLength);

            uint crc32 = Crc32.Compute(body);
            byte[] bodyOut = compress ? Lz4Codec.Compress(body) : body;

            ChunkMeta meta = payload.Meta;
            meta.SaveMode = ChunkSaveMode.SnapshotBacked;
            if (meta.DeltaCount < 0) meta.DeltaCount = 0;

            ChunkFlags flags = ChunkFlags.None;
            if (compress) flags |= ChunkFlags.Compressed | ChunkFlags.CompressionLz4;
            if (densityLength > 0) flags |= ChunkFlags.HasDensity;
            flags |= ChunkFlags.Materials16;

            using var ms = new MemoryStream(64 + bodyOut.Length);
            using var bw = new BinaryWriter(ms);
            bw.Write(Magic);
            bw.Write(Version);
            bw.Write((ushort)flags);
            bw.Write(payload.ChunkSize);
            bw.Write(payload.Coord.X);
            bw.Write(payload.Coord.Y);
            bw.Write(payload.Coord.Z);
            bw.Write((byte)meta.SaveMode);
            bw.Write(PackMetaFlags(meta));
            bw.Write(meta.GeneratorVersion);
            bw.Write(meta.LastSimTick);
            bw.Write(meta.DeltaCount);
            bw.Write(materialsLength);
            bw.Write(densityLength);
            bw.Write(bodyOut.Length);
            bw.Write(crc32);
            bw.Write(bodyOut);
            return ms.ToArray();
        }

        public static bool TryDeserialize(byte[] bytes, out Payload payload)
        {
            payload = default;
            if (bytes == null || bytes.Length < 32) return false;

            using var ms = new MemoryStream(bytes);
            using var br = new BinaryReader(ms);
            if (br.ReadUInt32() != Magic) return false;

            ushort version = br.ReadUInt16();
            if (version != 1 && version != 2 && version != 3 && version != Version) return false;
            int minHeader = version >= 4 ? 54 : (version >= 3 ? 50 : 36);
            if (bytes.Length < minHeader) return false;

            var flags = (ChunkFlags)br.ReadUInt16();
            int chunkSize = br.ReadInt32();
            int x = br.ReadInt32();
            int y = br.ReadInt32();
            int z = br.ReadInt32();

            ChunkMeta meta = ChunkMeta.Default(ChunkSaveMode.SnapshotBacked, 0);
            if (version >= 3)
            {
                meta.SaveMode = (ChunkSaveMode)br.ReadByte();
                byte metaFlags = br.ReadByte();
                meta.GeneratorVersion = br.ReadInt32();
                meta.LastSimTick = br.ReadInt32();
                meta.DeltaCount = br.ReadInt32();
                UnpackMetaFlags(metaFlags, ref meta);
            }

            int materialsLength = br.ReadInt32();
            int densityLength = br.ReadInt32();
            int bodyLength = br.ReadInt32();
            uint crc32 = 0;
            bool hasCrc = version >= 4;
            if (hasCrc)
            {
                if (ms.Length - ms.Position < 4) return false;
                crc32 = br.ReadUInt32();
            }

            if (materialsLength < 0 || densityLength < 0 || bodyLength < 0) return false;
            if (ms.Length - ms.Position < bodyLength) return false;

            byte[] body = br.ReadBytes(bodyLength);
            byte[] uncompressed = body;
            if ((flags & ChunkFlags.Compressed) != 0)
            {
                int expected = materialsLength + densityLength;
                bool useLz4 = (flags & ChunkFlags.CompressionLz4) != 0;
                try
                {
                    uncompressed = useLz4 ? Lz4Codec.Decompress(body, expected) : DecompressGzip(body, expected);
                }
                catch
                {
                    return false;
                }
            }

            if (uncompressed.Length < materialsLength + densityLength) return false;
            bool materials16 = (flags & ChunkFlags.Materials16) != 0;
            if (materials16 && (materialsLength % 2) != 0) return false;
            if (hasCrc)
            {
                uint computed = Crc32.Compute(uncompressed, 0, materialsLength + densityLength);
                if (computed != crc32) return false;
            }

            ushort[] materials;
            if (materials16)
            {
                int materialsCount = materialsLength / sizeof(ushort);
                materials = new ushort[materialsCount];
                Buffer.BlockCopy(uncompressed, 0, materials, 0, materialsLength);
            }
            else
            {
                materials = new ushort[materialsLength];
                for (int i = 0; i < materialsLength; i++)
                    materials[i] = uncompressed[i];
            }

            byte[] densityBytes = null;
            if (densityLength > 0)
            {
                densityBytes = new byte[densityLength];
                Buffer.BlockCopy(uncompressed, materialsLength, densityBytes, 0, densityLength);
            }

            payload = new Payload
            {
                Coord = new ChunkCoord(x, y, z),
                ChunkSize = chunkSize,
                Materials = materials,
                DensityBytes = densityBytes,
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

        static byte[] DecompressGzip(byte[] data, int expectedSize)
        {
            using var input = new MemoryStream(data);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = expectedSize > 0 ? new MemoryStream(expectedSize) : new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }
    }
}

