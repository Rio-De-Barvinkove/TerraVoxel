using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using Unity.Burst;

namespace TerraVoxel.Voxel.Svo
{
    /// <summary>
    /// Optional neighbor data for seamless SVO at chunk boundaries.
    /// </summary>
    public struct SvoNeighborData
    {
        public NativeArray<ushort>? XMin, XMax, YMin, YMax, ZMin, ZMax;
        public int NeighborSize;
    }

    public static class SvoBuilder
    {
        struct BuildState
        {
            public int X0, Y0, Z0, Size, NodeIndex;
        }

        /// <summary>Builds SVO from chunk data. Caller must call volume.Dispose() when done to avoid leaks.</summary>
        public static SvoVolume Build(ChunkData data, int leafSize, in SvoNeighborData? neighborData = null)
        {
            int size = data.Size;
            if (leafSize < 1) leafSize = 1;
            if (leafSize > size) leafSize = size;

            var volume = new SvoVolume(size, leafSize, Allocator.TempJob);
            volume.Nodes.Add(default);

            var queue = new Queue<BuildState>();
            queue.Enqueue(new BuildState { X0 = 0, Y0 = 0, Z0 = 0, Size = size, NodeIndex = 0 });

            bool hasDensity = data.Density.IsCreated && data.Density.Length == data.Materials.Length;

            while (queue.Count > 0)
            {
                var state = queue.Dequeue();
                int x0 = state.X0, y0 = state.Y0, z0 = state.Z0, sizeCur = state.Size, nodeIndex = state.NodeIndex;

                if (sizeCur <= leafSize)
                {
                    SampleRegionMaterialAndDensity(data, x0, y0, z0, sizeCur, hasDensity, out byte material, out byte density);
                    volume.Nodes[nodeIndex] = new SvoVolume.Node
                    {
                        ChildMask = 0,
                        Material = material,
                        Density = density,
                        FirstChild = -1
                    };
                    continue;
                }

                if (IsUniformRegion(data, x0, y0, z0, sizeCur, out byte uniformMat, out byte uniformDens))
                {
                    volume.Nodes[nodeIndex] = new SvoVolume.Node
                    {
                        ChildMask = 0,
                        Material = uniformMat,
                        Density = uniformDens,
                        FirstChild = -1
                    };
                    continue;
                }

                int childSize = sizeCur / 2;
                int firstChildIndex = volume.Nodes.Length;
                for (int i = 0; i < 8; i++)
                    volume.Nodes.Add(default);

                byte mask = 0;
                for (int i = 0; i < 8; i++)
                {
                    int ox = (i & 1) != 0 ? childSize : 0;
                    int oy = (i & 2) != 0 ? childSize : 0;
                    int oz = (i & 4) != 0 ? childSize : 0;
                    int nx = x0 + ox, ny = y0 + oy, nz = z0 + oz;
                    bool hasContent = RegionHasContent(data, nx, ny, nz, childSize, neighborData);
                    if (hasContent)
                        mask |= (byte)(1 << i);
                    queue.Enqueue(new BuildState { X0 = nx, Y0 = ny, Z0 = nz, Size = childSize, NodeIndex = firstChildIndex + i });
                }

                volume.Nodes[nodeIndex] = new SvoVolume.Node
                {
                    ChildMask = mask,
                    Material = 0,
                    Density = 0,
                    FirstChild = firstChildIndex
                };
            }

            return volume;
        }

        static bool RegionHasContent(ChunkData data, int x0, int y0, int z0, int size, in SvoNeighborData? neighborData)
        {
            for (int z = z0; z < z0 + size; z++)
            for (int y = y0; y < y0 + size; y++)
            for (int x = x0; x < x0 + size; x++)
            {
                if (!data.InBounds(x, y, z))
                {
                    if (neighborData.HasValue && SampleNeighbor(neighborData.Value, x, y, z, data.Size) != 0)
                        return true;
                    continue;
                }
                if (data.Materials[data.Index(x, y, z)] != 0)
                    return true;
            }
            return false;
        }

        /// <summary>Samples neighbor chunk data. Expects XMin/XMax length >= size^3; YMin/YMax/ZMin/ZMax length >= size^2. Returns 0 if out of bounds.</summary>
        static ushort SampleNeighbor(in SvoNeighborData nb, int x, int y, int z, int size)
        {
            if (size <= 0) return 0;
            if (x < 0 && nb.XMin.HasValue)
            {
                int nx = x + size, ny = Mathf.Clamp(y, 0, size - 1), nz = Mathf.Clamp(z, 0, size - 1);
                int idx = nx + ny * size + nz * size * size;
                if (idx >= 0 && idx < nb.XMin.Value.Length) return nb.XMin.Value[idx];
            }
            if (x >= size && nb.XMax.HasValue)
            {
                int nx = x - size, ny = Mathf.Clamp(y, 0, size - 1), nz = Mathf.Clamp(z, 0, size - 1);
                int idx = nx + ny * size + nz * size * size;
                if (idx >= 0 && idx < nb.XMax.Value.Length) return nb.XMax.Value[idx];
            }
            if (y < 0 && nb.YMin.HasValue)
            {
                int ix = Mathf.Clamp(x, 0, size - 1), iz = Mathf.Clamp(z, 0, size - 1);
                int idx = ix + iz * size;
                if (idx >= 0 && idx < nb.YMin.Value.Length) return nb.YMin.Value[idx];
            }
            if (y >= size && nb.YMax.HasValue)
            {
                int ix = Mathf.Clamp(x, 0, size - 1), iz = Mathf.Clamp(z, 0, size - 1);
                int idx = ix + iz * size;
                if (idx >= 0 && idx < nb.YMax.Value.Length) return nb.YMax.Value[idx];
            }
            if (z < 0 && nb.ZMin.HasValue)
            {
                int ix = Mathf.Clamp(x, 0, size - 1), iy = Mathf.Clamp(y, 0, size - 1);
                int idx = ix + iy * size;
                if (idx >= 0 && idx < nb.ZMin.Value.Length) return nb.ZMin.Value[idx];
            }
            if (z >= size && nb.ZMax.HasValue)
            {
                int ix = Mathf.Clamp(x, 0, size - 1), iy = Mathf.Clamp(y, 0, size - 1);
                int idx = ix + iy * size;
                if (idx >= 0 && idx < nb.ZMax.Value.Length) return nb.ZMax.Value[idx];
            }
            return 0;
        }

        /// <summary>O(size^3). May be slow for large regions.</summary>
        static bool IsUniformRegion(ChunkData data, int x0, int y0, int z0, int size, out byte material, out byte density)
        {
            material = 0;
            density = 0;
            bool hasFirst = false;
            ushort firstMat = 0;
            float firstDens = 0f;
            bool hasDensity = data.Density.IsCreated && data.Density.Length == data.Materials.Length;

            for (int z = z0; z < z0 + size; z++)
            {
                int zBase = data.Size * data.Size * z;
                for (int y = y0; y < y0 + size; y++)
                {
                    int yBase = data.Size * y + zBase;
                    for (int x = x0; x < x0 + size; x++)
                    {
                        int idx = x + yBase;
                        ushort v = data.Materials[idx];
                        if (!hasFirst)
                        {
                            firstMat = v;
                            firstDens = hasDensity ? data.Density[idx] : 0f;
                            hasFirst = true;
                            continue;
                        }
                        if (v != firstMat)
                            return false;
                    }
                }
            }

            if (!hasFirst) return true;
            material = (byte)Mathf.Clamp(firstMat, 0, 255);
            density = hasDensity ? (byte)Mathf.Clamp(firstDens * 255f, 0, 255) : (byte)255;
            return true;
        }

        /// <summary>Modal (most frequent non-zero) material in region; density = average if present. O(size^3).</summary>
        static void SampleRegionMaterialAndDensity(ChunkData data, int x0, int y0, int z0, int size, bool useDensity, out byte material, out byte density)
        {
            int[] counts = new int[256];
            float densSum = 0f;
            int densCount = 0;

            for (int z = z0; z < z0 + size; z++)
            {
                int zBase = data.Size * data.Size * z;
                for (int y = y0; y < y0 + size; y++)
                {
                    int yBase = data.Size * y + zBase;
                    for (int x = x0; x < x0 + size; x++)
                    {
                        int idx = x + yBase;
                        ushort v = data.Materials[idx];
                        if (v != 0 && v < 256)
                            counts[v]++;
                        if (useDensity && data.Density.IsCreated && idx < data.Density.Length)
                        {
                            densSum += data.Density[idx];
                            densCount++;
                        }
                    }
                }
            }

            int modal = 0;
            int maxCount = 0;
            for (int i = 1; i < 256; i++)
            {
                if (counts[i] > maxCount) { maxCount = counts[i]; modal = i; }
            }
            material = (byte)modal;
            density = densCount > 0 ? (byte)Mathf.Clamp((densSum / densCount) * 255f, 0, 255) : (byte)255;
        }
    }
}
