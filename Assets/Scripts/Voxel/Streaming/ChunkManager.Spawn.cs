using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Generation;
using TerraVoxel.Voxel.GPU;
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>Partial: EnsurePrefab, ActivatePreloadedChunk, SpawnChunk.</summary>
    public partial class ChunkManager
    {
        /// <summary>Ensures chunkPrefab is non-null (creates auto-prefab if needed). Returns false if creation failed; caller must not use prefab.</summary>
        bool EnsurePrefab()
        {
            if (chunkPrefab != null) return true;
            var go = new GameObject("ChunkPrefab (auto)");
            chunkPrefab = go.AddComponent<Chunk>();
            if (chunkPrefab == null)
            {
                Debug.LogError("[ChunkManager] EnsurePrefab: AddComponent<Chunk> failed.");
                return false;
            }
            go.SetActive(false);
            return true;
        }

        void ActivatePreloadedChunk(ChunkCoord coord, Chunk chunk)
        {
            if (!_preloaded.Remove(coord)) return;
            if (chunk == null) return;
            chunk.SetRendererEnabled(true);
            if (addColliders)
            {
                if (chunk.IsGpuRendered)
                {
                    float chunkWorldSize = worldGen.ChunkSize * VoxelConstants.VoxelSize;
                    chunk.SetGpuBoxCollider(true, chunkWorldSize);
                }
                else
                    chunk.SetColliderEnabled(true);
            }

            if (chunk.IsGpuRendered)
                return;

            Mesh mesh = chunk.GetRenderMesh();
            if (mesh == null || mesh.vertexCount == 0)
            {
                QueueRemesh(coord);
                return;
            }
            // If QueueRemesh did not run yet or remesh fails later, chunk stays without mesh until next pass.

            if (chunk.IsLowLod || (enableReverseLod && reverseLodStep > 1))
            {
                chunk.IsLowLod = false;
                chunk.LodStartTime = 0;
                QueueRemesh(coord);
            }
        }

        internal void SpawnChunk(ChunkCoord coord, bool preload = false)
        {
            if (useGpuPipeline && _gpuWorldState != null && _gpuChunkGenerator != null && _gpuChunkGenerator.IsValid)
            {
                SpawnChunkGpu(coord, preload);
                return;
            }

            if (useGpuPipeline && !_warnedGpuNotInitialized)
            {
                _warnedGpuNotInitialized = true;
                Debug.LogWarning("[ChunkManager] GPU pipeline enabled but GPU not initialized (WorldGen or compute shaders missing in Awake). Spawning CPU chunks. Assign WorldGen and compute shaders before Play.");
            }

            if (!EnsurePrefab() || chunkPrefab == null) return;
            if (_pool == null) _pool = new ChunkPool(chunkPrefab, transform);
            if (_generator == null) _generator = new ChunkGenerator();

            var chunk = _pool.Get();
            float chunkWorldSize = worldGen.ChunkSize * VoxelConstants.VoxelSize;
            chunk.Initialize(coord, chunkWorldSize);
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
            chunk.Data.ValidateSize(worldGen.ChunkSize);
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

        void SpawnChunkGpu(ChunkCoord coord, bool preload)
        {
            if (!EnsurePrefab() || chunkPrefab == null) return;
            if (_pool == null) _pool = new ChunkPool(chunkPrefab, transform);

            int slot;
            try
            {
                slot = _gpuWorldState.AllocateChunk(coord);
            }
            catch (System.InvalidOperationException)
            {
                if (!_warnedGpuSlotsFull)
                {
                    _warnedGpuSlotsFull = true;
                    Debug.LogWarning("[ChunkManager] GPU slot allocator full (gpuMaxChunks reached). Increase gpuMaxChunks or reduce load radius. Chunk not spawned.");
                }
                return;
            }

            float chunkWorldSize = worldGen.ChunkSize * VoxelConstants.VoxelSize;
            Chunk chunk = null;
            bool addedToActive = false;
            try
            {
                chunk = _pool.Get();
                chunk.Initialize(coord, chunkWorldSize);
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

                chunk.Data.GpuSlot = slot;
                chunk.Data.GpuOffset = _gpuWorldState.GetVoxelOffset(slot);
                bool allocateDensity = saveManager != null && saveManager.SaveDensity;
                chunk.Data.Allocate(worldGen.ChunkSize, Unity.Collections.Allocator.Persistent, allocateDensity);

                bool loadedFromCache = TryLoadFromCache(coord, chunk.Data);
                bool loadedSnapshot = false;
                if (!loadedFromCache)
                {
                    if (hybridSave != null)
                        loadedSnapshot = hybridSave.TryLoadSnapshot(coord, chunk.Data);
                    else if (saveManager != null && saveManager.LoadOnSpawn)
                        loadedSnapshot = saveManager.TryLoadInto(coord, chunk.Data);
                }
                else if (_dataCache.TryGetValue(coord, out var cached))
                {
                    cached.Dispose();
                    _dataCache.Remove(coord);
                }

                if (loadedFromCache || loadedSnapshot)
                {
                    if (hybridSave != null)
                        hybridSave.ApplyDeltaIfAny(coord, chunk.Data);
                    int voxelCount = chunk.Data.Materials.Length;
                    if (_gpuUploadMaterials == null || _gpuUploadMaterials.Length < voxelCount)
                        _gpuUploadMaterials = new ushort[voxelCount];
                    chunk.Data.Materials.CopyTo(_gpuUploadMaterials);
                    _gpuWorldState.SetVoxels(slot, _gpuUploadMaterials);
                }
                else
                {
                    chunk.Data.Dispose();
                    chunk.Data.GpuSlot = slot;
                    chunk.Data.GpuOffset = _gpuWorldState.GetVoxelOffset(slot);
                    _gpuChunkGenerator.ScheduleGeneration(_gpuWorldState, coord, slot, worldGen, noiseStack);
                    _gpuWorldState.SyncVoxelSlot(slot);
                }
                if (modManager != null)
                {
                    if (chunk.Data.IsCreated)
                        modManager.ApplyModsToChunk(coord, chunk.Data);
                    modManager.ApplyModsToGpu(coord, slot, _gpuWorldState);
                }

                if (_gpuMesher != null && _gpuMesher.IsValid)
                    _gpuMesher.MeshChunk(_gpuWorldState, slot);

                chunk.Data.ValidateSize(worldGen.ChunkSize);
                _active[coord] = chunk;
                addedToActive = true;
                chunk.ApplyGpuMeshRef(slot);
                if (addColliders)
                {
                    chunk.SetGpuBoxCollider(true, chunkWorldSize);
                }
            }
            finally
            {
                if (!addedToActive)
                {
                    _gpuWorldState.FreeChunk(coord);
                    if (chunk != null)
                    {
                        if (chunk.Data.IsCreated)
                            chunk.Data.Dispose();
                        _pool.Return(chunk);
                    }
                }
            }
        }
    }
}
