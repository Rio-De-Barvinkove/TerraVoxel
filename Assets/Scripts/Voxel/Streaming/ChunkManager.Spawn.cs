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
                    bool hasGeometry = chunk.Data.GpuSlot >= 0 && _gpuWorldState != null
                        && _gpuWorldState.GetDescriptor(chunk.Data.GpuSlot).VertexCount > 0;
                    chunk.SetGpuBoxCollider(hasGeometry, hasGeometry ? chunkWorldSize : 0f);
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

                // Apply safe spawn platform directly to GPU voxel buffer (CPU path is skipped when useGpuPipeline).
                if (_safeSpawnInitialized && worldGen.EnableSafeSpawn && !loadedFromCache && !loadedSnapshot && !preload)
                {
                    ApplySafeSpawnToGpu(coord, slot);
                }

                if (_gpuMesher != null && _gpuMesher.IsValid)
                    _gpuMesher.MeshChunk(_gpuWorldState, slot);

                chunk.Data.ValidateSize(worldGen.ChunkSize);
                _active[coord] = chunk;
                addedToActive = true;
                chunk.ApplyGpuMeshRef(slot);

                // Only add collider if chunk has geometry (vertexCount > 0); empty/air chunks get no collider.
                if (addColliders)
                {
                    var desc = _gpuWorldState.GetDescriptor(slot);
                    if (desc.VertexCount > 0)
                        chunk.SetGpuBoxCollider(true, chunkWorldSize);
                    else
                        chunk.SetGpuBoxCollider(false, 0f);
                }

                // GPU path does not use integration queue; clear safe-spawn wait when anchor chunk is spawned.
                if (_waitingSafeSpawnMesh && coord.Equals(_safeSpawnAnchorCoord))
                {
                    _safeSpawn?.SnapPlayerToSafeSpawn();
                    _safeSpawn?.SetPlayerFrozen(false);
                    _waitingSafeSpawnMesh = false;
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

        /// <summary>Write safe-spawn platform voxels directly into the GPU VoxelMaterialBuffer for a given slot/coord. Single batch upload.</summary>
        void ApplySafeSpawnToGpu(ChunkCoord coord, int slot)
        {
            if (worldGen == null || _gpuWorldState == null) return;
            int chunkSize = worldGen.ChunkSize;
            int worldX0 = coord.X * chunkSize;
            int worldZ0 = coord.Z * chunkSize;
            int worldX1 = worldX0 + chunkSize - 1;
            int worldZ1 = worldZ0 + chunkSize - 1;
            int worldY0 = coord.Y * chunkSize;
            int worldY1 = worldY0 + chunkSize - 1;

            int spawnX1 = _safeSpawnWorldX0 + _safeSpawnSizeVoxels - 1;
            int spawnZ1 = _safeSpawnWorldZ0 + _safeSpawnSizeVoxels - 1;

            if (worldX1 < _safeSpawnWorldX0 || worldX0 > spawnX1) return;
            if (worldZ1 < _safeSpawnWorldZ0 || worldZ0 > spawnZ1) return;
            if (worldY1 < _safeSpawnBaseY || worldY0 > _safeSpawnTopY) return;

            int matIndex = worldGen.SafeSpawnMaterialIndex <= 0 ? 200 : Mathf.Clamp(worldGen.SafeSpawnMaterialIndex, 1, ushort.MaxValue);
            uint matU = (uint)Mathf.Clamp(matIndex, 1, ushort.MaxValue);

            int startX = Mathf.Max(worldX0, _safeSpawnWorldX0);
            int endX = Mathf.Min(worldX1, spawnX1);
            int startZ = Mathf.Max(worldZ0, _safeSpawnWorldZ0);
            int endZ = Mathf.Min(worldZ1, spawnZ1);
            int startY = Mathf.Max(worldY0, _safeSpawnBaseY);
            int endY = Mathf.Min(worldY1, _safeSpawnTopY);

            // Read entire slot from GPU, patch safe spawn region, re-upload once.
            int voxelsPerChunk = chunkSize * chunkSize * chunkSize;
            int voxelOffset = _gpuWorldState.GetVoxelOffset(slot);
            uint[] voxels = new uint[voxelsPerChunk];
            _gpuWorldState.VoxelMaterialBuffer.GetData(voxels, 0, voxelOffset, voxelsPerChunk);

            for (int wx = startX; wx <= endX; wx++)
            {
                int lx = wx - worldX0;
                for (int wz = startZ; wz <= endZ; wz++)
                {
                    int lz = wz - worldZ0;
                    for (int wy = startY; wy <= endY; wy++)
                    {
                        int ly = wy - worldY0;
                        int localIdx = lx + ly * chunkSize + lz * chunkSize * chunkSize;
                        voxels[localIdx] = matU;
                    }
                }
            }

            _gpuWorldState.VoxelMaterialBuffer.SetData(voxels, 0, voxelOffset, voxelsPerChunk);
        }
    }
}
