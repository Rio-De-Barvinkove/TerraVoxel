using TerraVoxel.Voxel.Core;
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>Partial: removal queue processing, QueueRemoval, RemoveChunk.</summary>
    public partial class ChunkManager
    {
        internal void ProcessRemovalQueue()
        {
            if (player == null || worldGen == null) return;
            ChunkCoord center = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize);
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

                RemoveChunk(coord);
                _removeSet.Remove(coord);
                count++;
            }
        }

        internal void QueueRemoval(ChunkCoord coord)
        {
            if (!_active.ContainsKey(coord)) return;
            if (_removeSet.Add(coord))
                _removeQueue.Enqueue(coord);
        }

        internal void RemoveChunk(ChunkCoord coord)
        {
            if (!_active.TryGetValue(coord, out var chunk)) return;
            _active.Remove(coord);
            _meshedOnce.Remove(coord);
            _preloaded.Remove(coord);
            _preloadSet.Remove(coord);
            if (hybridSave != null)
            {
                hybridSave.HandleChunkUnloaded(coord, chunk.Data);
            }
            else
            {
                if (saveManager != null && saveManager.SaveOnUnload)
                    saveManager.EnqueueSave(coord, chunk.Data);
                if (modManager != null)
                    modManager.HandleChunkUnloaded(coord);
            }

            // Cache chunk data in RAM before disposing
            if (enableDataCache && chunk.Data.IsCreated)
            {
                CacheChunkData(coord, chunk.Data);
            }

            if (chunk.Data.IsCreated) chunk.Data.Dispose();
            ReleaseMeshCacheForChunk(coord);
            ReleaseFaceCacheForChunk(coord);
            _neighborDirtyFaces.Remove(coord);
            _faceRemeshSet.Remove(coord);
            if (svoManager != null)
                svoManager.ReleaseForChunk(coord);
            if (_pendingCachedMeshes.ContainsKey(coord))
                _pendingCachedMeshes.Remove(coord);

            _integrationSet.TryRemove(coord, out _);
            if (_pendingMeshJobs.TryGetValue(coord, out var meshJob))
            {
                meshJob.Dispose();
                _pendingMeshJobs.Remove(coord);
            }

            _pool.Return(chunk);
            RebuildNeighbors(coord);
        }
    }
}
