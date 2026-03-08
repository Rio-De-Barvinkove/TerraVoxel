/*
using System.Runtime.InteropServices;
using TerraVoxel.Voxel.Core;
using UnityEngine;

namespace TerraVoxel.Voxel.GPU
{
    /// <summary>
    /// Per-chunk metadata for GPU World State. Layout must match HLSL CullChunkDesc in ChunkCulling.compute (32 bytes: coord, slotGeneration, voxelOffset, meshOffset, vertexCount, flags).
    /// Used in ComputeBuffer with stride 32.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 32)]
    public struct GpuChunkDescriptor
    {
        /// <summary>Chunk coordinate (X, Y, Z).</summary>
        public int CoordX;
        public int CoordY;
        public int CoordZ;
        /// <summary>Slot generation for use-after-free check. Incremented when slot is freed.</summary>
        public uint SlotGeneration;
        /// <summary>Offset in VoxelMaterialBuffer (elements).</summary>
        public uint VoxelOffset;
        /// <summary>Offset in MeshVertexBuffer (vertices). 0xFFFFFFFF if not meshed. At ~4B vertices overflow possible.</summary>
        public uint MeshOffset;
        /// <summary>Vertex count for this chunk's mesh.</summary>
        public uint VertexCount;
        /// <summary>Chunk flags: empty, solid, mixed, visible, dirty. See ChunkDescriptorFlags.</summary>
        public uint Flags;

        public const uint MeshOffsetNone = 0xFFFFFFFF;

        public static int StrideBytes => 32;

        public ChunkCoord Coord
        {
            get => new ChunkCoord(CoordX, CoordY, CoordZ);
            set
            {
                CoordX = value.X;
                CoordY = value.Y;
                CoordZ = value.Z;
            }
        }

        public bool IsMeshed => MeshOffset != MeshOffsetNone && VertexCount > 0;
    }

    /// <summary>Flags for GpuChunkDescriptor.Flags. Must match HLSL CHUNK_FLAG_*.</summary>
    public static class ChunkDescriptorFlags
    {
        public const uint Empty = 1 << 0;
        public const uint Solid = 1 << 1;
        public const uint Mixed = 1 << 2;
        public const uint Visible = 1 << 3;
        public const uint Dirty = 1 << 4;
    }
}
*/

using System.Runtime.InteropServices;
using TerraVoxel.Voxel.Core;
using UnityEngine;

namespace TerraVoxel.Voxel.GPU
{
    [StructLayout(LayoutKind.Sequential, Size = 32)]
    public struct GpuChunkDescriptor
    {
        public int CoordX, CoordY, CoordZ;
        public uint SlotGeneration, VoxelOffset, MeshOffset, VertexCount, Flags;
        public const uint MeshOffsetNone = 0xFFFFFFFF;
        public static int StrideBytes => 32;
        public ChunkCoord Coord { get => new ChunkCoord(CoordX, CoordY, CoordZ); set { CoordX = value.X; CoordY = value.Y; CoordZ = value.Z; } }
        public bool IsMeshed => MeshOffset != MeshOffsetNone && VertexCount > 0;
    }

    public static class ChunkDescriptorFlags
    {
        public const uint Empty = 1 << 0;
        public const uint Solid = 1 << 1;
        public const uint Mixed = 1 << 2;
        public const uint Visible = 1 << 3;
        public const uint Dirty = 1 << 4;
    }
}