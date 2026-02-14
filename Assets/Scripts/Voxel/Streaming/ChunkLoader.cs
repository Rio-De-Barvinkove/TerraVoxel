using System;
using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Generation;
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>Facade: delegates radius maintenance and pending/preload spawn to ChunkManager. When UseGpuPipeline, respects GpuWorldState.ChunkCount vs GpuMaxChunks. ProcessPending/ProcessPreload use hard iteration limits to avoid runaway loops when viewCone rejects.</summary>
    internal sealed class ChunkLoader
    {
        readonly ChunkManager.Context _ctx;
        static bool _loggedDequeueFalse;
        static bool _loggedOutsideRadius;
        static bool _loggedAlreadyActive;
        static bool _loggedBudget;
        static bool _loggedGpuLimit;
        static bool _loggedGenLimit;
        static bool _loggedProcessPendingEntered;

        public ChunkLoader(ChunkManager.Context ctx)
        {
            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
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
            if (_ctx.Player == null || _ctx.WorldGen == null) return;
            ChunkCoord center = PlayerTracker.WorldToChunk(_ctx.Player.position, _ctx.WorldGen.ChunkSize);

            int pendingCount = Mathf.Max(_ctx.Owner.PendingCount, _ctx.Owner.PendingSetCount);
            if (pendingCount > 0 && !_loggedProcessPendingEntered)
            {
                _loggedProcessPendingEntered = true;
                Debug.Log($"[ChunkManager] ProcessPending entered: PendingCount={_ctx.Owner.PendingCount}, PendingSetCount={_ctx.Owner.PendingSetCount}, center={center}");
            }

            int spawned = 0;
            int maxIterations = Mathf.Min(pendingCount > 0 ? pendingCount : 1, Mathf.Max(maxSpawnsPerFrame * 8, 64));
            int iterations = 0;

            while ((_ctx.Owner.PendingCount > 0 || _ctx.Owner.PendingSetCount > 0) && spawned < maxSpawnsPerFrame && iterations < maxIterations)
            {
                iterations++;
                if (_ctx.BudgetExceeded())
                {
                    if (!_loggedBudget) { _loggedBudget = true; Debug.Log("[ChunkManager] ProcessPending: break BudgetExceeded"); }
                    break;
                }
                if (_ctx.UseGpuPipeline && _ctx.GpuWorldState != null && _ctx.GpuWorldState.ChunkCount >= _ctx.GpuWorldStateMaxChunks)
                {
                    if (!_loggedGpuLimit)
                    {
                        _loggedGpuLimit = true;
                        string hint = _ctx.HybridSave != null
                            ? " With hybrid save, slots are freed after readback; increase Gpu Max Chunks or reduce load radius."
                            : " Increase Gpu Max Chunks in ChunkManager or reduce load radius.";
                        Debug.LogWarning($"[ChunkManager] GPU chunk limit reached: ChunkCount={_ctx.GpuWorldState.ChunkCount} >= GpuMaxChunks={_ctx.GpuMaxChunks}. No new chunks will spawn.{hint}");
                    }
                    break;
                }
                if (!_ctx.UseGpuPipeline && _ctx.GenJobs.Count >= _ctx.CurrentMaxGenJobsInFlight)
                {
                    if (!_loggedGenLimit) { _loggedGenLimit = true; Debug.Log($"[ChunkManager] ProcessPending: break GenJobs={_ctx.GenJobs.Count} >= max"); }
                    break;
                }
                if (!TryDequeuePending(center, out var coord))
                {
                    if (!_loggedDequeueFalse) { _loggedDequeueFalse = true; Debug.Log("[ChunkManager] ProcessPending: TryDequeuePending returned false"); }
                    break;
                }
                if (!IsWithinLoadRadius(coord, center, loadRadius))
                {
                    if (!_loggedOutsideRadius) { _loggedOutsideRadius = true; Debug.Log($"[ChunkManager] ProcessPending: coord {coord} outside load radius center={center} radius={loadRadius}"); }
                    continue;
                }
                if (_ctx.Active.ContainsKey(coord))
                {
                    if (!_loggedAlreadyActive) { _loggedAlreadyActive = true; Debug.Log($"[ChunkManager] ProcessPending: coord {coord} already in Active"); }
                    continue;
                }
                SpawnChunk(coord);
                spawned++;
            }
            _ctx.SpawnedLastFrame = spawned;
        }

        internal void ProcessPreload()
        {
            if (!enablePreload)
            {
                _ctx.Preload.Clear();
                _ctx.PreloadSet.Clear();
                return;
            }
            if (_ctx.Player == null || _ctx.WorldGen == null) return;
            if (_ctx.Preload.Count == 0) return;
            if (_ctx.BudgetExceeded()) return;

            ChunkCoord center = PlayerTracker.WorldToChunk(_ctx.Player.position, _ctx.WorldGen.ChunkSize);
            int effectivePreloadRadius = _ctx.EffectivePreloadRadius();

            int spawned = 0;
            int preloadCount = _ctx.Preload.Count;
            int maxIterations = Mathf.Min(preloadCount > 0 ? preloadCount : 1, Mathf.Max(_ctx.CurrentMaxPreloadsPerFrame * 4, 32));
            int iterations = 0;

            while (_ctx.Preload.Count > 0 && spawned < _ctx.CurrentMaxPreloadsPerFrame && iterations < maxIterations)
            {
                iterations++;
                if (_ctx.BudgetExceeded()) break;
                if (_ctx.UseGpuPipeline && _ctx.GpuWorldState != null && _ctx.GpuWorldState.ChunkCount >= _ctx.GpuWorldStateMaxChunks) break;
                if (!_ctx.UseGpuPipeline && _ctx.GenJobs.Count >= _ctx.CurrentMaxGenJobsInFlight) break;
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
        internal bool IsWithinKeepRadius(ChunkCoord coord, ChunkCoord center, int keepRadius) => keepRadius >= 0 && _ctx.Owner.IsWithinKeepRadius(coord, center, keepRadius);
        internal bool IsWithinLoadRadius(ChunkCoord coord, ChunkCoord center, int radius) => radius >= 0 && _ctx.Owner.IsWithinLoadRadius(coord, center, radius);
    }
}
