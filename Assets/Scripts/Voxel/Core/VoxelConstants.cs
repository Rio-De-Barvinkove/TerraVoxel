using UnityEngine;

namespace TerraVoxel.Voxel.Core
{
    /// <summary>
    /// Default voxel constants for chunk sizing and world scale. WorldGenConfig may override ChunkSize per world.
    /// </summary>
    public static class VoxelConstants
    {
        public const int ChunkSize = 32;
        public const int ColumnChunks = 8;
        public const float VoxelSize = 0.1f;
        public const int WorldHeight = ChunkSize * ColumnChunks;
    }
}


