using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Meshing;
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    internal sealed class ChunkWorkDropManager
    {
        readonly ChunkManager.Context _ctx;

        public ChunkWorkDropManager(ChunkManager.Context ctx)
        {
            _ctx = ctx;
        }

        int workDropDistance => _ctx.WorkDropDistance;
        float workDropAngleDeg => _ctx.WorkDropAngleDeg;
        float workDropMoveAngleDeg => _ctx.WorkDropMoveAngleDeg;
        float workDropCooldown => _ctx.WorkDropCooldown;
        int loadRadius => _ctx.LoadRadius;
        bool enablePreload => _ctx.EnablePreload;

        ChunkCoord _lastDropCenter { get => _ctx.LastDropCenter; set => _ctx.LastDropCenter = value; }
        bool _hasDropCenter { get => _ctx.HasDropCenter; set => _ctx.HasDropCenter = value; }
        Vector3 _lastDropForward { get => _ctx.LastDropForward; set => _ctx.LastDropForward = value; }
        bool _hasDropForward { get => _ctx.HasDropForward; set => _ctx.HasDropForward = value; }
        double _lastDropTime { get => _ctx.LastDropTime; set => _ctx.LastDropTime = value; }
        int _streamingEpoch { get => _ctx.StreamingEpoch; set => _ctx.StreamingEpoch = value; }

        Dictionary<ChunkCoord, Chunk> _active => _ctx.Active;
        Queue<ChunkCoord> _pending => _ctx.Pending;
        HashSet<ChunkCoord> _pendingSet => _ctx.PendingSet;
        Queue<ChunkCoord> _preload => _ctx.Preload;
        HashSet<ChunkCoord> _preloadSet => _ctx.PreloadSet;
        Queue<ChunkCoord> _removeQueue => _ctx.RemoveQueue;
        HashSet<ChunkCoord> _removeSet => _ctx.RemoveSet;
        Queue<ChunkCoord> _integrationQueue => _ctx.IntegrationQueue;
        HashSet<ChunkCoord> _integrationSet => _ctx.IntegrationSet;
        HashSet<ChunkCoord> _remeshSet => _ctx.RemeshSet;
        Queue<ChunkCoord> _faceRemeshQueue => _ctx.FaceRemeshQueue;
        HashSet<ChunkCoord> _faceRemeshSet => _ctx.FaceRemeshSet;
        Dictionary<ChunkCoord, ChunkMeshJobHandle> _pendingMeshJobs => _ctx.PendingMeshJobs;
        Dictionary<ChunkCoord, ChunkManager.FaceMeshTask> _faceMeshJobs => _ctx.FaceMeshJobs;
        Dictionary<ChunkCoord, ChunkManager.PendingCachedMesh> _pendingCachedMeshes => _ctx.PendingCachedMeshes;
        Dictionary<ChunkCoord, ChunkManager.GenTask> _genJobs => _ctx.GenJobs;
        Dictionary<ChunkCoord, ChunkManager.MeshTask> _meshJobs => _ctx.MeshJobs;
        Dictionary<ChunkCoord, ChunkManager.CachedChunkData> _dataCache => _ctx.DataCache;
        Dictionary<ulong, ChunkManager.CachedMeshEntry> _meshCache => _ctx.MeshCache;
        Dictionary<ChunkCoord, ulong> _chunkMeshHashes => _ctx.ChunkMeshHashes;
        Dictionary<ChunkCoord, int> _neighborDirtyFaces => _ctx.NeighborDirtyFaces;
        Dictionary<ChunkCoord, MeshData[]> _chunkFaceCache => _ctx.ChunkFaceCache;
        HashSet<ChunkCoord> _remeshAfterIntegration => _ctx.RemeshAfterIntegration;
        ChunkViewConePrioritizer viewCone => _ctx.ViewCone;
        Transform player => _ctx.Player;

        /// <summary>When player moved far (workDropDistance) or view angle changed (workDropAngleDeg) or move vs view (workDropMoveAngleDeg), drops queues after cooldown.</summary>
        internal void MaybeDropWork(ChunkCoord center)
        {
            if (workDropDistance <= 0 && workDropAngleDeg <= 0f)
            {
                _lastDropCenter = center;
                _hasDropCenter = true;
                _lastDropForward = ResolveViewForward();
                _hasDropForward = true;
                return;
            }

            bool drop = false;
            if (_hasDropCenter && workDropDistance > 0)
            {
                int dx = Mathf.Abs(center.X - _lastDropCenter.X);
                int dz = Mathf.Abs(center.Z - _lastDropCenter.Z);
                if (dx > workDropDistance || dz > workDropDistance)
                    drop = true;
            }

            Vector3 forward = ResolveViewForward();
            if (_hasDropForward && workDropAngleDeg > 0f)
            {
                float angle = Vector3.Angle(_lastDropForward, forward);
                if (angle >= workDropAngleDeg)
                    drop = true;
            }

            if (!drop && _hasDropCenter && workDropMoveAngleDeg > 0f)
            {
                Vector3 move = new Vector3(center.X - _lastDropCenter.X, 0f, center.Z - _lastDropCenter.Z);
                if (move.sqrMagnitude > 0.0001f)
                {
                    move.Normalize();
                    float moveAngle = Vector3.Angle(forward, move);
                    if (moveAngle >= workDropMoveAngleDeg)
                        drop = true;
                }
            }

            if (drop)
            {
                double now = Time.realtimeSinceStartupAsDouble;
                if (workDropCooldown <= 0f || now - _lastDropTime >= workDropCooldown)
                {
                    _lastDropTime = now;
                    _streamingEpoch++;
                    DropWorkQueues(center);
                }
            }

            _lastDropCenter = center;
            _hasDropCenter = true;
            if (forward.sqrMagnitude > 0.0001f)
            {
                _lastDropForward = forward;
                _hasDropForward = true;
            }
        }

        internal Vector3 ResolveViewForward()
        {
            Vector3 forward = Vector3.forward;
            if (Camera.main != null)
                forward = Camera.main.transform.forward;
            else if (player != null)
                forward = player.forward;

            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                forward = Vector3.forward;
            else
                forward.Normalize();
            return forward;
        }

        /// <summary>Clears or filters pending/preload/remove/integration queues; keeps only in-range remesh/mesh jobs and in-range pending/preload coords. MaintainRadius may still repopulate pending.</summary>
        internal void DropWorkQueues(ChunkCoord center)
        {
            int keepRadius = _ctx.EffectiveUnloadRadius();
            if (enablePreload)
                keepRadius = Mathf.Max(keepRadius, _ctx.EffectivePreloadRadius());

            var pendingKeep = _ctx.DropBufferPendingKeep;
            pendingKeep.Clear();
            foreach (var coord in _pendingSet)
            {
                if (_ctx.IsWithinLoadRadius(coord, center, loadRadius))
                    pendingKeep.Add(coord);
            }
            _pending.Clear();
            _pendingSet.Clear();
            for (int i = 0; i < pendingKeep.Count; i++)
                _pendingSet.Add(pendingKeep[i]);

            var preloadKeep = _ctx.DropBufferPreloadKeep;
            preloadKeep.Clear();
            foreach (var coord in _preloadSet)
            {
                if (_ctx.IsWithinLoadRadius(coord, center, _ctx.EffectivePreloadRadius()))
                    preloadKeep.Add(coord);
            }
            _preload.Clear();
            _preloadSet.Clear();
            for (int i = 0; i < preloadKeep.Count; i++)
            {
                var c = preloadKeep[i];
                _preloadSet.Add(c);
                _preload.Enqueue(c);
            }
            _removeQueue.Clear();
            _removeSet.Clear();
            _integrationQueue.Clear();
            lock (_ctx.IntegrationLock) { _integrationSet.Clear(); }

            var remeshKeep = _ctx.DropBufferRemeshKeep;
            remeshKeep.Clear();
            foreach (var coord in _remeshSet)
            {
                if (_active.ContainsKey(coord) && _ctx.IsWithinKeepRadius(coord, center, keepRadius))
                    remeshKeep.Add(coord);
            }
            _remeshSet.Clear();
            for (int i = 0; i < remeshKeep.Count; i++)
                _remeshSet.Add(remeshKeep[i]);

            var faceRemeshKeep = _ctx.DropBufferFaceRemeshKeep;
            faceRemeshKeep.Clear();
            foreach (var coord in _faceRemeshSet)
            {
                if (_active.ContainsKey(coord) && _ctx.IsWithinKeepRadius(coord, center, keepRadius))
                {
                    if (_neighborDirtyFaces.TryGetValue(coord, out int mask))
                        faceRemeshKeep.Add((coord, mask));
                }
                else
                {
                    _ctx.ReleaseFaceCacheForChunk(coord);
                    _neighborDirtyFaces.Remove(coord);
                }
            }
            _faceRemeshQueue.Clear();
            _faceRemeshSet.Clear();
            for (int i = 0; i < faceRemeshKeep.Count; i++)
            {
                var (coord, mask) = faceRemeshKeep[i];
                _faceRemeshSet.Add(coord);
                _neighborDirtyFaces[coord] = mask;
                _faceRemeshQueue.Enqueue(coord);
            }

            var faceMeshStale = _ctx.DropBufferFaceMeshStale;
            faceMeshStale.Clear();
            foreach (var kvp in _faceMeshJobs)
            {
                if (!_active.ContainsKey(kvp.Key) || !_ctx.IsWithinKeepRadius(kvp.Key, center, keepRadius))
                {
                    kvp.Value.Job.Handle.Complete();
                    kvp.Value.Job.Dispose();
                    faceMeshStale.Add(kvp.Key);
                }
            }
            for (int i = 0; i < faceMeshStale.Count; i++)
                _faceMeshJobs.Remove(faceMeshStale[i]);

            var stale = _ctx.DropBufferStale;
            stale.Clear();
            foreach (var kvp in _pendingMeshJobs)
            {
                if (!_active.ContainsKey(kvp.Key) || !_ctx.IsWithinKeepRadius(kvp.Key, center, keepRadius))
                {
                    kvp.Value.Dispose();
                    stale.Add(kvp.Key);
                }
                else
                {
                    lock (_ctx.IntegrationLock)
                    {
                        if (!_integrationSet.Contains(kvp.Key))
                        {
                            _integrationQueue.Enqueue(kvp.Key);
                            _integrationSet.Add(kvp.Key);
                        }
                    }
                }
            }
            for (int i = 0; i < stale.Count; i++)
                _pendingMeshJobs.Remove(stale[i]);

            var cachedStale = _ctx.DropBufferCachedStale;
            cachedStale.Clear();
            foreach (var kvp in _pendingCachedMeshes)
            {
                if (!_active.ContainsKey(kvp.Key) || !_ctx.IsWithinKeepRadius(kvp.Key, center, keepRadius))
                    cachedStale.Add(kvp.Key);
                else
                {
                    lock (_ctx.IntegrationLock)
                    {
                        if (!_integrationSet.Contains(kvp.Key))
                        {
                            _integrationQueue.Enqueue(kvp.Key);
                            _integrationSet.Add(kvp.Key);
                        }
                    }
                }
            }
            for (int i = 0; i < cachedStale.Count; i++)
                _pendingCachedMeshes.Remove(cachedStale[i]);

            var remeshAfter = _ctx.DropBufferRemeshAfter;
            remeshAfter.Clear();
            foreach (var coord in _remeshAfterIntegration)
            {
                if (_active.ContainsKey(coord) && _ctx.IsWithinKeepRadius(coord, center, keepRadius))
                    remeshAfter.Add(coord);
            }
            _remeshAfterIntegration.Clear();
            for (int i = 0; i < remeshAfter.Count; i++)
                _remeshAfterIntegration.Add(remeshAfter[i]);

            // Drop active chunks outside keep radius (queue removal)
            foreach (var kvp in _active)
            {
                if (_ctx.IsWithinKeepRadius(kvp.Key, center, keepRadius)) continue;
                if (_removeSet.Add(kvp.Key))
                    _removeQueue.Enqueue(kvp.Key);
            }

            if (viewCone != null && viewCone.Enabled)
                viewCone.Clear();
        }
    }
}
