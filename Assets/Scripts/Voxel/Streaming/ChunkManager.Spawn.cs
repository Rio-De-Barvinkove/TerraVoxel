using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Generation;
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>Partial: EnsurePrefab, ActivatePreloadedChunk, SpawnChunk.</summary>
    public partial class ChunkManager
    {
        void EnsurePrefab()
        {
            if (chunkPrefab == null)
            {
                var go = new GameObject("ChunkPrefab (auto)");
                chunkPrefab = go.AddComponent<Chunk>();
                go.SetActive(false);
            }
        }

        void ActivatePreloadedChunk(ChunkCoord coord, Chunk chunk)
        {
            if (!_preloaded.Remove(coord)) return;
            if (chunk == null) return;
            chunk.SetRendererEnabled(true);
            if (addColliders)
                chunk.SetColliderEnabled(true);

            Mesh mesh = chunk.GetRenderMesh();
            if (mesh == null || mesh.vertexCount == 0)
            {
                QueueRemesh(coord);
                return;
            }

            if (chunk.IsLowLod || (enableReverseLod && reverseLodStep > 1))
            {
                chunk.IsLowLod = false;
                chunk.LodStartTime = 0;
                QueueRemesh(coord);
            }
        }

        internal void SpawnChunk(ChunkCoord coord, bool preload = false)
        {
            EnsurePrefab();
            if (_pool == null) _pool = new ChunkPool(chunkPrefab, transform);
            if (_generator == null) _generator = new ChunkGenerator();

            var chunk = _pool.Get();
            chunk.Initialize(coord);
            if (srpBatchingConfig != null)
                srpBatchingConfig.ApplyToChunk(chunk);
            else if (voxelMaterial != null)
                chunk.SetSharedMaterial(voxelMaterial);
            ApplyChunkLayer(chunk);
            if (preload)
            {
                _preloaded.Add(coord);
                chunk.SetRendererEnabled(false);
                chunk.SetColliderEnabled(false);
            }
            else if (_preloaded.Contains(coord))
            {
                _preloaded.Remove(coord);
            }

            bool allocateDensity = saveManager != null && saveManager.SaveDensity;
            chunk.Data.Allocate(worldGen.ChunkSize, Unity.Collections.Allocator.Persistent, allocateDensity);
            double spawnStart = Time.realtimeSinceStartupAsDouble;
            bool loadedFromCache = TryLoadFromCache(coord, chunk.Data);
            bool loadedSnapshot = false;
            if (!loadedFromCache)
            {
                if (hybridSave != null)
                    loadedSnapshot = hybridSave.TryLoadSnapshot(coord, chunk.Data);
                else if (saveManager != null && saveManager.LoadOnSpawn)
                    loadedSnapshot = saveManager.TryLoadInto(coord, chunk.Data);
            }
            else
            {
                if (_dataCache.TryGetValue(coord, out var cached))
                {
                    cached.Dispose();
                    _dataCache.Remove(coord);
                }
            }

            chunk.transform.position = new Vector3(coord.X * worldGen.ChunkSize, coord.Y * worldGen.ChunkSize, coord.Z * worldGen.ChunkSize) * VoxelConstants.VoxelSize;
            _active[coord] = chunk;

            bool applySafeSpawn = !loadedFromCache && !loadedSnapshot && _safeSpawnInitialized && worldGen.EnableSafeSpawn && !preload;
            bool applyDelta = !loadedFromCache && !loadedSnapshot && hybridSave != null;

            if (loadedFromCache || loadedSnapshot)
            {
                if (!loadedFromCache && hybridSave == null && modManager != null)
                    modManager.ApplyModsToChunk(coord, chunk.Data);

                if (!ScheduleMeshForChunk(coord, spawnStart, 1))
                    QueueRemesh(coord);
            }
            else
            {
                ScheduleGenJob(coord, chunk, spawnStart, applySafeSpawn, applyDelta);
            }
        }
    }
}
