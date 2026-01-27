using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Meshing;

namespace TerraVoxel.Voxel.Streaming
{
    public enum ChunkTaskState
    {
        PendingGen,
        PendingMesh,
        ReadyToApply,
        Active,
        Unload
    }

    public struct ChunkTask
    {
        public ChunkCoord Coord;
        public ChunkTaskState State;
        public ChunkData Data;
        public MeshData MeshData;
    }
}


