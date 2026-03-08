using TerraVoxel.Voxel.Core;
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>Converts world position to chunk coordinate using chunk size and VoxelConstants.VoxelSize. Supports any VoxelSize (scale = chunkSize * VoxelSize).</summary>
    public static class PlayerTracker
    {
        /// <param name="worldPos">World position (Unity units).</param>
        /// <param name="chunkSize">Chunk size in voxels; must be &gt; 0 for correct result. If &lt;= 0, returns default(ChunkCoord).</param>
        /// <param name="voxelSize">World size of one voxel. When omitted, uses VoxelConstants.VoxelSize.</param>
        public static ChunkCoord WorldToChunk(Vector3 worldPos, int chunkSize, float voxelSize = -1f)
        {
            if (chunkSize <= 0)
            {
                Debug.LogWarning("[PlayerTracker] WorldToChunk: chunkSize must be > 0 (got " + chunkSize + "). Returning (0,0,0).");
                return default;
            }
            float vs = voxelSize > 0f ? voxelSize : VoxelConstants.VoxelSize;
            double scale = chunkSize * (double)vs;
            int cx = VoxelMath.FloorToIntClamped(worldPos.x / scale);
            int cy = VoxelMath.FloorToIntClamped(worldPos.y / scale);
            int cz = VoxelMath.FloorToIntClamped(worldPos.z / scale);
            return new ChunkCoord(cx, cy, cz);
        }
    }
}


