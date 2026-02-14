using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using UnityEngine;

namespace TerraVoxel.Voxel.Lod
{
    /// <summary>
    /// Optional LOD mode: uses ChunkOctree instead of distance-based pending.
    /// Traverse fills leaves; caller adds (coord, lodStep) to pending.
    /// SvoManager.TryGetOrBuildMesh used for leaf mesh (downsampled).
    /// </summary>
    public sealed class ChunkOctreeLodManager
    {
        readonly ChunkOctree _octree;
        readonly List<(ChunkCoord coord, int lodStep)> _leaves = new List<(ChunkCoord, int)>();
        readonly int _maxNodeCreationsPerFrame;

        public ChunkOctreeLodManager(int loadRadius, int columnChunks, float innerPaddingChunks = 2f, int maxNodeCreationsPerFrame = 50)
        {
            _octree = new ChunkOctree(loadRadius, columnChunks, innerPaddingChunks);
            _maxNodeCreationsPerFrame = Mathf.Max(1, maxNodeCreationsPerFrame);
        }

        public void Traverse(ChunkCoord center, System.Action<ChunkCoord, int> onLeafCoord)
        {
            _octree.EnsureRoot(center.X, center.Z);
            _octree.Traverse(center.X, center.Z, _maxNodeCreationsPerFrame, onLeafCoord);
        }

        public void CollectLeaves(ChunkCoord center, System.Func<ChunkCoord, bool> isActive, HashSet<ChunkCoord> pendingSet, Dictionary<ChunkCoord, int> pendingLodStep, bool replacePending = true, int pendingQueueCap = 0)
        {
            _leaves.Clear();
            Traverse(center, (coord, lodStep) =>
            {
                if (isActive(coord)) return;
                _leaves.Add((coord, lodStep));
            });

            if (replacePending)
            {
                pendingSet.Clear();
                pendingLodStep.Clear();
            }
            foreach (var (coord, lodStep) in _leaves)
            {
                if (pendingQueueCap > 0 && pendingSet.Count >= pendingQueueCap) break;
                if (pendingSet.Add(coord))
                    pendingLodStep[coord] = lodStep;
            }
        }
    }
}
