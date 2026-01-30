using System;
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
        readonly LinkedList<ulong> _lruOrder = new LinkedList<ulong>();
        readonly Dictionary<ulong, LinkedListNode<ulong>> _lruNodes = new Dictionary<ulong, LinkedListNode<ulong>>();
        readonly object _cacheLock = new object();

        struct SvoMeshEntry
        {
            public Mesh Mesh;
            public int RefCount;
            public int LastUsedFrame;
            public int UseCount;
        }

        /// <summary>Builds or returns cached SVO mesh. On Build/BuildMesh exception, logs error, disposes volume, returns false and does not cache.</summary>
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
                    MoveLruToEnd(hash);
                    mesh = entry.Mesh;
                    return true;
                }
            }

            SvoVolume volume = null;
            try
            {
                volume = SvoBuilder.Build(data, leafSize);
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
                    var node = _lruOrder.AddLast(hash);
                    _lruNodes[hash] = node;
                    EvictIfNeededInner();
                }
                mesh = builtMesh;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SvoManager] Build/BuildMesh failed for chunk {coord}: {e.Message}\n{e.StackTrace}");
                return false;
            }
            finally
            {
                volume?.Dispose();
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

        void MoveLruToEnd(ulong hash)
        {
            if (!_lruNodes.TryGetValue(hash, out var node)) return;
            _lruOrder.Remove(node);
            _lruOrder.AddLast(node);
        }

        /// <summary>LRU eviction: evicts oldest (front of _lruOrder) entries with RefCount 0. O(1) per eviction.</summary>
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

            for (var node = _lruOrder.First; node != null && _cache.Count > maxCacheEntries && budget > 0;)
            {
                var next = node.Next;
                ulong hash = node.Value;
                if (_cache.TryGetValue(hash, out var entry) && entry.RefCount == 0)
                {
                    RemoveEntryInner(hash);
                    budget--;
                }
                node = next;
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
            if (_lruNodes.TryGetValue(hash, out var lruNode))
            {
                _lruOrder.Remove(lruNode);
                _lruNodes.Remove(hash);
            }
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
