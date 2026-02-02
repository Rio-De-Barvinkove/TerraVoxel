using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Meshing;
using Unity.Collections;
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>Partial: data cache, mesh cache hash/register/release/evict, TryLoadFromCache, ReleaseFaceCacheForChunk.</summary>
    public partial class ChunkManager
    {
        void CacheChunkData(ChunkCoord coord, ChunkData data)
        {
            if (_cache != null)
            {
                _cache.CacheChunkData(coord, data);
                return;
            }
            if (!enableDataCache) return;
            if (maxCachedChunks <= 0) return;
            if (maxCacheOpsPerFrame > 0 && _cacheOpsThisFrame >= maxCacheOpsPerFrame) return;

            int cacheCap = maxCachedChunks;
            if (memoryPressureThresholdMb > 0)
            {
#if UNITY_EDITOR || true
                long memMb = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024);
                if (memMb > memoryPressureThresholdMb)
                    cacheCap = Mathf.Max(1, maxCachedChunks / 2);
#endif
            }

            while (_dataCache.Count >= cacheCap && _dataCache.Count > 0)
            {
                var first = default(ChunkCoord);
                foreach (var key in _dataCache.Keys)
                {
                    first = key;
                    break;
                }
                if (_dataCache.TryGetValue(first, out var oldCached))
                {
                    oldCached.Dispose();
                    _dataCache.Remove(first);
                }
            }

            var cached = new CachedChunkData();
            cached.CopyFrom(data);
            _dataCache[coord] = cached;
            _cacheOpsThisFrame++;
        }

        ulong ComputeMeshCacheHash(NativeArray<ushort> materials, int size, NeighborDataBuffers neighbors, int lodStep = 1, NativeArray<float> density = default)
        {
            if (_cache != null)
                return _cache.ComputeMeshCacheHash(materials, size, neighbors, lodStep, density);
            if (!materials.IsCreated || materials.Length == 0) return 0ul;
            ulong hash = 1469598103934665603ul;
            HashArray(materials, ref hash);

            var data = neighbors.Data;
            if (data.HasNegX) HashNeighborFace(neighbors.NegX, size, 0, size - 1, ref hash);
            if (data.HasPosX) HashNeighborFace(neighbors.PosX, size, 0, 0, ref hash);
            if (data.HasNegY) HashNeighborFace(neighbors.NegY, size, 1, size - 1, ref hash);
            if (data.HasPosY) HashNeighborFace(neighbors.PosY, size, 1, 0, ref hash);
            if (data.HasNegZ) HashNeighborFace(neighbors.NegZ, size, 2, size - 1, ref hash);
            if (data.HasPosZ) HashNeighborFace(neighbors.PosZ, size, 2, 0, ref hash);

            hash ^= (ulong)materials.Length;
            hash ^= (ulong)lodStep;
            hash *= 1099511628211ul;
            if (density.IsCreated && density.Length == materials.Length)
            {
                for (int i = 0; i < density.Length; i++)
                {
                    hash ^= (ulong)(density[i] * 0xFFFFFF);
                    hash *= 1099511628211ul;
                }
            }
            return hash;
        }

        void HashArray(NativeArray<ushort> data, ref ulong hash)
        {
            if (!data.IsCreated || data.Length == 0) return;
            for (int i = 0; i < data.Length; i++)
            {
                hash ^= data[i];
                hash *= 1099511628211ul;
            }
        }

        void HashNeighborFace(NativeArray<ushort> data, int size, int axis, int index, ref ulong hash)
        {
            if (!data.IsCreated || data.Length == 0) return;
            if (axis == 0)
            {
                for (int z = 0; z < size; z++)
                {
                    int zBase = size * size * z;
                    for (int y = 0; y < size; y++)
                    {
                        int idx = index + size * y + zBase;
                        hash ^= data[idx];
                        hash *= 1099511628211ul;
                    }
                }
                return;
            }
            if (axis == 1)
            {
                int yBase = size * index;
                for (int z = 0; z < size; z++)
                {
                    int zBase = size * size * z;
                    for (int x = 0; x < size; x++)
                    {
                        int idx = x + yBase + zBase;
                        hash ^= data[idx];
                        hash *= 1099511628211ul;
                    }
                }
                return;
            }

            int zIndexBase = size * size * index;
            for (int y = 0; y < size; y++)
            {
                int yBase = size * y + zIndexBase;
                for (int x = 0; x < size; x++)
                {
                    int idx = x + yBase;
                    hash ^= data[idx];
                    hash *= 1099511628211ul;
                }
            }
        }

        bool HasAllNeighbors(GreedyMesher.NeighborData data)
        {
            return data.HasNegX && data.HasPosX
                && data.HasNegY && data.HasPosY
                && data.HasNegZ && data.HasPosZ;
        }

        bool TryQueueCachedMesh(ChunkCoord coord, ulong hash, Mesh mesh)
        {
            if (_cache != null)
                return _cache.TryQueueCachedMesh(coord, hash, mesh);
            if (mesh == null || mesh.vertexCount == 0)
                return false;
            if (!_active.TryGetValue(coord, out var chunk) || !chunk.Data.IsCreated)
                return false;

            if (!_integrationSet.TryAdd(coord, 0))
                return false;
            _pendingCachedMeshes[coord] = new PendingCachedMesh
            {
                Mesh = mesh,
                Hash = hash,
                Epoch = _streamingEpoch
            };
            _integrationQueue.Enqueue(coord);
            return true;
        }

        void RegisterMeshCacheForChunk(ChunkCoord coord, ulong hash, Mesh mesh, bool markShared, bool addCollider)
        {
            if (_cache != null)
            {
                _cache.RegisterMeshCacheForChunk(coord, hash, mesh, markShared, addCollider);
                return;
            }
            if (mesh == null) return;

            if (_chunkMeshHashes.TryGetValue(coord, out var oldHash))
            {
                if (oldHash == hash)
                {
                    if (enableMeshCache && maxMeshCacheEntries > 0 && _meshCache.TryGetValue(hash, out var sameEntry))
                    {
                        sameEntry.LastUsedFrame = Time.frameCount;
                        _meshCache[hash] = sameEntry;
                    }
                    if (markShared && _active.TryGetValue(coord, out var sameChunk))
                        sameChunk.ApplySharedMesh(mesh, addCollider);
                    return;
                }
                ReleaseMeshCacheForChunk(coord);
            }

            _chunkMeshHashes[coord] = hash;

            if (enableMeshCache && maxMeshCacheEntries > 0)
            {
                if (_meshCache.TryGetValue(hash, out var entry))
                {
                    entry.RefCount++;
                    entry.LastUsedFrame = Time.frameCount;
                    _meshCache[hash] = entry;
                }
                else
                {
                    _meshCache[hash] = new CachedMeshEntry
                    {
                        Mesh = mesh,
                        RefCount = 1,
                        LastUsedFrame = Time.frameCount
                    };
                }
                EvictMeshCacheIfNeeded();
            }

            if (markShared && _active.TryGetValue(coord, out var chunk))
                chunk.ApplySharedMesh(mesh, addCollider);
        }

        void ReleaseMeshCacheForChunk(ChunkCoord coord)
        {
            if (_cache != null)
            {
                _cache.ReleaseMeshCacheForChunk(coord);
                return;
            }
            if (!_chunkMeshHashes.TryGetValue(coord, out var hash)) return;
            _chunkMeshHashes.Remove(coord);

            if (_meshCache.TryGetValue(hash, out var entry))
            {
                entry.RefCount = Mathf.Max(0, entry.RefCount - 1);
                _meshCache[hash] = entry;
                if (entry.RefCount == 0 && _meshCache.Count > maxMeshCacheEntries)
                    EvictMeshCacheIfNeeded();
            }
        }

        void EvictMeshCacheIfNeeded()
        {
            if (_cache != null)
            {
                _cache.EvictMeshCacheIfNeeded();
                return;
            }
            if (maxMeshCacheEntries <= 0 || _meshCache.Count <= maxMeshCacheEntries) return;
            int evictBudget = meshCacheEvictPerFrame > 0 ? meshCacheEvictPerFrame : int.MaxValue;
            if (memoryPressureThresholdMb > 0)
            {
#if UNITY_EDITOR || true
                long memMb = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024);
                if (memMb > memoryPressureThresholdMb)
                    evictBudget *= 2;
#endif
            }

            while (_meshCache.Count > maxMeshCacheEntries && evictBudget-- > 0)
            {
                bool found = false;
                ulong bestKey = 0;
                int bestFrame = int.MaxValue;
                int bestVertexCount = -1;

                foreach (var kvp in _meshCache)
                {
                    if (kvp.Value.RefCount > 0) continue;
                    int vertexCount = kvp.Value.Mesh != null ? kvp.Value.Mesh.vertexCount : 0;
                    bool better = !found ||
                        vertexCount > bestVertexCount ||
                        (vertexCount == bestVertexCount && kvp.Value.LastUsedFrame < bestFrame);
                    if (better)
                    {
                        found = true;
                        bestKey = kvp.Key;
                        bestFrame = kvp.Value.LastUsedFrame;
                        bestVertexCount = vertexCount;
                    }
                }

                if (!found) break;
                RemoveMeshCacheEntry(bestKey);
            }
        }

        void RemoveMeshCacheEntry(ulong hash)
        {
            if (_meshCache.TryGetValue(hash, out var entry))
            {
                if (entry.Mesh != null)
                    Destroy(entry.Mesh);
                _meshCache.Remove(hash);
            }
        }

        bool TryLoadFromCache(ChunkCoord coord, ChunkData data)
        {
            if (_cache != null)
                return _cache.TryLoadFromCache(coord, data);
            if (!enableDataCache) return false;
            if (!_dataCache.TryGetValue(coord, out var cached)) return false;
            if (!cached.IsValid) return false;
            if (modManager != null && modManager.GetDeltaCount(coord) > 0)
            {
                cached.Dispose();
                _dataCache.Remove(coord);
                return false;
            }

            cached.CopyTo(data);
            return true;
        }

        void ReleaseFaceCacheForChunk(ChunkCoord coord)
        {
            if (_cache != null)
            {
                _cache.ReleaseFaceCacheForChunk(coord);
                return;
            }
            if (!_chunkFaceCache.TryGetValue(coord, out var arr)) return;
            _chunkFaceCache.Remove(coord);
            if (arr != null)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    if (arr[i].Vertices.IsCreated)
                        arr[i].Dispose();
                }
            }
        }
    }
}
