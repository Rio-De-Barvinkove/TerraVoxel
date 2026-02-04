using System.Collections.Concurrent;
using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>Processes integration queue: applies completed mesh jobs to chunks, registers cache, queues remesh. Delegates to ChunkManager when no separate manager.</summary>
    internal sealed class ChunkIntegrationManager
    {
        readonly ChunkManager.Context _ctx;

        public ChunkIntegrationManager(ChunkManager.Context ctx)
        {
            _ctx = ctx;
        }

        ConcurrentQueue<ChunkCoord> _integrationQueue => _ctx.IntegrationQueue;
        ConcurrentDictionary<ChunkCoord, byte> _integrationSet => _ctx.IntegrationSet;
        Dictionary<ChunkCoord, ChunkMeshJobHandle> _pendingMeshJobs => _ctx.PendingMeshJobs;
        Dictionary<ChunkCoord, ChunkManager.PendingCachedMesh> _pendingCachedMeshes => _ctx.PendingCachedMeshes;
        Dictionary<ChunkCoord, Chunk> _active => _ctx.Active;
        Dictionary<ChunkCoord, ChunkManager.MeshTask> _meshJobs => _ctx.MeshJobs;
        Dictionary<ulong, ChunkManager.CachedMeshEntry> _meshCache => _ctx.MeshCache;
        Dictionary<ChunkCoord, ulong> _chunkMeshHashes => _ctx.ChunkMeshHashes;
        HashSet<ChunkCoord> _preloaded => _ctx.Preloaded;
        HashSet<ChunkCoord> _meshedOnce => _ctx.MeshedOnce;
        ChunkViewConePrioritizer viewCone => _ctx.ViewCone;
        StreamingTimeBudget streamingBudget => _ctx.StreamingBudget;

        bool enableMeshCache => _ctx.EnableMeshCache;
        int maxMeshCacheEntries => _ctx.MaxMeshCacheEntries;
        bool dynamicIntegrationLimit => _ctx.DynamicIntegrationLimit;
        int maxIntegrationQueueSize => _ctx.MaxIntegrationQueueSize;
        bool enablePreload => _ctx.EnablePreload;
        int loadRadius => _ctx.LoadRadius;

        internal bool HasAnySolid(ChunkData data)
        {
            if (!data.Materials.IsCreated) return false;
            var mats = data.Materials;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] != 0) return true;
            }
            return false;
        }

        internal bool IsInIntegrationSet(ChunkCoord coord)
        {
            return _integrationSet.ContainsKey(coord);
        }

        internal void ProcessIntegrationQueue()
        {
            int integrationsThisFrame = 0;
            ChunkCoord center = default;
            int keepRadius = 0;
            bool hasCenter = _ctx.Player != null && _ctx.WorldGen != null;
            if (hasCenter)
            {
                center = PlayerTracker.WorldToChunk(_ctx.Player.position, _ctx.WorldGen.ChunkSize);
                keepRadius = _ctx.EffectiveUnloadRadius();
                if (enablePreload)
                    keepRadius = Mathf.Max(keepRadius, _ctx.EffectivePreloadRadius());
            }

            // Dynamic limit: if queue is very large, process more per frame to catch up
            int integrationLimit = _ctx.CurrentMaxIntegrationsPerFrame;
            if (dynamicIntegrationLimit && _integrationQueue.Count > maxIntegrationQueueSize * 0.5f)
            {
                // Process more aggressively when queue is large, but keep cap to avoid long frames
                integrationLimit = Mathf.Min(_ctx.CurrentMaxIntegrationsPerFrame * 2, _integrationQueue.Count / 10);
            }
            integrationLimit = Mathf.Min(integrationLimit, 64);

            // Clean up stale entries while processing (skip them instead of rebuilding queue)
            int processed = 0;
            int maxIterations = Mathf.Min(_integrationQueue.Count, integrationLimit * 2); // Prevent long frames

            while (_integrationQueue.Count > 0 && integrationsThisFrame < integrationLimit && processed < maxIterations)
            {
                if (streamingBudget != null && streamingBudget.IsExceeded())
                    break;

                processed++;
                ChunkCoord coord;
                if (!_integrationQueue.TryDequeue(out coord)) break;
                _integrationSet.TryRemove(coord, out _);

                // Skip stale entries (no longer active or out of range)
                if (!_active.TryGetValue(coord, out var chunk))
                {
                    // Чанк видалено: dispose job
                    if (_pendingMeshJobs.TryGetValue(coord, out var job))
                    {
                        job.Dispose();
                        _pendingMeshJobs.Remove(coord);
                    }
                    _pendingCachedMeshes.Remove(coord);
                    continue;
                }
                if (hasCenter && !_ctx.IsWithinKeepRadius(coord, center, keepRadius))
                {
                    // Out of range: dispose job
                    if (_pendingMeshJobs.TryGetValue(coord, out var job))
                    {
                        job.Dispose();
                        _pendingMeshJobs.Remove(coord);
                    }
                    _pendingCachedMeshes.Remove(coord);
                    continue;
                }

                if (_pendingCachedMeshes.TryGetValue(coord, out var cachedMesh))
                {
                    if (cachedMesh.Epoch != _ctx.StreamingEpoch)
                    {
                        _pendingCachedMeshes.Remove(coord);
                        _ctx.QueueRemesh(coord);
                        continue;
                    }

                    // Validate cached mesh before applying
                    if (cachedMesh.Mesh == null || cachedMesh.Mesh.vertexCount == 0)
                    {
                        _pendingCachedMeshes.Remove(coord);
                        if (HasAnySolid(chunk.Data))
                        {
                            // Invalid mesh for non-empty chunk - queue remesh
                            _ctx.QueueRemesh(coord);
                        }
                        else
                        {
                            // Empty chunk is valid: keep renderer/collider disabled
                            chunk.SetRendererEnabled(false);
                            chunk.SetColliderEnabled(false);
                        }
                        continue;
                    }
                    
                    // Re-validate hash matches current chunk data (only when all neighbors present)
                    var currentNeighbors = _ctx.Jobs.GatherNeighborCopies(coord);
                    bool hashStillValid = false;
                    if (_ctx.HasAllNeighbors(currentNeighbors.Data))
                    {
                        ulong currentHash = _ctx.Cache.ComputeMeshCacheHash(chunk.Data.Materials, chunk.Data.Size, currentNeighbors, chunk.LodStep, chunk.Data.Density);
                        hashStillValid = (currentHash == cachedMesh.Hash);
                    }
                    currentNeighbors.Dispose();

                    if (!hashStillValid)
                    {
                        _pendingCachedMeshes.Remove(coord);
                        _ctx.QueueRemesh(coord);
                        continue;
                    }

                    bool cachedApplyCollider = _ctx.AddColliders && !_preloaded.Contains(coord);
                    chunk.ApplySharedMesh(cachedMesh.Mesh, cachedApplyCollider);
                    if (_ctx.EnableEdgeOnlyRemesh)
                        _ctx.ReleaseFaceCacheForChunk(coord);
                    _ctx.RegisterMeshCacheForChunk(coord, cachedMesh.Hash, cachedMesh.Mesh, markShared: false, addCollider: cachedApplyCollider);
                    _pendingCachedMeshes.Remove(coord);
                    chunk.IsLowLod = false;
                    chunk.LodStartTime = 0;
                    chunk.LodStep = 1;
                    chunk.UsesSvo = false;

                    if (!_preloaded.Contains(coord) && cachedMesh.Mesh != null && cachedMesh.Mesh.vertexCount > 0)
                        chunk.SetRendererEnabled(true);
                    if (_preloaded.Contains(coord))
                    {
                        chunk.SetRendererEnabled(false);
                        chunk.SetColliderEnabled(false);
                    }

                    if (_meshedOnce.Add(coord))
                        _ctx.RebuildNeighbors(coord);

                    if (_ctx.WaitingSafeSpawnMesh && coord.Equals(_ctx.SafeSpawnAnchorCoord))
                    {
                        _ctx.SafeSpawn.SnapPlayerToSafeSpawn();
                        _ctx.SafeSpawn.SetPlayerFrozen(false);
                        _ctx.WaitingSafeSpawnMesh = false;
                    }

                    if (_ctx.RemeshAfterIntegration.Remove(coord))
                        _ctx.QueueRemesh(coord);

                    integrationsThisFrame++;
                    continue;
                }

                if (!_pendingMeshJobs.TryGetValue(coord, out var meshJob))
                    continue;

                if (meshJob.Epoch != _ctx.StreamingEpoch)
                {
                    meshJob.Dispose();
                    _pendingMeshJobs.Remove(coord);
                    _ctx.QueueRemesh(coord);
                    continue;
                }
                if (!meshJob.Handle.IsCompleted) continue;
                _pendingMeshJobs.Remove(coord);
                _pendingCachedMeshes.Remove(coord);
                meshJob.Handle.Complete();

                if (meshJob.MeshData.Vertices.Length == 0)
                {
                    meshJob.Dispose();
                    // Empty chunk is valid: keep renderer/collider disabled
                    if (!HasAnySolid(chunk.Data))
                    {
                        chunk.SetRendererEnabled(false);
                        chunk.SetColliderEnabled(false);
                        continue;
                    }
                    // Non-empty but no mesh? queue remesh
                    _ctx.QueueRemesh(coord);
                    continue;
                }

                bool applyCollider = _ctx.AddColliders && !_preloaded.Contains(coord);
                chunk.ApplyMesh(meshJob.MeshData, applyCollider);
                if (_ctx.EnableEdgeOnlyRemesh)
                    _ctx.ReleaseFaceCacheForChunk(coord);
                if (enableMeshCache && meshJob.LodStep <= 1 && meshJob.MaterialsHash != 0)
                {
                    Mesh renderMesh = chunk.GetRenderMesh();
                    _ctx.RegisterMeshCacheForChunk(coord, meshJob.MaterialsHash, renderMesh, markShared: true, addCollider: applyCollider);
                }

                chunk.IsLowLod = meshJob.LodStep > 1;
                chunk.LodStartTime = chunk.IsLowLod ? UnityEngine.Time.realtimeSinceStartupAsDouble : 0;
                chunk.LodStep = meshJob.LodStep;
                chunk.UsesSvo = false;

                Mesh checkMesh = chunk.GetRenderMesh();
                if (checkMesh == null || checkMesh.vertexCount == 0)
                {
                    meshJob.Dispose();
                    if (HasAnySolid(chunk.Data))
                        _ctx.QueueRemesh(coord);
                    else
                    {
                        chunk.SetRendererEnabled(false);
                        chunk.SetColliderEnabled(false);
                    }
                    continue;
                }
                if (!_preloaded.Contains(coord))
                    chunk.SetRendererEnabled(true);
                if (_preloaded.Contains(coord))
                {
                    chunk.SetRendererEnabled(false);
                    chunk.SetColliderEnabled(false);
                }

                if (_meshedOnce.Add(coord))
                    _ctx.RebuildNeighbors(coord);

                if (_ctx.WaitingSafeSpawnMesh && coord.Equals(_ctx.SafeSpawnAnchorCoord))
                {
                    _ctx.SafeSpawn.SnapPlayerToSafeSpawn();
                    _ctx.SafeSpawn.SetPlayerFrozen(false);
                    _ctx.WaitingSafeSpawnMesh = false;
                }

                meshJob.Dispose();
                if (_ctx.RemeshAfterIntegration.Remove(coord))
                    _ctx.QueueRemesh(coord);

                integrationsThisFrame++;
            }

            _ctx.IntegrationsLastFrame = integrationsThisFrame;
        }
    }
}
