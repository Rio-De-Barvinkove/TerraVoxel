using TerraVoxel.Voxel.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace TerraVoxel.Voxel.Meshing
{
    /// <summary>
    /// Greedy mesher running as a Burst job (single-threaded).
    /// </summary>
    public static class GreedyMesher
    {
        public struct NeighborData
        {
            public bool HasNegX;
            public bool HasPosX;
            public bool HasNegY;
            public bool HasPosY;
            public bool HasNegZ;
            public bool HasPosZ;

            public NativeArray<ushort> NegX;
            public NativeArray<ushort> PosX;
            public NativeArray<ushort> NegY;
            public NativeArray<ushort> PosY;
            public NativeArray<ushort> NegZ;
            public NativeArray<ushort> PosZ;
        }

        public struct MaskCell
        {
            public ushort Material;
            public sbyte Normal;

            public bool SameAs(MaskCell other) => Material == other.Material && Normal == other.Normal;
        }

        /// <summary>Face indices: 0=NegX, 1=PosX, 2=NegY, 3=PosY, 4=NegZ, 5=PosZ. Use for edge-only invalidation.</summary>
        public const int FaceMaskAll = 0x3F;
        public static int FaceBit(int faceIndex) => 1 << faceIndex;

        [BurstCompile]
        struct GreedyMesherJob : IJob
        {
            [ReadOnly] public NativeArray<ushort> Materials;
            public int Size;
            public float Scale;
            public NativeList<Vector3> Vertices;
            public NativeList<int> Triangles;
            public NativeList<Vector3> Normals;
            public NativeList<Color32> Colors;
            public byte MaxMaterialIndex;
            public byte FallbackMaterialIndex;
            [ReadOnly] public NeighborData Neighbors;
            public NativeArray<MaskCell> Mask;
            public int FaceMask;
            public bool EnableSkirts;
            public float SkirtOffset;

            public void Execute()
            {
                int size = Size;
                int3 x = int3.zero;
                int3 q = int3.zero;

                for (int d = 0; d < 3; d++)
                {
                    int u = (d + 1) % 3;
                    int v = (d + 2) % 3;
                    q = int3.zero;
                    q[d] = 1;

                    for (x[d] = -1; x[d] < size; )
                    {
                        if (FaceMask != 0 && FaceMask != FaceMaskAll)
                        {
                            if (x[d] != -1 && x[d] != size - 1)
                            {
                                x[d]++;
                                continue;
                            }
                            int faceIndex = (x[d] == -1) ? (d * 2) : (d * 2 + 1);
                            if ((FaceMask & (1 << faceIndex)) == 0)
                            {
                                x[d]++;
                                continue;
                            }
                        }
                        int n = 0;
                        bool isInteriorSlice = (x[d] >= 0 && x[d] < size - 1);
                        if (isInteriorSlice)
                        {
                            int slice = x[d];
                            int stepU = (d == 0) ? size : (d == 1) ? (size * size) : 1;
                            for (x[v] = 0; x[v] < size; x[v]++)
                            {
                                int j = x[v];
                                int rowStart = j * size;
                                int baseA = InteriorIndex(d, slice, 0, j);
                                int baseB = InteriorIndex(d, slice + 1, 0, j);
                                int i = 0;
                                for (; i <= size - 4; i += 4, n += 4)
                                {
                                    ushort a0 = Materials[baseA]; ushort a1 = Materials[baseA + stepU]; ushort a2 = Materials[baseA + 2 * stepU]; ushort a3 = Materials[baseA + 3 * stepU];
                                    ushort b0 = Materials[baseB]; ushort b1 = Materials[baseB + stepU]; ushort b2 = Materials[baseB + 2 * stepU]; ushort b3 = Materials[baseB + 3 * stepU];
                                    Mask[rowStart + i + 0] = MaskCellFromPair(a0, b0);
                                    Mask[rowStart + i + 1] = MaskCellFromPair(a1, b1);
                                    Mask[rowStart + i + 2] = MaskCellFromPair(a2, b2);
                                    Mask[rowStart + i + 3] = MaskCellFromPair(a3, b3);
                                    baseA += 4 * stepU;
                                    baseB += 4 * stepU;
                                }
                                for (; i < size; i++, n++)
                                {
                                    ushort a = Materials[baseA];
                                    ushort b = Materials[baseB];
                                    Mask[rowStart + i] = MaskCellFromPair(a, b);
                                    baseA += stepU;
                                    baseB += stepU;
                                }
                            }
                        }
                        else
                        {
                            for (x[v] = 0; x[v] < size; x[v]++)
                            {
                                for (x[u] = 0; x[u] < size; x[u]++)
                                {
                                    ushort a = (x[d] >= 0) ? GetVoxel(x) : (ushort)0;
                                    ushort b = (x[d] < size - 1) ? GetVoxel(x + q) : (ushort)0;
                                    Mask[n] = MaskCellFromPair(a, b);
                                    n++;
                                }
                            }
                        }

                        int sliceCoord = x[d];
                        x[d]++;

                        n = 0;
                        for (int j = 0; j < size; j++)
                        {
                            for (int i = 0; i < size; )
                            {
                                MaskCell c = Mask[n];
                                if (c.Normal == 0)
                                {
                                    i++;
                                    n++;
                                    continue;
                                }

                                int w = 1;
                                while (i + w < size && Mask[n + w].SameAs(c)) w++;

                                int h = 1;
                                bool done = false;
                                while (j + h < size && !done)
                                {
                                    for (int k = 0; k < w; k++)
                                    {
                                        if (!Mask[n + k + h * size].SameAs(c))
                                        {
                                            done = true;
                                            break;
                                        }
                                    }
                                    if (!done) h++;
                                }

                                x[u] = i;
                                x[v] = j;
                                int3 du = int3.zero;
                                int3 dv = int3.zero;
                                du[u] = w;
                                dv[v] = h;

                                EmitQuad(d, x, du, dv, c.Normal, c.Material, sliceCoord);

                                for (int dy = 0; dy < h; dy++)
                                {
                                    for (int dx = 0; dx < w; dx++)
                                    {
                                        Mask[n + dx + dy * size] = default;
                                    }
                                }

                                i += w;
                                n += w;
                            }
                        }
                    }
                }
            }

            void EmitQuad(int d, int3 x, int3 du, int3 dv, sbyte normalSign, ushort material, int sliceCoord)
            {
                Vector3 p0 = new Vector3(x.x, x.y, x.z) * Scale;
                Vector3 p1 = new Vector3(x.x + du.x, x.y + du.y, x.z + du.z) * Scale;
                Vector3 p2 = new Vector3(x.x + du.x + dv.x, x.y + du.y + dv.y, x.z + du.z + dv.z) * Scale;
                Vector3 p3 = new Vector3(x.x + dv.x, x.y + dv.y, x.z + dv.z) * Scale;

                Vector3 n = d == 0 ? Vector3.right : (d == 1 ? Vector3.up : Vector3.forward);
                if (normalSign < 0) n = -n;

                bool isBoundary = (sliceCoord == -1 || sliceCoord == Size - 1);
                bool hasNeighbor = (sliceCoord == -1 && ((d == 0 && Neighbors.HasNegX) || (d == 1 && Neighbors.HasNegY) || (d == 2 && Neighbors.HasNegZ)))
                    || (sliceCoord == Size - 1 && ((d == 0 && Neighbors.HasPosX) || (d == 1 && Neighbors.HasPosY) || (d == 2 && Neighbors.HasPosZ)));
                if (EnableSkirts && isBoundary && hasNeighbor && SkirtOffset > 0f)
                {
                    float off = SkirtOffset * Scale;
                    p0 += n * off; p1 += n * off; p2 += n * off; p3 += n * off;
                }

                int vertStart = Vertices.Length;
                byte resolved = ResolveMaterialIndex(material);
                Color32 c = new Color32(resolved, 0, 0, 255);
                if (normalSign > 0)
                {
                    Vertices.Add(p0); Vertices.Add(p1); Vertices.Add(p2); Vertices.Add(p3);
                }
                else
                {
                    Vertices.Add(p0); Vertices.Add(p3); Vertices.Add(p2); Vertices.Add(p1);
                }

                Normals.Add(n); Normals.Add(n); Normals.Add(n); Normals.Add(n);
                Colors.Add(c); Colors.Add(c); Colors.Add(c); Colors.Add(c);

                Triangles.Add(vertStart + 0);
                Triangles.Add(vertStart + 1);
                Triangles.Add(vertStart + 2);

                Triangles.Add(vertStart + 0);
                Triangles.Add(vertStart + 2);
                Triangles.Add(vertStart + 3);
            }

            byte ResolveMaterialIndex(ushort material)
            {
                if (material == 0) return 0;
                if (MaxMaterialIndex == 0) return FallbackMaterialIndex;
                if (material > MaxMaterialIndex) return FallbackMaterialIndex;
                return (byte)material;
            }

            ushort GetVoxel(int3 p)
            {
                return GetVoxel(p.x, p.y, p.z);
            }

            ushort GetVoxel(int x, int y, int z)
            {
                if (x >= 0 && y >= 0 && z >= 0 && x < Size && y < Size && z < Size)
                    return Materials[Index(x, y, z)];

                if (x < 0) return SampleNeighbor(Neighbors.NegX, Neighbors.HasNegX, Size - 1, y, z);
                if (x >= Size) return SampleNeighbor(Neighbors.PosX, Neighbors.HasPosX, 0, y, z);
                if (y < 0) return SampleNeighbor(Neighbors.NegY, Neighbors.HasNegY, x, Size - 1, z);
                if (y >= Size) return SampleNeighbor(Neighbors.PosY, Neighbors.HasPosY, x, 0, z);
                if (z < 0) return SampleNeighbor(Neighbors.NegZ, Neighbors.HasNegZ, x, y, Size - 1);
                if (z >= Size) return SampleNeighbor(Neighbors.PosZ, Neighbors.HasPosZ, x, y, 0);
                return 0;
            }

            ushort SampleNeighbor(NativeArray<ushort> data, bool has, int x, int y, int z)
            {
                if (!has || data.Length == 0) return 0;
                int idx = Index(x, y, z);
                return data[idx];
            }

            int Index(int x, int y, int z) => x + Size * (y + Size * z);

            /// <summary>Linear index into Materials for interior voxel at (d, slice, i_u, j_v).</summary>
            int InteriorIndex(int d, int slice, int iU, int jV)
            {
                if (d == 0) return slice + Size * (iU + Size * jV);
                if (d == 1) return jV + Size * (slice + Size * iU);
                return iU + Size * (jV + Size * slice);
            }

            static MaskCell MaskCellFromPair(ushort a, ushort b)
            {
                if ((a != 0) == (b != 0)) return default;
                if (a != 0) return new MaskCell { Material = a, Normal = 1 };
                return new MaskCell { Material = b, Normal = -1 };
            }
        }

        public static JobHandle Schedule(ChunkData data,
            NeighborData neighbors,
            byte maxMaterialIndex,
            byte fallbackMaterialIndex,
            NativeArray<MaskCell> mask,
            NativeArray<ushort> empty,
            ref MeshData meshData,
            float voxelScale = 0f,
            int faceMask = 0,
            bool enableSkirts = false,
            float skirtOffset = 0.001f)
        {
            meshData.Clear();
            if (faceMask == 0) faceMask = FaceMaskAll;

            // All NativeArray fields must be valid when scheduling a job.
            if (!neighbors.HasNegX) neighbors.NegX = empty;
            if (!neighbors.HasPosX) neighbors.PosX = empty;
            if (!neighbors.HasNegY) neighbors.NegY = empty;
            if (!neighbors.HasPosY) neighbors.PosY = empty;
            if (!neighbors.HasNegZ) neighbors.NegZ = empty;
            if (!neighbors.HasPosZ) neighbors.PosZ = empty;

            if (maxMaterialIndex > 0 && fallbackMaterialIndex > maxMaterialIndex)
                fallbackMaterialIndex = maxMaterialIndex;

            var job = new GreedyMesherJob
            {
                Materials = data.Materials,
                Size = data.Size,
                Scale = voxelScale > 0f ? voxelScale : VoxelConstants.VoxelSize,
                Vertices = meshData.Vertices,
                Triangles = meshData.Triangles,
                Normals = meshData.Normals,
                Colors = meshData.Colors,
                MaxMaterialIndex = maxMaterialIndex,
                FallbackMaterialIndex = fallbackMaterialIndex,
                Neighbors = neighbors,
                Mask = mask,
                FaceMask = faceMask,
                EnableSkirts = enableSkirts,
                SkirtOffset = skirtOffset
            };

            return job.Schedule();
        }

        public static void Build(ChunkData data, NeighborData neighbors, byte maxMaterialIndex, byte fallbackMaterialIndex, ref MeshData meshData, float voxelScale = 0f, int faceMask = 0, bool enableSkirts = false, float skirtOffset = 0.001f)
        {
            // All NativeArray fields must be valid when scheduling a job.
            var empty = new NativeArray<ushort>(0, Allocator.TempJob);
            if (!neighbors.HasNegX) neighbors.NegX = empty;
            if (!neighbors.HasPosX) neighbors.PosX = empty;
            if (!neighbors.HasNegY) neighbors.NegY = empty;
            if (!neighbors.HasPosY) neighbors.PosY = empty;
            if (!neighbors.HasNegZ) neighbors.NegZ = empty;
            if (!neighbors.HasPosZ) neighbors.PosZ = empty;

            var mask = new NativeArray<MaskCell>(data.Size * data.Size, Allocator.TempJob);

            if (maxMaterialIndex > 0 && fallbackMaterialIndex > maxMaterialIndex)
                fallbackMaterialIndex = maxMaterialIndex;

            var handle = Schedule(data, neighbors, maxMaterialIndex, fallbackMaterialIndex, mask, empty, ref meshData, voxelScale, faceMask, enableSkirts, skirtOffset);
            handle.Complete();

            mask.Dispose();
            empty.Dispose();
        }
    }
}

