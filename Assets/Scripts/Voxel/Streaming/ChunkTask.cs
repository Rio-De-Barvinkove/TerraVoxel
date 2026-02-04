using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Meshing;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>Lifecycle state for a chunk in the streaming pipeline.</summary>
    public enum ChunkTaskState
    {
        PendingGen,
        PendingMesh,
        ReadyToApply,
        Active,
        Unload
    }

    /// <summary>Chunk coordinate, state, and optional data/mesh for streaming.</summary>
    public struct ChunkTask
    {
        public ChunkCoord Coord;
        public ChunkTaskState State;
        public ChunkData Data;
        public MeshData MeshData;
    }
}


