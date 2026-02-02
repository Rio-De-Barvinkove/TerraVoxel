using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Lod;
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>Partial: pending queue, radius checks, GetInitialLodStep.</summary>
    public partial class ChunkManager
    {
        bool ShouldRebuildPending(ChunkCoord center)
        {
            if (!_hasPendingCenter)
            {
                _lastPendingCenter = center;
                _hasPendingCenter = true;
                return false;
            }

            if (pendingQueueCap > 0 && PendingCount > pendingQueueCap)
                return true;

            if (pendingResetDistance > 0)
            {
                int dx = Mathf.Abs(center.X - _lastPendingCenter.X);
                int dz = Mathf.Abs(center.Z - _lastPendingCenter.Z);
                if (dx > pendingResetDistance || dz > pendingResetDistance)
                    return true;
            }

            return false;
        }

        internal void RebuildPendingQueue(ChunkCoord center)
        {
            _pending.Clear();
            _pendingSet.Clear();
            if (viewCone != null && viewCone.Enabled)
                viewCone.Clear();
            _pendingDistanceHeap.Clear();
            _pendingDequeueCenter = default;
            _lastPendingCenter = center;
            _hasPendingCenter = true;

            for (int dz = -loadRadius; dz <= loadRadius; dz++)
            {
                for (int dx = -loadRadius; dx <= loadRadius; dx++)
                {
                    for (int dy = 0; dy < worldGen.ColumnChunks; dy++)
                    {
                        var coord = new ChunkCoord(center.X + dx, dy, center.Z + dz);
                        if (_active.ContainsKey(coord)) continue;
                        if (viewCone != null && viewCone.Enabled)
                        {
                            if (_pendingSet.Add(coord))
                                viewCone.EnqueueWithPriority(coord, center, player);
                        }
                        else
                        {
                            _pendingSet.Add(coord);
                        }
                    }
                }
            }
        }

        internal void DropOnePendingOldest(ChunkCoord center)
        {
            if (PendingCount == 0) return;
            ChunkCoord dropped;
            if (viewCone != null && viewCone.Enabled)
            {
                if (!viewCone.TryRemoveLowestPriority(out dropped)) return;
            }
            else
            {
                if (!TryFindFarthestPending(center, out dropped)) return;
            }
            _pendingSet.Remove(dropped);
        }

        internal bool TryDequeuePending(ChunkCoord center, out ChunkCoord coord)
        {
            if (PendingCount == 0)
            {
                coord = default;
                return false;
            }
            if (viewCone != null && viewCone.Enabled)
            {
                while (viewCone.TryDequeue(out coord))
                {
                    if (_pendingSet.Remove(coord))
                        return true;
                }
                return false;
            }
            return TryFindClosestPending(center, out coord);
        }

        internal bool TryFindClosestPending(ChunkCoord center, out ChunkCoord coord)
        {
            coord = default;
            if (_pendingSet.Count == 0) return false;
            if (_pendingDistanceHeap.Count == 0 || center.X != _pendingDequeueCenter.X || center.Y != _pendingDequeueCenter.Y || center.Z != _pendingDequeueCenter.Z)
                BuildPendingDistanceHeap(center);
            while (_pendingDistanceHeap.Count > 0)
            {
                TryPopPendingDistanceMin(out coord);
                if (_pendingSet.Remove(coord))
                    return true;
            }
            return false;
        }

        static int PendingDistanceSq(ChunkCoord c, ChunkCoord center)
        {
            int dx = c.X - center.X;
            int dz = c.Z - center.Z;
            return dx * dx + dz * dz;
        }

        void BuildPendingDistanceHeap(ChunkCoord center)
        {
            _pendingDequeueCenter = center;
            _pendingDistanceHeap.Clear();
            foreach (var c in _pendingSet)
                _pendingDistanceHeap.Add(c);
            for (int i = _pendingDistanceHeap.Count / 2 - 1; i >= 0; i--)
                PendingDistanceHeapBubbleDown(i);
        }

        bool TryPopPendingDistanceMin(out ChunkCoord coord)
        {
            if (_pendingDistanceHeap.Count == 0)
            {
                coord = default;
                return false;
            }
            coord = _pendingDistanceHeap[0];
            int last = _pendingDistanceHeap.Count - 1;
            _pendingDistanceHeap[0] = _pendingDistanceHeap[last];
            _pendingDistanceHeap.RemoveAt(last);
            if (_pendingDistanceHeap.Count > 0)
                PendingDistanceHeapBubbleDown(0);
            return true;
        }

        void PendingDistanceHeapBubbleDown(int i)
        {
            int count = _pendingDistanceHeap.Count;
            var heap = _pendingDistanceHeap;
            var center = _pendingDequeueCenter;
            while (true)
            {
                int left = 2 * i + 1;
                int right = 2 * i + 2;
                int smallest = i;
                if (left < count && PendingDistanceSq(heap[left], center) < PendingDistanceSq(heap[smallest], center))
                    smallest = left;
                if (right < count && PendingDistanceSq(heap[right], center) < PendingDistanceSq(heap[smallest], center))
                    smallest = right;
                if (smallest == i) break;
                var tmp = heap[i];
                heap[i] = heap[smallest];
                heap[smallest] = tmp;
                i = smallest;
            }
        }

        internal bool TryFindFarthestPending(ChunkCoord center, out ChunkCoord coord)
        {
            coord = default;
            if (_pendingSet.Count == 0) return false;
            ChunkCoord best = default;
            int bestDistSq = -1;
            foreach (var c in _pendingSet)
            {
                int dx = c.X - center.X;
                int dz = c.Z - center.Z;
                int d = dx * dx + dz * dz; // 2D distance (XZ)
                if (d > bestDistSq)
                {
                    bestDistSq = d;
                    best = c;
                }
            }
            coord = best;
            return true;
        }

        internal bool IsWithinKeepRadius(ChunkCoord coord, ChunkCoord center, int keepRadius)
        {
            if (worldGen == null) return false;
            if (coord.Y < 0 || coord.Y >= worldGen.ColumnChunks) return false;
            int dx = Mathf.Abs(coord.X - center.X);
            int dz = Mathf.Abs(coord.Z - center.Z);
            return dx <= keepRadius && dz <= keepRadius;
        }

        internal bool IsWithinLoadRadius(ChunkCoord coord, ChunkCoord center, int radius)
        {
            if (worldGen == null) return false;
            if (coord.Y < 0 || coord.Y >= worldGen.ColumnChunks) return false;
            int dx = Mathf.Abs(coord.X - center.X);
            int dz = Mathf.Abs(coord.Z - center.Z);
            return dx <= radius && dz <= radius;
        }

        internal int GetInitialLodStep(ChunkCoord coord)
        {
            if (enableFullLod && lodSettings != null && player != null && worldGen != null)
            {
                ChunkCoord center = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize);
                int dx = Mathf.Abs(coord.X - center.X);
                int dz = Mathf.Abs(coord.Z - center.Z);
                int dist = Mathf.Max(dx, dz);
                var desired = lodSettings.ResolveLevel(dist, 1, ChunkLodMode.Mesh);
                return Mathf.Max(1, desired.LodStep);
            }

            if (!enableReverseLod) return 1;
            if (reverseLodStep <= 1) return 1;
            if (player == null || worldGen == null) return 1;

            ChunkCoord playerChunk = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize);
            int dxx = Mathf.Abs(coord.X - playerChunk.X);
            int dzz = Mathf.Abs(coord.Z - playerChunk.Z);
            int dist2 = Mathf.Max(dxx, dzz);
            if (dist2 <= reverseLodMinDistance) return 1;
            return reverseLodStep;
        }
    }
}
