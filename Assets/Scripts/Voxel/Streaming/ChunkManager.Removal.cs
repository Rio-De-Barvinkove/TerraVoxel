using System;
using TerraVoxel.Voxel.Core;
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>Partial: removal queue processing, QueueRemoval, RemoveChunk. Main-thread only; no lock. RemoveChunk wrapped in try-catch in ProcessRemovalQueue so one failure does not block the queue.</summary>
    public partial class ChunkManager
    {
        /// <summary>Processes removal queue up to maxRemovalsPerFrame and removalBudgetMs. Skips coords in keep radius or busy; calls RemoveChunk for each. Main-thread only.</summary>
        internal void ProcessRemovalQueue()
        {
            if (player == null || worldGen == null) return;
            ChunkCoord center = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize, worldGen.VoxelSize);
            int keepRadius = EffectiveUnloadRadius();
            if (enablePreload)
                keepRadius = Mathf.Max(keepRadius, EffectivePreloadRadius());

            double removalStart = Time.realtimeSinceStartupAsDouble;
            int count = 0;
            int guard = _removeQueue.Count;
            while (_removeQueue.Count > 0 && count < maxRemovalsPerFrame && guard-- > 0)
            {
                if (BudgetExceeded()) break;
                if (removalBudgetMs > 0f && (Time.realtimeSinceStartupAsDouble - removalStart) * 1000.0 >= removalBudgetMs)
                    break;
                var coord = _removeQueue.Dequeue();

                if (!_active.ContainsKey(coord))
                {
                    _removeSet.Remove(coord);
                    continue;
                }
                if (IsWithinKeepRadius(coord, center, keepRadius))
                {
                    _removeSet.Remove(coord);
                    continue;
                }
                if (IsChunkBusy(coord))
                {
                    _removeQueue.Enqueue(coord);
                    continue;
                }

                try
                {
                    RemoveChunk(coord);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ChunkManager] RemoveChunk {coord}: {e.Message}");
                }
                _removeSet.Remove(coord);
                count++;
            }
        }

        /// <summary>Enqueues coord for removal if it is active and not already in remove set. Idempotent (set add).</summary>
        internal void QueueRemoval(ChunkCoord coord)
        {
            if (!_active.ContainsKey(coord)) return;
            if (_removeSet.Add(coord))
                _removeQueue.Enqueue(coord);
        }

        /// <summary>Removes chunk from active set, enqueues save when hybridSave/saveManager, caches data when enableDataCache (CPU path), frees GPU slot when hybridSave is null (no save path), disposes data and mesh refs, returns chunk to pool. Main-thread only.</summary>
        internal void RemoveChunk(ChunkCoord coord)
        {
            if (!_active.TryGetValue(coord, out var chunk) || chunk == null) return;

            /* CPU-only rollback: GPU path вимкнено
            int gpuSlot = -1;
            if (useGpuPipeline && _gpuWorldState != null)
                _gpuWorldState.TryGetSlot(coord, out gpuSlot);
            */

            _active.Remove(coord);
            _meshedOnce.Remove(coord);
            _preloaded.Remove(coord);
            _preloadSet.Remove(coord);

            /* CPU-only rollback: save/cache вимкнено
            if (gpuSlot >= 0 && hybridSave != null)
            {
                hybridSave.HandleChunkUnloadedGpu(coord, gpuSlot);
            }
            else if (gpuSlot >= 0 && _gpuWorldState != null)
            {
                _gpuWorldState.FreeChunk(coord);
            }
            else if (hybridSave != null)
            {
                hybridSave.HandleChunkUnloaded(coord, chunk.Data);
            }
            else
            {
                if (saveManager != null && saveManager.SaveOnUnload && chunk.Data.IsCreated)
                    saveManager.EnqueueSave(coord, chunk.Data);
                if (modManager != null)
                    modManager.HandleChunkUnloaded(coord);
            }

            if (gpuSlot < 0 && enableDataCache && chunk.Data.IsCreated)
            {
                CacheChunkData(coord, chunk.Data);
            }
            */

            if (enableDataCache && chunk.Data.IsCreated)
                CacheChunkData(coord, chunk.Data);

            if (chunk.Data.IsCreated)
            {
                try { chunk.Data.Dispose(); }
                catch (Exception e) { Debug.LogWarning($"[ChunkManager] RemoveChunk Data.Dispose {coord}: {e.Message}"); }
            }
            ReleaseMeshCacheForChunk(coord);
            ReleaseFaceCacheForChunk(coord);
            _neighborDirtyFaces.Remove(coord);
            _faceRemeshSet.Remove(coord);
            /* if (svoManager != null)
                svoManager.ReleaseForChunk(coord); */
            if (_pendingCachedMeshes.ContainsKey(coord))
                _pendingCachedMeshes.Remove(coord);

            _integrationSet.TryRemove(coord, out _);
            if (_pendingMeshJobs.TryGetValue(coord, out var meshJob))
            {
                try { meshJob.Dispose(); } catch (Exception e) { Debug.LogWarning($"[ChunkManager] RemoveChunk meshJob.Dispose {coord}: {e.Message}"); }
                _pendingMeshJobs.Remove(coord);
            }

            /* if (gpuSlot >= 0 && chunk.IsGpuRendered)
                chunk.ClearGpuMeshRef();
            */
            _pool.Return(chunk);
            RebuildNeighbors(coord);
        }
    }
}
