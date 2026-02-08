using System;
using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Meshing;
using TerraVoxel.Voxel.Save;
using Unity.Collections;
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>Data and mesh cache for chunks; hash/register/release/evict. Used when EnableDataCache/EnableMeshCache; GPU path uses GpuWorldState for mesh. DataCache/MeshCache are main-thread only (no lock); if Burst jobs ever read, add synchronization.</summary>
    internal sealed class ChunkCacheManager
    {
        readonly ChunkManager.Context _ctx;

        public ChunkCacheManager(ChunkManager.Context ctx)
        {
            _ctx = ctx;
        }

        Dictionary<ChunkCoord, ChunkManager.CachedChunkData> _dataCache => _ctx.DataCache;
        Dictionary<ulong, ChunkManager.CachedMeshEntry> _meshCache => _ctx.MeshCache;
        Dictionary<ChunkCoord, ulong> _chunkMeshHashes => _ctx.ChunkMeshHashes;
        Dictionary<ChunkCoord, ChunkManager.PendingCachedMesh> _pendingCachedMeshes => _ctx.PendingCachedMeshes;
        Dictionary<ChunkCoord, Chunk> _active => _ctx.Active;
        Dictionary<ChunkCoord, MeshData[]> _chunkFaceCache => _ctx.ChunkFaceCache;
        System.Collections.Concurrent.ConcurrentDictionary<ChunkCoord, byte> _integrationSet => _ctx.IntegrationSet;
        System.Collections.Concurrent.ConcurrentQueue<ChunkCoord> _integrationQueue => _ctx.IntegrationQueue;

        List<(ulong hash, int vertexCount, int frame)> _evictCandidates = new List<(ulong, int, int)>();

        int _cacheOpsThisFrame { get => _ctx.CacheOpsThisFrame; set => _ctx.CacheOpsThisFrame = value; }

        bool enableDataCache => _ctx.EnableDataCache;
        int maxCachedChunks => _ctx.MaxCachedChunks;
        int maxCacheOpsPerFrame => _ctx.MaxCacheOpsPerFrame;
        long memoryPressureThresholdMb => _ctx.MemoryPressureThresholdMb;

        bool enableMeshCache => _ctx.EnableMeshCache;
        int maxMeshCacheEntries => _ctx.MaxMeshCacheEntries;
        int meshCacheEvictPerFrame => _ctx.MeshCacheEvictPerFrame;

        ChunkModManager modManager => _ctx.ModManager;

        internal void CacheChunkData(ChunkCoord coord, ChunkData data)
        {
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

            // Evict in FIFO order (oldest first) using eviction list; O(1) per eviction.
            while (_dataCache.Count >= cacheCap && _ctx.TryDequeueDataCacheEviction(out var evictCoord))
            {
                if (_dataCache.TryGetValue(evictCoord, out var oldCached))
                {
                    oldCached.Dispose();
                    _dataCache.Remove(evictCoord);
                }
            }

            var cached = new ChunkManager.CachedChunkData();
            cached.CopyFrom(data);
            _dataCache[coord] = cached;
            _ctx.AddDataCacheEviction(coord);
            _cacheOpsThisFrame++;
        }

        internal bool TryLoadFromCache(ChunkCoord coord, ChunkData data)
        {
            if (!enableDataCache) return false;
            if (!_dataCache.TryGetValue(coord, out var cached)) return false;
            if (!cached.IsValid) return false;
            // Invalidate cache if chunk was modified (mod/save) after being cached
            if (modManager != null && modManager.GetDeltaCount(coord) > 0)
            {
                cached.Dispose();
                _dataCache.Remove(coord);
                _ctx.RemoveDataCacheEviction(coord);
                return false;
            }

            cached.CopyTo(data);
            return true;
        }

        internal ulong ComputeMeshCacheHash(NativeArray<ushort> materials, int size, NeighborDataBuffers neighbors, int lodStep = 1, NativeArray<float> density = default)
        {
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
                    hash ^= (ulong)(uint)BitConverter.SingleToInt32Bits(density[i]);
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

        internal bool TryQueueCachedMesh(ChunkCoord coord, ulong hash, Mesh mesh)
        {
            // Validate mesh before queuing - must have vertices
            if (mesh == null || mesh.vertexCount == 0)
                return false;
            // Validate that chunk still exists and has data
            if (!_active.TryGetValue(coord, out var chunk) || !chunk.Data.IsCreated)
                return false;

            if (!_integrationSet.TryAdd(coord, 0))
                return false;
            _pendingCachedMeshes[coord] = new ChunkManager.PendingCachedMesh
            {
                Mesh = mesh,
                Hash = hash,
                Epoch = _ctx.StreamingEpoch
            };
            _integrationQueue.Enqueue(coord);
            return true;
        }

        internal void RegisterMeshCacheForChunk(ChunkCoord coord, ulong hash, Mesh mesh, bool markShared, bool addCollider)
        {
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
                    _meshCache[hash] = new ChunkManager.CachedMeshEntry
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

        internal void ReleaseMeshCacheForChunk(ChunkCoord coord)
        {
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

        internal void EvictMeshCacheIfNeeded()
        {
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

            // One pass: collect evictable (RefCount==0), sort by size desc then LRU (frame asc), evict up to budget. O(n) + O(k log k) vs O(budget * n).
            _evictCandidates.Clear();
            foreach (var kvp in _meshCache)
            {
                if (kvp.Value.RefCount > 0) continue;
                int vc = kvp.Value.Mesh != null ? kvp.Value.Mesh.vertexCount : 0;
                _evictCandidates.Add((kvp.Key, vc, kvp.Value.LastUsedFrame));
            }
            _evictCandidates.Sort((a, b) =>
            {
                int cmp = b.vertexCount.CompareTo(a.vertexCount);
                if (cmp != 0) return cmp;
                return a.frame.CompareTo(b.frame);
            });

            int evicted = 0;
            for (int i = 0; i < _evictCandidates.Count && _meshCache.Count > maxMeshCacheEntries && evicted < evictBudget; i++)
            {
                RemoveMeshCacheEntry(_evictCandidates[i].hash);
                evicted++;
            }
        }

        void RemoveMeshCacheEntry(ulong hash)
        {
            if (_meshCache.TryGetValue(hash, out var entry))
            {
                if (entry.Mesh != null)
                    UnityEngine.Object.Destroy(entry.Mesh);
                _meshCache.Remove(hash);
            }
        }

        /// <summary>Releases face cache for coord. MeshData.Dispose checks Vertices, Triangles, Normals, Colors IsCreated.</summary>
        internal void ReleaseFaceCacheForChunk(ChunkCoord coord)
        {
            if (!_chunkFaceCache.TryGetValue(coord, out var arr)) return;
            _chunkFaceCache.Remove(coord);
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i].Vertices.IsCreated)
                    arr[i].Dispose();
            }
        }
    }
}
