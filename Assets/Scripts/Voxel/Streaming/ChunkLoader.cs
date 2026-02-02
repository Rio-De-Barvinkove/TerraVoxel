using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Generation;
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    internal sealed class ChunkLoader
    {
        readonly ChunkManager.Context _ctx;

        public ChunkLoader(ChunkManager.Context ctx)
        {
            _ctx = ctx;
        }

        Transform player => _ctx.Player;
        WorldGenConfig worldGen => _ctx.WorldGen;
        ChunkViewConePrioritizer viewCone => _ctx.ViewCone;
        int loadRadius => _ctx.LoadRadius;
        int maxSpawnsPerFrame => _ctx.MaxSpawnsPerFrame;
        bool enablePreload => _ctx.EnablePreload;

        internal void MaintainRadius() => _ctx.Owner.MaintainRadius();

        internal void ProcessPending()
        {
            if (player == null || worldGen == null) return;
            ChunkCoord center = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize);

            int spawned = 0;
            while (_ctx.Owner.PendingCount > 0 && spawned < maxSpawnsPerFrame)
            {
                if (_ctx.BudgetExceeded()) break;
                if (_ctx.GenJobs.Count >= _ctx.CurrentMaxGenJobsInFlight) break;
                if (!TryDequeuePending(center, out var coord))
                    break;
                if (IsWithinLoadRadius(coord, center, loadRadius) == false) continue;
                if (_ctx.Active.ContainsKey(coord)) continue;
                // Work dropping: skip spawning out-of-view-cone chunks (they get re-queued by MaintainRadius)
                if (viewCone != null && viewCone.Enabled && _ctx.WorkDropAngleDeg > 0f && !viewCone.IsInViewCone(coord, center, player))
                    continue;
                SpawnChunk(coord);
                spawned++;
            }
            _ctx.SpawnedLastFrame = spawned;
        }

        internal void ProcessPreload()
        {
            if (!enablePreload) return;
            if (player == null || worldGen == null) return;
            if (_ctx.Preload.Count == 0) return;
            if (_ctx.BudgetExceeded()) return;

            ChunkCoord center = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize);
            int effectivePreloadRadius = _ctx.EffectivePreloadRadius();

            int spawned = 0;
            while (_ctx.Preload.Count > 0 && spawned < _ctx.CurrentMaxPreloadsPerFrame)
            {
                if (_ctx.BudgetExceeded()) break;
                if (_ctx.GenJobs.Count >= _ctx.CurrentMaxGenJobsInFlight) break;
                var coord = _ctx.Preload.Dequeue();
                _ctx.PreloadSet.Remove(coord);

                if (!IsWithinLoadRadius(coord, center, effectivePreloadRadius)) continue;
                if (IsWithinLoadRadius(coord, center, loadRadius))
                {
                    if (!_ctx.Active.ContainsKey(coord) && !_ctx.PendingSet.Contains(coord))
                    {
                        if (viewCone != null && viewCone.Enabled)
                        {
                            if (_ctx.PendingSet.Add(coord))
                                viewCone.EnqueueWithPriority(coord, center, player);
                        }
                        else
                        {
                            _ctx.PendingSet.Add(coord);
                        }
                    }
                    continue;
                }
                if (_ctx.Active.ContainsKey(coord)) continue;

                SpawnChunk(coord, preload: true);
                spawned++;
            }
        }

        internal void ProcessRemovalQueue() => _ctx.Owner.ProcessRemovalQueue();
        internal void QueueRemoval(ChunkCoord coord) => _ctx.Owner.QueueRemoval(coord);
        internal void SpawnChunk(ChunkCoord coord, bool preload = false) => _ctx.Owner.SpawnChunk(coord, preload);
        internal void RemoveChunk(ChunkCoord coord) => _ctx.Owner.RemoveChunk(coord);
        internal bool TryDequeuePending(ChunkCoord center, out ChunkCoord coord) => _ctx.Owner.TryDequeuePending(center, out coord);
        internal bool TryFindClosestPending(ChunkCoord center, out ChunkCoord coord) => _ctx.Owner.TryFindClosestPending(center, out coord);
        internal bool TryFindFarthestPending(ChunkCoord center, out ChunkCoord coord) => _ctx.Owner.TryFindFarthestPending(center, out coord);
        internal void DropOnePendingOldest(ChunkCoord center) => _ctx.Owner.DropOnePendingOldest(center);
        internal void RebuildPendingQueue(ChunkCoord center) => _ctx.Owner.RebuildPendingQueue(center);
        internal bool IsWithinKeepRadius(ChunkCoord coord, ChunkCoord center, int keepRadius) => _ctx.Owner.IsWithinKeepRadius(coord, center, keepRadius);
        internal bool IsWithinLoadRadius(ChunkCoord coord, ChunkCoord center, int radius) => _ctx.Owner.IsWithinLoadRadius(coord, center, radius);
    }
}
