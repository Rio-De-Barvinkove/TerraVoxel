using Unity.Collections;
using UnityEngine;

namespace TerraVoxel.Voxel.Core
{
    /// <summary>
    /// Raw voxel buffers for a single chunk.
    /// </summary>
    public struct ChunkData
    {
        public NativeArray<ushort> Materials;
        public NativeArray<float> Density; // optional, for marching cubes / erosion
        public int Size;
        /// <summary>GPU World State slot index when chunk is on GPU; -1 when CPU-only.</summary>
        public int GpuSlot;
        /// <summary>Offset in GPU VoxelMaterialBuffer (elements) when on GPU.</summary>
        public int GpuOffset;

        public bool IsCreated => Materials.IsCreated;
        public bool IsOnGpu => GpuSlot >= 0;

        public void Allocate(int size, Allocator allocator, bool allocateDensity = true)
        {
            GpuSlot = -1;
            GpuOffset = -1;
            if (Materials.IsCreated) Materials.Dispose();
            if (Density.IsCreated) Density.Dispose();
            Size = size;
            int count = size * size * size;
            Materials = new NativeArray<ushort>(count, allocator, NativeArrayOptions.ClearMemory);
            if (allocateDensity)
                Density = new NativeArray<float>(count, allocator, NativeArrayOptions.ClearMemory);
            else
                Density = default;
        }

        public void Dispose()
        {
            if (Materials.IsCreated) Materials.Dispose();
            if (Density.IsCreated) Density.Dispose();
        }

        public int Index(int x, int y, int z) => x + Size * (y + Size * z);

        public bool InBounds(int x, int y, int z)
        {
            return x >= 0 && y >= 0 && z >= 0 && x < Size && y < Size && z < Size;
        }
    }
}


