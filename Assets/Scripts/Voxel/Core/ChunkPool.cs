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
        }

        public Chunk Get()
        {
            if (_pool.Count > 0)
            {
                var chunk = _pool.Dequeue();
                chunk.gameObject.SetActive(true);
                return chunk;
            }

            var instance = Object.Instantiate(_prefab, _parent);
            instance.gameObject.SetActive(true);
            return instance;
        }

        public void Return(Chunk chunk)
        {
            chunk.gameObject.SetActive(false);
            _pool.Enqueue(chunk);
        }
    }
}


