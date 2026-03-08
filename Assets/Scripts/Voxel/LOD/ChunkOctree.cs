/*
using TerraVoxel.Voxel.Core;
using UnityEngine;
using System.Collections.Generic;

namespace TerraVoxel.Voxel.Lod
{
    /// <summary>
    /// Quad-tree on XZ for flat terrain. Traverse updates subdivide/collapse by camera.
    /// Leaf = region (coord range) + LodStep. Depth = log2(loadRadius).
    /// </summary>
    public sealed class ChunkOctree
    {
        public ChunkOctreeNode Root { get; private set; }
        readonly int _loadRadius;
        readonly int _columnChunks;
        readonly int _divisions;
        readonly float _innerPaddingChunks;
        int _creationsThisFrame;

        public ChunkOctree(int loadRadius, int columnChunks, float innerPaddingChunks = 2f)
        {
            _loadRadius = Mathf.Max(1, loadRadius);
            _columnChunks = Mathf.Max(1, columnChunks);
            _innerPaddingChunks = Mathf.Max(0f, innerPaddingChunks);
            _divisions = Mathf.Max(1, Mathf.CeilToInt(Mathf.Log(_loadRadius * 2 + 1, 2)));
        }

        public void EnsureRoot(int centerX, int centerZ)
        {
            if (Root != null) return;
            int half = _loadRadius;
            int minX = centerX - half;
            int maxX = centerX + half;
            int minZ = centerZ - half;
            int maxZ = centerZ + half;
            int lodStep = Mathf.Max(1, 1 << (_divisions - 1));
            Root = new ChunkOctreeNode(minX, minZ, maxX, maxZ, 0, _columnChunks - 1, lodStep, 0);
        }

        public void Traverse(int camChunkX, int camChunkZ, int maxNodeCreationsPerFrame, System.Action<ChunkCoord, int> onLeafCoord)
        {
            if (Root == null) return;
            _creationsThisFrame = 0;
            TraverseInternal(Root, camChunkX, camChunkZ, onLeafCoord, maxNodeCreationsPerFrame);
        }

        void TraverseInternal(ChunkOctreeNode node, int camChunkX, int camChunkZ, System.Action<ChunkCoord, int> onLeafCoord, int maxCreations)
        {
            if (node.Children != null)
            {
                if (ShouldCollapse(node, camChunkX, camChunkZ))
                    Collapse(node);
                else
                {
                    for (int i = 0; i < node.Children.Length; i++)
                        TraverseInternal(node.Children[i], camChunkX, camChunkZ, onLeafCoord, maxCreations);
                    return;
                }
            }

            if (ShouldSubdivide(node, camChunkX, camChunkZ) && _creationsThisFrame < maxCreations)
            {
                Subdivide(node, camChunkX, camChunkZ);
                _creationsThisFrame++;
                for (int i = 0; i < node.Children.Length; i++)
                    TraverseInternal(node.Children[i], camChunkX, camChunkZ, onLeafCoord, maxCreations);
                return;
            }

            node.EnumerateCoords(onLeafCoord);
        }

        public bool ShouldSubdivide(ChunkOctreeNode node, int camChunkX, int camChunkZ)
        {
            if (node.Depth >= _divisions - 1) return false;
            return node.ContainsCamera(camChunkX, camChunkZ, _innerPaddingChunks);
        }

        public bool ShouldCollapse(ChunkOctreeNode node, int camChunkX, int camChunkZ)
        {
            return node.CameraOutside(camChunkX, camChunkZ, _innerPaddingChunks);
        }

        public void Subdivide(ChunkOctreeNode node, int camChunkX, int camChunkZ)
        {
            if (node.Children != null) return;
            int midX = (node.MinX + node.MaxX) / 2;
            int midZ = (node.MinZ + node.MaxZ) / 2;
            int childStep = Mathf.Max(1, node.LodStep / 2);
            int childDepth = node.Depth + 1;

            node.Children = new ChunkOctreeNode[4];
            node.Children[0] = new ChunkOctreeNode(node.MinX, node.MinZ, midX, midZ, node.MinY, node.MaxY, childStep, childDepth);
            node.Children[1] = new ChunkOctreeNode(midX + 1, node.MinZ, node.MaxX, midZ, node.MinY, node.MaxY, childStep, childDepth);
            node.Children[2] = new ChunkOctreeNode(node.MinX, midZ + 1, midX, node.MaxZ, node.MinY, node.MaxY, childStep, childDepth);
            node.Children[3] = new ChunkOctreeNode(midX + 1, midZ + 1, node.MaxX, node.MaxZ, node.MinY, node.MaxY, childStep, childDepth);
            for (int i = 0; i < 4; i++)
                node.Children[i].Parent = node;
        }

        public void Collapse(ChunkOctreeNode node)
        {
            node.Children = null;
        }
    }
}
*/