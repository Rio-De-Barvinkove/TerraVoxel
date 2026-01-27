using UnityEngine;

namespace TerraVoxel.Voxel.Core
{
    /// <summary>
    /// Global voxel constants for chunk sizing and world scale.
    /// </summary>
    public static class VoxelConstants
    {
        public const int ChunkSize = 32;
        public const int ColumnChunks = 8;
        public const float VoxelSize = 0.1f;
        public const int WorldHeight = ChunkSize * ColumnChunks;
    }
}


