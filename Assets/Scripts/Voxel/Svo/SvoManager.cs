using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using Unity.Collections;
using UnityEngine;

namespace TerraVoxel.Voxel.Svo
{
    [DisallowMultipleComponent]
    public class SvoManager : MonoBehaviour
    {
        [SerializeField] bool enableSvo = true;
        [SerializeField] int maxCacheEntries = 256;
        [SerializeField] int evictPerFrame = 4;
        [Tooltip("Not implemented; when true, TryGetOrBuildMesh returns false.")]
        [SerializeField] bool useGpuRaymarch = false;

        readonly Dictionary<ulong, SvoMeshEntry> _cache = new Dictionary<ulong, SvoMeshEntry>();
        readonly Dictionary<ChunkCoord, ulong> _chunkHashes = new Dictionary<ChunkCoord, ulong>();
        readonly object _cacheLock = new object();

        struct SvoMeshEntry
        {
            public Mesh Mesh;
            public int RefCount;
            public int LastUsedFrame;
            public int UseCount;
        }

        public bool TryGetOrBuildMesh(ChunkCoord coord, ChunkData data, int leafSize, byte maxMaterialIndex, byte fallbackMaterialIndex, out Mesh mesh)
        {
            mesh = null;
            if (!enableSvo) return false;
            if (!data.Materials.IsCreated || data.Materials.Length == 0) return false;

            if (useGpuRaymarch)
                return false;

            ulong hash = ComputeHash(data, leafSize, 0);
            lock (_cacheLock)
            {
                if (_chunkHashes.TryGetValue(coord, out var oldHash) && oldHash != hash)
                    ReleaseForChunkInner(coord);

                if (_cache.TryGetValue(hash, out var entry) && entry.Mesh != null)
                {
                    entry.RefCount++;
                    entry.LastUsedFrame = Time.frameCount;
                    entry.UseCount++;
                    _cache[hash] = entry;
                    _chunkHashes[coord] = hash;
                    mesh = entry.Mesh;
                    return true;
                }
            }

            var volume = SvoBuilder.Build(data, leafSize);
            try
            {
                var builtMesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                SvoMeshBuilder.BuildMesh(volume, builtMesh, maxMaterialIndex, fallbackMaterialIndex);

                lock (_cacheLock)
                {
                    _cache[hash] = new SvoMeshEntry
                    {
                        Mesh = builtMesh,
                        RefCount = 1,
                        LastUsedFrame = Time.frameCount,
                        UseCount = 1
                    };
                    _chunkHashes[coord] = hash;
                    EvictIfNeededInner();
                }
                mesh = builtMesh;
                return true;
            }
            finally
            {
                volume.Dispose();
            }
        }

        public void ReleaseForChunk(ChunkCoord coord)
        {
            lock (_cacheLock)
            {
                ReleaseForChunkInner(coord);
            }
        }

        void ReleaseForChunkInner(ChunkCoord coord)
        {
            if (!_chunkHashes.TryGetValue(coord, out var hash)) return;
            _chunkHashes.Remove(coord);

            if (_cache.TryGetValue(hash, out var entry))
            {
                entry.RefCount = Mathf.Max(0, entry.RefCount - 1);
                _cache[hash] = entry;
                if (entry.RefCount == 0 && _cache.Count > maxCacheEntries)
                    EvictIfNeededInner();
            }
        }

        /// <summary>LRU-style eviction (UseCount, LastUsedFrame). May be suboptimal for highly dynamic scenes.</summary>
        void EvictIfNeeded()
        {
            lock (_cacheLock)
            {
                EvictIfNeededInner();
            }
        }

        void EvictIfNeededInner()
        {
            if (maxCacheEntries <= 0 || _cache.Count <= maxCacheEntries) return;
            int budget = evictPerFrame > 0 ? evictPerFrame : int.MaxValue;

            while (_cache.Count > maxCacheEntries && budget-- > 0)
            {
                bool found = false;
                ulong bestKey = 0;
                float bestScore = float.MinValue;

                foreach (var kvp in _cache)
                {
                    if (kvp.Value.RefCount > 0) continue;
                    float score = kvp.Value.UseCount * 1000f - kvp.Value.LastUsedFrame;
                    if (!found || score < bestScore)
                    {
                        found = true;
                        bestKey = kvp.Key;
                        bestScore = score;
                    }
                }

                if (!found) break;
                RemoveEntryInner(bestKey);
            }
        }

        void RemoveEntry(ulong hash)
        {
            lock (_cacheLock)
            {
                RemoveEntryInner(hash);
            }
        }

        void RemoveEntryInner(ulong hash)
        {
            if (_cache.TryGetValue(hash, out var entry))
            {
                if (entry.Mesh != null)
                    Destroy(entry.Mesh);
                _cache.Remove(hash);
            }
        }

        ulong ComputeHash(ChunkData data, int leafSize, ulong neighborsHash)
        {
            if (!data.Materials.IsCreated) return 0;
            ulong hash = 1469598103934665603ul;
            for (int i = 0; i < data.Materials.Length; i++)
            {
                hash ^= data.Materials[i];
                hash *= 1099511628211ul;
            }
            if (data.Density.IsCreated && data.Density.Length == data.Materials.Length)
            {
                for (int i = 0; i < data.Density.Length; i++)
                {
                    hash ^= (ulong)(data.Density[i] * 0xFFFFFF);
                    hash *= 1099511628211ul;
                }
            }
            hash ^= (ulong)leafSize;
            hash ^= neighborsHash;
            return hash;
        }
    }
}
