using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Lod;
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>Partial: MaintainRadius, ProcessFarRangeLod, ProcessPending, ProcessPreload. Main-thread only.</summary>
    public partial class ChunkManager
    {
        /// <summary>Maintains load/preload/keep radius: fills pending/preload, builds remove candidates, queues removal and far-range LOD. Requires worldGen and player. Uses nested loops over radius and ColumnChunks, or ChunkOctree when enableOctreeLod.</summary>
        internal void MaintainRadius()
        {
            if (worldGen == null || player == null) return;
            if (worldGen.ColumnChunks < 1)
            {
                if (!_warnedColumnChunksZero)
                {
                    _warnedColumnChunksZero = true;
                    Debug.LogWarning("[ChunkManager] WorldGen.ColumnChunks is < 1. No chunk coords will be added. Set ColumnChunks >= 1 in WorldGenConfig.");
                }
                return;
            }
            ChunkCoord center = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize);
            MaybeDropWork(center);
            if (!enableOctreeLod && ShouldRebuildPending(center))
                RebuildPendingQueue(center);
            RepopulateViewConeFromPendingSet(center);
            int effectivePreloadRadius = EffectivePreloadRadius();

            if (enableOctreeLod)
            {
                if (_octreeLodManager == null)
                    _octreeLodManager = new ChunkOctreeLodManager(loadRadius, worldGen.ColumnChunks, 2f, maxOctreeNodeCreationsPerFrame);
                _octreeLodManager.CollectLeaves(center, c => _active.ContainsKey(c), _pendingSet, _pendingLodStep, replacePending: true, pendingQueueCap: pendingQueueCap);
                InvalidatePendingHeap();
                foreach (var coord in new System.Collections.Generic.List<ChunkCoord>(_preloaded))
                {
                    if (_active.TryGetValue(coord, out var existing))
                        ActivatePreloadedChunk(coord, existing);
                }
                if (viewCone != null && viewCone.Enabled && !UseGpuPipeline)
                {
                    viewCone.Clear();
                    foreach (var coord in _pendingSet)
                        viewCone.EnqueueWithPriority(coord, center, player);
                }
            }
            else
            {
                for (int dz = -loadRadius; dz <= loadRadius; dz++)
                {
                    for (int dx = -loadRadius; dx <= loadRadius; dx++)
                    {
                        for (int dy = 0; dy < worldGen.ColumnChunks; dy++)
                        {
                            var coord = new ChunkCoord(center.X + dx, dy, center.Z + dz);
                            if (_active.TryGetValue(coord, out var existing))
                            {
                                if (_preloaded.Contains(coord))
                                    ActivatePreloadedChunk(coord, existing);
                                continue;
                            }
                            if (_pendingSet.Contains(coord)) continue;
                            if (pendingQueueCap > 0 && PendingCount >= pendingQueueCap)
                                DropOnePendingOldest(center);
                            if (viewCone != null && viewCone.Enabled && !UseGpuPipeline)
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

            if (enablePreload && effectivePreloadRadius > loadRadius)
            {
                for (int dz = -effectivePreloadRadius; dz <= effectivePreloadRadius; dz++)
                {
                    for (int dx = -effectivePreloadRadius; dx <= effectivePreloadRadius; dx++)
                    {
                        if (Mathf.Abs(dx) <= loadRadius && Mathf.Abs(dz) <= loadRadius) continue;
                        for (int dy = 0; dy < worldGen.ColumnChunks; dy++)
                        {
                            var coord = new ChunkCoord(center.X + dx, dy, center.Z + dz);
                            if (_active.ContainsKey(coord)) continue;
                            if (_pendingSet.Contains(coord)) continue;
                            if (_preloadSet.Contains(coord)) continue;
                            _preload.Enqueue(coord);
                            _preloadSet.Add(coord);
                        }
                    }
                }
            }

            int keepRadius = EffectiveUnloadRadius();
            if (enablePreload)
                keepRadius = Mathf.Max(keepRadius, effectivePreloadRadius);

            _removeCandidates.Clear();
            foreach (var kvp in _active)
            {
                if (IsWithinKeepRadius(kvp.Key, center, keepRadius)) continue;
                int dx = kvp.Key.X - center.X;
                int dy = kvp.Key.Y - center.Y;
                int dz = kvp.Key.Z - center.Z;
                int dist = dx * dx + dy * dy + dz * dz;
                _removeCandidates.Add(new RemoveCandidate(kvp.Key, dist));
            }

            _removeCandidates.Sort((a, b) => b.Distance.CompareTo(a.Distance));
            foreach (var c in _removeCandidates)
                QueueRemoval(c.Coord);

            if (enableFarRangeLod && farRangeRadius > keepRadius)
            {
                int farR = Mathf.Min(farRangeRadius, 32);
                for (int dz = -farR; dz <= farR; dz++)
                for (int dx = -farR; dx <= farR; dx++)
                {
                    int dist = dx * dx + dz * dz;
                    if (dist <= keepRadius * keepRadius) continue;
                    if (dist > farR * farR) continue;
                    for (int dy = 0; dy < worldGen.ColumnChunks; dy++)
                    {
                        var coord = new ChunkCoord(center.X + dx, dy, center.Z + dz);
                        if (_active.ContainsKey(coord)) continue;
                        if (_farRangeRenderSet.Add(coord))
                            _farRangeRenderQueue.Enqueue(coord);
                    }
                }
            }
        }

        /// <summary>Stub: render-only chunks beyond unloadRadius with low LOD/SVO not yet implemented. Drains far-range queue down to cap to avoid unbounded growth. Main-thread only.</summary>
        internal void ProcessFarRangeLod()
        {
            const int farRangeQueueCap = 1024;
            while (_farRangeRenderQueue.Count > farRangeQueueCap)
            {
                var coord = _farRangeRenderQueue.Dequeue();
                _farRangeRenderSet.Remove(coord);
            }
        }

        /// <summary>Dequeues pending coords and spawns up to maxSpawnsPerFrame (or CurrentMax when adaptive). Requires player and worldGen. Main-thread only.</summary>
        internal void ProcessPending()
        {
            if (player == null || worldGen == null) return;
            ChunkCoord center = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize);

            int maxSpawns = useGpuPipeline ? Mathf.Min(maxSpawnsPerFrame, gpuMaxSpawnsPerFrame) : maxSpawnsPerFrame;
            int spawned = 0;
            while ((PendingCount > 0 || PendingSetCount > 0) && spawned < maxSpawns)
            {
                if (BudgetExceeded()) break;
                if (useGpuPipeline && _gpuWorldState != null && _gpuWorldState.ChunkCount >= _gpuWorldState.MaxChunks)
                    break;
                if (_genJobs.Count >= CurrentMaxGenJobsInFlight)
                    break;
                if (!TryDequeuePending(center, out var coord))
                    break;
                if (!IsWithinLoadRadius(coord, center, loadRadius)) continue;
                if (_active.ContainsKey(coord)) continue;
                int lodStepOverride = _pendingLodStep.TryGetValue(coord, out var step) ? step : 0;
                if (lodStepOverride > 0)
                    _pendingLodStep.Remove(coord);
                SpawnChunk(coord, preload: false, lodStepOverride: lodStepOverride);
                spawned++;
            }
            _spawnedLastFrame = spawned;
        }

        /// <summary>Dequeues preload coords and spawns up to CurrentMaxPreloadsPerFrame. Requires player and worldGen. Main-thread only.</summary>
        internal void ProcessPreload()
        {
            if (!enablePreload) return;
            if (player == null || worldGen == null) return;
            if (_preload.Count == 0) return;
            if (BudgetExceeded()) return;

            ChunkCoord center = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize);
            int effectivePreloadRadius = EffectivePreloadRadius();

            int spawned = 0;
            while (_preload.Count > 0 && spawned < CurrentMaxPreloadsPerFrame)
            {
                if (BudgetExceeded()) break;
                if (useGpuPipeline && _gpuWorldState != null)
                {
                    if (_gpuWorldState.ChunkCount >= _gpuWorldState.MaxChunks) break;
                }
                else if (_genJobs.Count >= CurrentMaxGenJobsInFlight)
                    break;
                var coord = _preload.Dequeue();
                _preloadSet.Remove(coord);

                if (!IsWithinLoadRadius(coord, center, effectivePreloadRadius)) continue;
                if (IsWithinLoadRadius(coord, center, loadRadius))
                {
                    if (!_active.ContainsKey(coord) && !_pendingSet.Contains(coord))
                    {
                        if (viewCone != null && viewCone.Enabled && !UseGpuPipeline)
                        {
                            if (_pendingSet.Add(coord))
                                viewCone.EnqueueWithPriority(coord, center, player);
                        }
                        else
                        {
                            _pendingSet.Add(coord);
                        }
                    }
                    continue;
                }
                if (_active.ContainsKey(coord)) continue;

                SpawnChunk(coord, preload: true);
                spawned++;
            }
        }
    }
}
