/*
using TerraVoxel.Voxel.Core;
using UnityEngine;

namespace TerraVoxel.Voxel.Lod
{
    /// <summary>
    /// Quad-tree node for XZ plane with Y range. Leaf = one logical chunk region (coord + LodStep).
    /// Subdivide when camera inside AABB; collapse when outside.
    /// </summary>
    public sealed class ChunkOctreeNode
    {
        public ChunkOctreeNode Parent;
        public ChunkOctreeNode[] Children;
        public int MinX, MinZ, MaxX, MaxZ;
        public int MinY, MaxY;
        public int LodStep;
        public int Depth;

        public bool IsLeaf => Children == null;

        public ChunkOctreeNode(int minX, int minZ, int maxX, int maxZ, int minY, int maxY, int lodStep, int depth)
        {
            MinX = minX;
            MinZ = minZ;
            MaxX = maxX;
            MaxZ = maxZ;
            MinY = minY;
            MaxY = maxY;
            LodStep = lodStep;
            Depth = depth;
        }

        public bool ContainsCamera(int camChunkX, int camChunkZ, float paddingChunks)
        {
            float pad = paddingChunks;
            float minXf = MinX - pad;
            float maxXf = MaxX + pad;
            float minZf = MinZ - pad;
            float maxZf = MaxZ + pad;
            return camChunkX >= minXf && camChunkX <= maxXf && camChunkZ >= minZf && camChunkZ <= maxZf;
        }

        public bool CameraOutside(int camChunkX, int camChunkZ, float paddingChunks)
        {
            float pad = paddingChunks;
            return camChunkX < MinX - pad || camChunkX > MaxX + pad || camChunkZ < MinZ - pad || camChunkZ > MaxZ + pad;
        }

        public void EnumerateCoords(System.Action<ChunkCoord, int> onCoord)
        {
            if (IsLeaf)
            {
                for (int x = MinX; x <= MaxX; x++)
                {
                    for (int z = MinZ; z <= MaxZ; z++)
                    {
                        for (int y = MinY; y <= MaxY; y++)
                        {
                            onCoord(new ChunkCoord(x, y, z), LodStep);
                        }
                    }
                }
            }
            else
            {
                for (int i = 0; i < Children.Length; i++)
                {
                    if (Children[i] != null)
                        Children[i].EnumerateCoords(onCoord);
                }
            }
        }
    }
}
*/