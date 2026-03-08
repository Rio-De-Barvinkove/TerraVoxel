using TerraVoxel.Voxel.Core;
/* using TerraVoxel.Voxel.Lod; */
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
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
            ChunkCoord center = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize, worldGen.VoxelSize);
            MaybeDropWork(center);
            if (!enableOctreeLod && ShouldRebuildPending(center))
                RebuildPendingQueue(center);
            RepopulateViewConeFromPendingSet(center);
            int effectivePreloadRadius = EffectivePreloadRadius();
            int vr = verticalRadius;

            {
                for (int dz = -loadRadius; dz <= loadRadius; dz++)
                {
                    for (int dx = -loadRadius; dx <= loadRadius; dx++)
                    {
                        for (int dy = -vr; dy <= vr; dy++)
                        {
                            var coord = new ChunkCoord(center.X + dx, center.Y + dy, center.Z + dz);
                            if (_active.TryGetValue(coord, out var existing))
                            {
                                if (_preloaded.Contains(coord))
                                    ActivatePreloadedChunk(coord, existing);
                                continue;
                            }
                            if (_pendingSet.Contains(coord)) continue;
                            if (pendingQueueCap > 0 && PendingCount >= pendingQueueCap)
                                DropOnePendingOldest(center);
                            _pendingSet.Add(coord);
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
                        for (int dy = -vr; dy <= vr; dy++)
                        {
                            var coord = new ChunkCoord(center.X + dx, center.Y + dy, center.Z + dz);
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

            GatherAndQueueRemovals(center, keepRadius);

            if (enableFarRangeLod && farRangeRadius > keepRadius)
            {
                int farR = Mathf.Min(farRangeRadius, 32);
                for (int dz = -farR; dz <= farR; dz++)
                for (int dx = -farR; dx <= farR; dx++)
                {
                    int dist = dx * dx + dz * dz;
                    if (dist <= keepRadius * keepRadius) continue;
                    if (dist > farR * farR) continue;
                    for (int dy = -vr; dy <= vr; dy++)
                    {
                        var coord = new ChunkCoord(center.X + dx, center.Y + dy, center.Z + dz);
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

        [BurstCompile]
        struct GatherRemoveCandidatesJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<ChunkCoord> ActiveKeys;
            public ChunkCoord Center;
            public int KeepRadiusXZ;
            public int KeepRadiusY;
            public NativeList<RemoveCandidate>.ParallelWriter Writer;

            public void Execute(int index)
            {
                var key = ActiveKeys[index];
                int dx = key.X - Center.X;
                int dy = key.Y - Center.Y;
                int dz = key.Z - Center.Z;
                int adx = dx < 0 ? -dx : dx;
                int ady = dy < 0 ? -dy : dy;
                int adz = dz < 0 ? -dz : dz;
                if (adx <= KeepRadiusXZ && adz <= KeepRadiusXZ && ady <= KeepRadiusY)
                    return;
                int dist = dx * dx + dy * dy + dz * dz;
                Writer.AddNoResize(new RemoveCandidate(key, dist));
            }
        }

        void GatherAndQueueRemovals(ChunkCoord center, int keepRadius)
        {
            int count = _active.Count;
            if (count == 0) return;

            var activeKeys = new NativeArray<ChunkCoord>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            int i = 0;
            foreach (var kvp in _active)
                activeKeys[i++] = kvp.Key;

            _removeCandidates.Clear();
            if (_removeCandidates.Capacity < count)
                _removeCandidates.Capacity = count;

            var job = new GatherRemoveCandidatesJob
            {
                ActiveKeys = activeKeys,
                Center = center,
                KeepRadiusXZ = keepRadius,
                KeepRadiusY = verticalRadius,
                Writer = _removeCandidates.AsParallelWriter()
            };

            job.Schedule(count, 64).Complete();
            activeKeys.Dispose();

            _removeCandidates.Sort();
            for (int j = 0; j < _removeCandidates.Length; j++)
                QueueRemoval(_removeCandidates[j].Coord);
        }

        /// <summary>Dequeues pending coords and spawns up to maxSpawnsPerFrame (or CurrentMax when adaptive). Requires player and worldGen. Main-thread only.</summary>
        internal void ProcessPending()
        {
            if (player == null || worldGen == null) return;
            ChunkCoord center = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize, worldGen.VoxelSize);

            int maxSpawns = maxSpawnsPerFrame;
            int spawned = 0;
            while ((PendingCount > 0 || PendingSetCount > 0) && spawned < maxSpawns)
            {
                if (BudgetExceeded()) break;
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

            ChunkCoord center = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize, worldGen.VoxelSize);
            int effectivePreloadRadius = EffectivePreloadRadius();

            int spawned = 0;
            while (_preload.Count > 0 && spawned < CurrentMaxPreloadsPerFrame)
            {
                if (BudgetExceeded()) break;
                if (_genJobs.Count >= CurrentMaxGenJobsInFlight)
                    break;
                var coord = _preload.Dequeue();
                _preloadSet.Remove(coord);

                if (!IsWithinLoadRadius(coord, center, effectivePreloadRadius)) continue;
                if (IsWithinLoadRadius(coord, center, loadRadius))
                {
                    if (!_active.ContainsKey(coord) && !_pendingSet.Contains(coord))
                    {
                        _pendingSet.Add(coord);
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
