using TerraVoxel.Voxel.Core;
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    public static class PlayerTracker
    {
        public static ChunkCoord WorldToChunk(Vector3 worldPos, int chunkSize)
        {
            double scale = chunkSize * VoxelConstants.VoxelSize;
            int cx = VoxelMath.FloorToIntClamped(worldPos.x / scale);
            int cy = VoxelMath.FloorToIntClamped(worldPos.y / scale);
            int cz = VoxelMath.FloorToIntClamped(worldPos.z / scale);
            return new ChunkCoord(cx, cy, cz);
        }
    }
}


