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
            if (size <= 0)
            {
                UnityEngine.Debug.LogError("[ChunkData] Allocate: size must be > 0 (got " + size + "). Skipping allocation.");
                return;
            }
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

        /// <summary>Call after Allocate or load; logs warning if Size != expectedSize (e.g. worldGen.ChunkSize).</summary>
        public void ValidateSize(int expectedSize)
        {
            if (Size != expectedSize)
                UnityEngine.Debug.LogWarning("[ChunkData] Size mismatch: Size=" + Size + " expected=" + expectedSize + ". Index/InBounds may be wrong.");
        }

        public void Dispose()
        {
            if (Materials.IsCreated) Materials.Dispose();
            if (Density.IsCreated) Density.Dispose();
        }

        public int Index(int x, int y, int z)
        {
            if (!InBounds(x, y, z))
            {
#if UNITY_EDITOR
                UnityEngine.Debug.LogWarning("[ChunkData] Index out of bounds: (" + x + "," + y + "," + z + ") Size=" + Size);
#endif
                return 0;
            }
            return x + Size * (y + Size * z);
        }

        public bool InBounds(int x, int y, int z)
        {
            return x >= 0 && y >= 0 && z >= 0 && x < Size && y < Size && z < Size;
        }
    }
}


