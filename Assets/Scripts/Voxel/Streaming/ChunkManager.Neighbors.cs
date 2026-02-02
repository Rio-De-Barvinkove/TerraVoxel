using TerraVoxel.Voxel.Core;
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>Partial: neighbor invalidation, remesh queue, layer, TryGetChunk, RequestRemesh.</summary>
    public partial class ChunkManager
    {
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

        void ApplyChunkLayer(Chunk chunk)
        {
            if (chunk == null) return;
            if (string.IsNullOrWhiteSpace(chunkLayerName)) return;
            int layer = LayerMask.NameToLayer(chunkLayerName);
            if (layer < 0) return;
            SetLayerRecursively(chunk.transform, layer);
        }

        static void SetLayerRecursively(Transform t, int layer)
        {
            if (t == null) return;
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++)
                SetLayerRecursively(t.GetChild(i), layer);
        }

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
            int columnChunks = worldGen != null ? worldGen.ColumnChunks : 4;
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
                        lock (_integrationLock)
                        {
                            if (_integrationSet.Contains(neighbor))
                                _integrationSet.Remove(neighbor);
                        }
                    }
                }

                if (enableEdgeOnlyRemesh)
                    InvalidateNeighborFace(neighbor, faceIndex);
                else
                    QueueRemesh(neighbor);
            }
        }

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

            if (_active.TryGetValue(coord, out var chunk) && chunk.Data.IsCreated && !chunk.UsesSvo)
            {
                int lodStep = Mathf.Max(1, chunk.LodStep);
                if (lodStep == 1 && _chunkMeshHashes.TryGetValue(coord, out ulong storedHash))
                {
                    var neighbors = GatherNeighborCopies(coord);
                    if (HasAllNeighbors(neighbors.Data))
                    {
                        ulong currentHash = ComputeMeshCacheHash(chunk.Data.Materials, chunk.Data.Size, neighbors, 1, chunk.Data.Density);
                        neighbors.Dispose();
                        if (currentHash == storedHash)
                            return;
                    }
                    else
                    {
                        neighbors.Dispose();
                    }
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
                var n = new[] {
                    new ChunkCoord(coord.X + 1, coord.Y, coord.Z), new ChunkCoord(coord.X - 1, coord.Y, coord.Z),
                    new ChunkCoord(coord.X, coord.Y + 1, coord.Z), new ChunkCoord(coord.X, coord.Y - 1, coord.Z),
                    new ChunkCoord(coord.X, coord.Y, coord.Z + 1), new ChunkCoord(coord.X, coord.Y, coord.Z - 1)
                };
                for (int i = 0; i < 6; i++)
                    ReleaseMeshCacheForChunk(n[i]);
            }
            _remeshSet.Add(coord);
        }

        /// <summary>Extract coord with minimum distance to center from _remeshSet (min-heap semantics).</summary>
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
