using TerraVoxel.Voxel.Core;
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>Partial: neighbor invalidation, remesh queue, layer, TryGetChunk, RequestRemesh. All access to _active, _genJobs, _remeshSet, etc. is main-thread only; no lock.</summary>
    public partial class ChunkManager
    {
        /// <summary>Returns true if chunk at coord is active and not currently generating. Main-thread only.</summary>
        public bool TryGetChunk(ChunkCoord coord, out Chunk chunk)
        {
            if (!_active.TryGetValue(coord, out chunk)) return false;
            if (chunk == null) return false;
            if (_genJobs.ContainsKey(coord))
            {
                chunk = null;
                return false;
            }
            return true;
        }

        /// <summary>Enqueue coord for remesh and optionally neighbors. _requestRemeshDepth guards re-entrancy; bounded by maxRequestRemeshNeighborsDepth.</summary>
        public void RequestRemesh(ChunkCoord coord, bool includeNeighbors)
        {
            QueueRemesh(coord);
            if (!includeNeighbors || _requestRemeshDepth >= maxRequestRemeshNeighborsDepth) return;
            int columnChunks = ColumnChunks;
            if (columnChunks <= 0) return;
            _requestRemeshDepth++;
            try
            {
                var n = new ChunkCoord(coord.X + 1, coord.Y, coord.Z);
                if (n.Y >= 0 && n.Y < columnChunks) QueueRemesh(n);
                n = new ChunkCoord(coord.X - 1, coord.Y, coord.Z);
                if (n.Y >= 0 && n.Y < columnChunks) QueueRemesh(n);
                n = new ChunkCoord(coord.X, coord.Y + 1, coord.Z);
                if (n.Y >= 0 && n.Y < columnChunks) QueueRemesh(n);
                n = new ChunkCoord(coord.X, coord.Y - 1, coord.Z);
                if (n.Y >= 0 && n.Y < columnChunks) QueueRemesh(n);
                n = new ChunkCoord(coord.X, coord.Y, coord.Z + 1);
                if (n.Y >= 0 && n.Y < columnChunks) QueueRemesh(n);
                n = new ChunkCoord(coord.X, coord.Y, coord.Z - 1);
                if (n.Y >= 0 && n.Y < columnChunks) QueueRemesh(n);
            }
            finally { _requestRemeshDepth--; }
        }

        const int MaxLayerRecursionDepth = 32;

        void ApplyChunkLayer(Chunk chunk)
        {
            if (chunk == null) return;
            if (string.IsNullOrWhiteSpace(chunkLayerName)) return;
            int layer = LayerMask.NameToLayer(chunkLayerName);
            if (layer < 0)
            {
                if (!_warnedInvalidChunkLayer)
                {
                    _warnedInvalidChunkLayer = true;
                    Debug.LogWarning($"[ChunkManager] Chunk layer '{chunkLayerName}' not found. Create the layer in Tags and Layers or use a valid name.");
                }
                return;
            }
            SetLayerRecursively(chunk.transform, layer, 0);
        }

        static void SetLayerRecursively(Transform t, int layer, int depth)
        {
            if (t == null) return;
            if (depth > MaxLayerRecursionDepth) return;
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++)
                SetLayerRecursively(t.GetChild(i), layer, depth + 1);
        }

        /// <summary>Rebuild neighbor invalidation/remesh. _rebuildNeighborsDepth guards re-entrancy; main-thread only.</summary>
        void RebuildNeighbors(ChunkCoord coord)
        {
            if (_rebuildNeighborsDepth >= maxRebuildNeighborsDepth) return;
            _rebuildNeighborsDepth++;
            try
            {
                RebuildNeighborsInner(coord);
            }
            finally { _rebuildNeighborsDepth--; }
        }

        void RebuildNeighborsInner(ChunkCoord coord)
        {
            if (worldGen == null) return;
            int columnChunks = worldGen.ColumnChunks;
            if (columnChunks <= 0) return;
            var neighbors = new (ChunkCoord coord, int faceIndex)[]
            {
                (new ChunkCoord(coord.X + 1, coord.Y, coord.Z), 0),
                (new ChunkCoord(coord.X - 1, coord.Y, coord.Z), 1),
                (new ChunkCoord(coord.X, coord.Y + 1, coord.Z), 2),
                (new ChunkCoord(coord.X, coord.Y - 1, coord.Z), 3),
                (new ChunkCoord(coord.X, coord.Y, coord.Z + 1), 4),
                (new ChunkCoord(coord.X, coord.Y, coord.Z - 1), 5)
            };

            foreach (var (neighbor, faceIndex) in neighbors)
            {
                if (neighbor.Y < 0 || neighbor.Y >= columnChunks) continue;
                if (!_active.ContainsKey(neighbor)) continue;
                if (IsChunkGenerating(neighbor)) continue;
                if (_meshJobs.ContainsKey(neighbor)) continue;
                if (IsInIntegrationSet(neighbor)) continue;
                if (_remeshSet.Contains(neighbor)) continue;

                if (enableMeshCache)
                {
                    ReleaseMeshCacheForChunk(neighbor);
                    if (_pendingCachedMeshes.ContainsKey(neighbor))
                    {
                        _pendingCachedMeshes.Remove(neighbor);
                        _integrationSet.TryRemove(neighbor, out _);
                    }
                }

                if (enableEdgeOnlyRemesh)
                    InvalidateNeighborFace(neighbor, faceIndex);
                else
                    QueueRemesh(neighbor);
            }
        }

        /// <summary>Mark neighbor face for remesh. _faceRemeshSet and _faceRemeshQueue kept in sync (Set for Contains, Queue for FIFO). Main-thread only.</summary>
        void InvalidateNeighborFace(ChunkCoord neighbor, int faceIndex)
        {
            if (_meshJobs.ContainsKey(neighbor)) return;
            if (_faceMeshJobs.ContainsKey(neighbor)) return;
            if (_faceRemeshSet.Contains(neighbor))
            {
                _neighborDirtyFaces.TryGetValue(neighbor, out int existing);
                _neighborDirtyFaces[neighbor] = existing | (1 << faceIndex);
                return;
            }
            _neighborDirtyFaces[neighbor] = 1 << faceIndex;
            _faceRemeshSet.Add(neighbor);
            _faceRemeshQueue.Enqueue(neighbor);
        }

        void QueueRemesh(ChunkCoord coord)
        {
            if (!_active.ContainsKey(coord)) return;
            if (_meshJobs.ContainsKey(coord)) return;
            if (_remeshSet.Contains(coord)) return;
            if (IsInIntegrationSet(coord) || _pendingCachedMeshes.ContainsKey(coord))
            {
                _remeshAfterIntegration.Add(coord);
                return;
            }

            if (_active.TryGetValue(coord, out var chunk) && chunk != null && chunk.Data.IsCreated && !chunk.UsesSvo)
            {
                int lodStep = Mathf.Max(1, chunk.LodStep);
                if (lodStep == 1 && _chunkMeshHashes.TryGetValue(coord, out ulong storedHash))
                {
                    var neighbors = GatherNeighborCopies(coord);
                    try
                    {
                        if (HasAllNeighbors(neighbors.Data))
                        {
                            ulong currentHash = ComputeMeshCacheHash(chunk.Data.Materials, chunk.Data.Size, neighbors, 1, chunk.Data.Density);
                            if (currentHash == storedHash)
                                return;
                        }
                    }
                    finally { neighbors.Dispose(); }
                }
            }

            if (enableEdgeOnlyRemesh)
            {
                ReleaseFaceCacheForChunk(coord);
                _neighborDirtyFaces.Remove(coord);
                _faceRemeshSet.Remove(coord);
            }
            if (svoManager != null)
                svoManager.ReleaseForChunk(coord);
            if (enableMeshCache)
            {
                ReleaseMeshCacheForChunk(coord);
                int colChunks = worldGen != null ? worldGen.ColumnChunks : 1;
                var n = new[] {
                    new ChunkCoord(coord.X + 1, coord.Y, coord.Z), new ChunkCoord(coord.X - 1, coord.Y, coord.Z),
                    new ChunkCoord(coord.X, coord.Y + 1, coord.Z), new ChunkCoord(coord.X, coord.Y - 1, coord.Z),
                    new ChunkCoord(coord.X, coord.Y, coord.Z + 1), new ChunkCoord(coord.X, coord.Y, coord.Z - 1)
                };
                for (int i = 0; i < 6; i++)
                {
                    if (n[i].Y >= 0 && n[i].Y < colChunks)
                        ReleaseMeshCacheForChunk(n[i]);
                }
            }
            _remeshSet.Add(coord);
        }

        /// <summary>Dequeue coord in _remeshSet with minimum XZ distance to center (Y ignored for remesh priority). O(n) over _remeshSet; for very large sets consider a min-heap. Main-thread only.</summary>
        bool TryDequeueClosestRemesh(ChunkCoord center, out ChunkCoord coord)
        {
            coord = default;
            if (_remeshSet.Count == 0) return false;

            ChunkCoord best = default;
            int bestDistSq = int.MaxValue;
            foreach (var c in _remeshSet)
            {
                int dx = c.X - center.X;
                int dz = c.Z - center.Z;
                int d = dx * dx + dz * dz;
                if (d < bestDistSq)
                {
                    bestDistSq = d;
                    best = c;
                }
            }
            _remeshSet.Remove(best);
            coord = best;
            return true;
        }
    }
}
