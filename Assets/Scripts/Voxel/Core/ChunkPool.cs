using System.Collections.Generic;
using UnityEngine;

namespace TerraVoxel.Voxel.Core
{
    /// <summary>
    /// Simple pool for chunk GameObjects.
    /// </summary>
    public class ChunkPool
    {
        readonly Queue<Chunk> _pool = new Queue<Chunk>();
        readonly Chunk _prefab;
        readonly Transform _parent;

        public ChunkPool(Chunk prefab, Transform parent)
        {
            _prefab = prefab;
            _parent = parent;
            PrepareChunk(_prefab);
        }

        public Chunk Get()
        {
            if (_pool.Count > 0)
            {
                var chunk = _pool.Dequeue();
                PrepareChunk(chunk);
                chunk.gameObject.SetActive(true);
                return chunk;
            }

            var instance = Object.Instantiate(_prefab, _parent);
            PrepareChunk(instance);
            instance.gameObject.SetActive(true);
            return instance;
        }

        public void Return(Chunk chunk)
        {
            PrepareChunk(chunk);
            chunk.gameObject.SetActive(false);
            _pool.Enqueue(chunk);
        }

        static void PrepareChunk(Chunk chunk)
        {
            if (chunk == null) return;
            var collider = chunk.GetComponent<MeshCollider>();
            if (collider == null) return;
            collider.sharedMesh = null;
            collider.enabled = false;
        }
    }
}


