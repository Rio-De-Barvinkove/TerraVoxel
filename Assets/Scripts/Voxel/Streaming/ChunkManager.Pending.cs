using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Lod;
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>Partial: pending queue, distance heap, radius checks, GetInitialLodStep. All access to _pendingSet, _pending, _pendingDistanceHeap is main-thread only; no lock. _pending, viewCone, worldGen, player, loadRadius, etc. are defined in other ChunkManager partials.</summary>
    public partial class ChunkManager
    {
        /// <summary>True when center moved by pendingResetDistance or pending over cap. Caller must call RebuildPendingQueue(center) when true so _lastPendingCenter is updated.</summary>
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

        /// <summary>Clears and refills pending from loadRadius box. Caps size to pendingQueueCap when &gt; 0. Distance is XZ-only (horizontal).</summary>
        internal void RebuildPendingQueue(ChunkCoord center)
        {
            _pending.Clear();
            _pendingSet.Clear();
            _pendingLodStep.Clear();
            if (viewCone != null && viewCone.Enabled)
                viewCone.Clear();
            _pendingDistanceHeap.Clear();
            _pendingDequeueCenter = default;
            _lastPendingCenter = center;
            _hasPendingCenter = true;

            if (worldGen == null) return;
            int columnChunks = worldGen.ColumnChunks;
            if (columnChunks <= 0) return;

            for (int dz = -loadRadius; dz <= loadRadius; dz++)
            {
                for (int dx = -loadRadius; dx <= loadRadius; dx++)
                {
                    for (int dy = 0; dy < columnChunks; dy++)
                    {
                        if (pendingQueueCap > 0 && _pendingSet.Count >= pendingQueueCap)
                            return;
                        var coord = new ChunkCoord(center.X + dx, dy, center.Z + dz);
                        if (_active.ContainsKey(coord)) continue;
                        if (_pendingSet.Add(coord) && viewCone != null && viewCone.Enabled)
                            viewCone.EnqueueWithPriority(coord, center, player);
                    }
                }
            }
        }

        /// <summary>Invalidates the pending distance heap so it will be rebuilt on next TryFindClosestPending. Call when _pendingSet was replaced (e.g. from octree).</summary>
        internal void InvalidatePendingHeap()
        {
            _pendingDistanceHeap.Clear();
            _pendingDequeueCenter = default;
        }

        /// <summary>When viewCone is enabled but heap is empty and _pendingSet has entries, repopulates viewCone from _pendingSet so dequeue can progress. Call once per frame from MaintainRadius.</summary>
        internal void RepopulateViewConeFromPendingSet(ChunkCoord center)
        {
            if (viewCone == null || !viewCone.Enabled || viewCone.Count > 0 || _pendingSet.Count == 0 || player == null) return;
            foreach (var c in _pendingSet)
                viewCone.EnqueueWithPriority(c, center, player);
        }

        /// <summary>Removes one pending coord (farthest when no viewCone). O(n) over pending set when using TryFindFarthestPending.</summary>
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

        /// <summary>Dequeues one pending coord (closest when no viewCone). When viewCone enabled, returns first from heap; if heap empty but _pendingSet has entries, falls back to distance-based dequeue.</summary>
        internal bool TryDequeuePending(ChunkCoord center, out ChunkCoord coord)
        {
            coord = default;
            if (PendingCount == 0 && _pendingSet.Count == 0) return false;
            if (viewCone != null && viewCone.Enabled)
            {
                if (viewCone.TryDequeue(out coord))
                {
                    _pendingSet.Remove(coord);
                    return true;
                }
                if (_pendingSet.Count > 0)
                    return TryFindClosestPending(center, out coord);
                return false;
            }
            return TryFindClosestPending(center, out coord);
        }

        /// <summary>Returns closest pending coord by XZ distance (Y ignored). Rebuilds heap when center changes; O(n) over _pendingSet.</summary>
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

        /// <summary>XZ-only distance squared (Y ignored for horizontal radius). For vertical worlds consider including Y.</summary>
        static int PendingDistanceSq(ChunkCoord c, ChunkCoord center)
        {
            int dx = c.X - center.X;
            int dz = c.Z - center.Z;
            return dx * dx + dz * dz;
        }

        /// <summary>Rebuilds min-heap from _pendingSet for current center. O(n) per center change.</summary>
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

        /// <summary>Farthest pending coord by XZ distance. O(n) over _pendingSet.</summary>
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
                int d = dx * dx + dz * dz;
                if (d > bestDistSq)
                {
                    bestDistSq = d;
                    best = c;
                }
            }
            coord = best;
            return true;
        }

        /// <summary>True if coord is within keepRadius of center (XZ only; Y checked against ColumnChunks bounds).</summary>
        internal bool IsWithinKeepRadius(ChunkCoord coord, ChunkCoord center, int keepRadius)
        {
            if (keepRadius < 0) return false;
            if (worldGen == null) return false;
            if (coord.Y < 0 || coord.Y >= worldGen.ColumnChunks) return false;
            int dx = Mathf.Abs(coord.X - center.X);
            int dz = Mathf.Abs(coord.Z - center.Z);
            return dx <= keepRadius && dz <= keepRadius;
        }

        /// <summary>True if coord is within radius of center (XZ only; Y checked against ColumnChunks bounds).</summary>
        internal bool IsWithinLoadRadius(ChunkCoord coord, ChunkCoord center, int radius)
        {
            if (radius < 0) return false;
            if (worldGen == null) return false;
            if (coord.Y < 0 || coord.Y >= worldGen.ColumnChunks) return false;
            int dx = Mathf.Abs(coord.X - center.X);
            int dz = Mathf.Abs(coord.Z - center.Z);
            return dx <= radius && dz <= radius;
        }

        /// <summary>Initial LOD step for chunk not yet meshed. Uses current step 1 in ResolveLevel because chunk has no mesh yet. XZ distance only.</summary>
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
