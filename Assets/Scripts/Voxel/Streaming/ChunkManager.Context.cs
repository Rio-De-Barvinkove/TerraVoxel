using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Generation;
using TerraVoxel.Voxel.GPU;
using TerraVoxel.Voxel.Lod;
using TerraVoxel.Voxel.Meshing;
using TerraVoxel.Voxel.Occlusion;
using TerraVoxel.Voxel.Rendering;
using TerraVoxel.Voxel.Save;
using TerraVoxel.Voxel.Svo;
using Unity.Collections;
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    public partial class ChunkManager
    {
        /// <summary>Internal context for streaming subsystems. Populated by ChunkManager.Init; Loader, Jobs, Cache, etc. may be null until then. Main-thread only; no lock. Properties delegate to Owner; setters have no min/max validation (Owner fields are serialized).</summary>
        internal sealed class Context
        {
            readonly ChunkManager _owner;

            internal Context(ChunkManager owner)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            }

            internal ChunkManager Owner => _owner;

            internal ChunkLoader Loader { get; set; }
            internal ChunkJobsManager Jobs { get; set; }
            internal ChunkIntegrationManager Integration { get; set; }
            internal ChunkLodManager Lod { get; set; }
            internal ChunkCacheManager Cache { get; set; }
            internal ChunkAdaptiveLimitsManager Adaptive { get; set; }
            internal ChunkWorkDropManager WorkDrop { get; set; }
            internal ChunkSafeSpawnManager SafeSpawn { get; set; }
            internal ChunkPhysicsManager Physics { get; set; }

            internal Transform Player { get => _owner.player; set => _owner.player = value; }
            internal Chunk ChunkPrefab { get => _owner.chunkPrefab; set => _owner.chunkPrefab = value; }
            internal WorldGenConfig WorldGen => _owner.worldGen;
            internal NoiseStack NoiseStack => _owner.noiseStack;
            internal ChunkViewConePrioritizer ViewCone => _owner.viewCone;
            internal ChunkOcclusionCuller OcclusionCuller => _owner.occlusionCuller;
            internal SvoManager SvoManager => _owner.svoManager;
            internal StreamingTimeBudget StreamingBudget => _owner.streamingBudget;
            internal SrpBatchingConfig SrpBatchingConfig => _owner.srpBatchingConfig;
            internal Material VoxelMaterial => _owner.voxelMaterial;
            internal VoxelMaterialLibrary VoxelMaterialLibrary => _owner.voxelMaterialLibrary;
            internal ChunkSaveManager SaveManager => _owner.saveManager;
            internal ChunkModManager ModManager => _owner.modManager;
            internal ChunkHybridSaveManager HybridSave => _owner.hybridSave;
            internal ChunkPhysicsOptimizer PhysicsOptimizer => _owner.physicsOptimizer;

            internal bool UseGpuPipeline => _owner.useGpuPipeline;
            internal int GpuMaxChunks => _owner.gpuMaxChunks;
            internal GpuWorldState GpuWorldState => _owner._gpuWorldState;
            internal GpuChunkGenerator GpuChunkGenerator => _owner._gpuChunkGenerator;
            internal GpuMesher GpuMesher => _owner._gpuMesher;
            internal GpuCuller GpuCuller => _owner._gpuCuller;
            internal GpuReadbackManager GpuReadbackManager => _owner._gpuReadbackManager;
            internal uint GetGpuChunkFlags(int slot) => _owner._gpuSlotFlags != null && _owner._gpuSlotFlags.TryGetValue(slot, out var f) ? f : 0;

            internal int LoadRadius { get => _owner.loadRadius; set => _owner.loadRadius = value; }
            internal int UnloadRadius { get => _owner.unloadRadius; set => _owner.unloadRadius = value; }
            internal bool AddColliders { get => _owner.addColliders; set => _owner.addColliders = value; }
            internal int MaxSpawnsPerFrame { get => _owner.maxSpawnsPerFrame; set => _owner.maxSpawnsPerFrame = value; }
            internal int MaxRemeshPerFrame { get => _owner.maxRemeshPerFrame; set => _owner.maxRemeshPerFrame = value; }
            internal int MaxRemovalsPerFrame { get => _owner.maxRemovalsPerFrame; set => _owner.maxRemovalsPerFrame = value; }

            internal bool ScaleJobsByProcessorCount { get => _owner.scaleJobsByProcessorCount; set => _owner.scaleJobsByProcessorCount = value; }
            internal int MaxGenJobsInFlight { get => _owner.maxGenJobsInFlight; set => _owner.maxGenJobsInFlight = value; }
            internal int MaxMeshJobsInFlight { get => _owner.maxMeshJobsInFlight; set => _owner.maxMeshJobsInFlight = value; }
            internal int MaxIntegrationsPerFrame { get => _owner.maxIntegrationsPerFrame; set => _owner.maxIntegrationsPerFrame = value; }
            internal bool DynamicIntegrationLimit { get => _owner.dynamicIntegrationLimit; set => _owner.dynamicIntegrationLimit = value; }
            internal int MaxIntegrationQueueSize { get => _owner.maxIntegrationQueueSize; set => _owner.maxIntegrationQueueSize = value; }

            internal bool StreamingPaused { get => _owner.streamingPaused; set => _owner.streamingPaused = value; }

            internal bool EnablePreload { get => _owner.enablePreload; set => _owner.enablePreload = value; }
            internal int PreloadRadius { get => _owner.preloadRadius; set => _owner.preloadRadius = value; }
            internal int MaxPreloadsPerFrame { get => _owner.maxPreloadsPerFrame; set => _owner.maxPreloadsPerFrame = value; }

            internal float RemovalBudgetMs { get => _owner.removalBudgetMs; set => _owner.removalBudgetMs = value; }

            internal int WorkDropDistance { get => _owner.workDropDistance; set => _owner.workDropDistance = value; }
            internal float WorkDropAngleDeg { get => _owner.workDropAngleDeg; set => _owner.workDropAngleDeg = value; }
            internal float WorkDropMoveAngleDeg { get => _owner.workDropMoveAngleDeg; set => _owner.workDropMoveAngleDeg = value; }
            internal float WorkDropCooldown { get => _owner.workDropCooldown; set => _owner.workDropCooldown = value; }

            internal int PendingQueueCap { get => _owner.pendingQueueCap; set => _owner.pendingQueueCap = value; }
            internal int PendingResetDistance { get => _owner.pendingResetDistance; set => _owner.pendingResetDistance = value; }

            internal bool EnableFullLod { get => _owner.enableFullLod; set => _owner.enableFullLod = value; }
            internal bool InitialLodFromDistance { get => _owner.initialLodFromDistance; set => _owner.initialLodFromDistance = value; }
            internal bool EnableFarRangeLod { get => _owner.enableFarRangeLod; set => _owner.enableFarRangeLod = value; }
            internal int FarRangeRadius { get => _owner.farRangeRadius; set => _owner.farRangeRadius = value; }
            internal ChunkLodSettings LodSettings => _owner.lodSettings;
            internal int MaxLodTransitionsPerFrame { get => _owner.maxLodTransitionsPerFrame; set => _owner.maxLodTransitionsPerFrame = value; }
            internal float LodTransitionCooldown { get => _owner.lodTransitionCooldown; set => _owner.lodTransitionCooldown = value; }
            internal int MaxSvoBuildsPerFrame { get => _owner.maxSvoBuildsPerFrame; set => _owner.maxSvoBuildsPerFrame = value; }
            internal bool EnableLodTransitionLog { get => _owner.enableLodTransitionLog; set => _owner.enableLodTransitionLog = value; }

            internal bool EnableGenSlicing { get => _owner.enableGenSlicing; set => _owner.enableGenSlicing = value; }
            internal int GenSliceCount { get => _owner.genSliceCount; set => _owner.genSliceCount = value; }

            internal bool EnableDataCache { get => _owner.enableDataCache; set => _owner.enableDataCache = value; }
            internal int MaxCachedChunks { get => _owner.maxCachedChunks; set => _owner.maxCachedChunks = value; }
            internal int MaxCacheOpsPerFrame { get => _owner.maxCacheOpsPerFrame; set => _owner.maxCacheOpsPerFrame = value; }

            internal bool EnableMeshCache { get => _owner.enableMeshCache; set => _owner.enableMeshCache = value; }
            internal int MaxMeshCacheEntries { get => _owner.maxMeshCacheEntries; set => _owner.maxMeshCacheEntries = value; }
            internal int MeshCacheEvictPerFrame { get => _owner.meshCacheEvictPerFrame; set => _owner.meshCacheEvictPerFrame = value; }

            internal bool EnableReverseLod { get => _owner.enableReverseLod; set => _owner.enableReverseLod = value; }
            internal int ReverseLodStep { get => _owner.reverseLodStep; set => _owner.reverseLodStep = value; }
            internal float ReverseLodUpgradeSeconds { get => _owner.reverseLodUpgradeSeconds; set => _owner.reverseLodUpgradeSeconds = value; }
            internal int MaxLodUpgradesPerFrame { get => _owner.maxLodUpgradesPerFrame; set => _owner.maxLodUpgradesPerFrame = value; }
            internal int ReverseLodMinDistance { get => _owner.reverseLodMinDistance; set => _owner.reverseLodMinDistance = value; }

            internal bool EnableAdaptiveLimits { get => _owner.enableAdaptiveLimits; set => _owner.enableAdaptiveLimits = value; }
            internal int GenSlowMs { get => _owner.genSlowMs; set => _owner.genSlowMs = value; }
            internal int MeshSlowMs { get => _owner.meshSlowMs; set => _owner.meshSlowMs = value; }
            internal int IntegrationSlowMs { get => _owner.integrationSlowMs; set => _owner.integrationSlowMs = value; }
            internal float AdaptiveCooldown { get => _owner.adaptiveCooldown; set => _owner.adaptiveCooldown = value; }
            internal long MemoryPressureThresholdMb { get => _owner.memoryPressureThresholdMb; set => _owner.memoryPressureThresholdMb = value; }
            internal long GraphicsMemoryThresholdMb { get => _owner.graphicsMemoryThresholdMb; set => _owner.graphicsMemoryThresholdMb = value; }

            internal float SafeSpawnTimeoutSeconds { get => _owner.safeSpawnTimeoutSeconds; set => _owner.safeSpawnTimeoutSeconds = value; }
            internal int MaxRebuildNeighborsDepth { get => _owner.maxRebuildNeighborsDepth; set => _owner.maxRebuildNeighborsDepth = value; }
            internal int MaxRequestRemeshNeighborsDepth { get => _owner.maxRequestRemeshNeighborsDepth; set => _owner.maxRequestRemeshNeighborsDepth = value; }
            internal bool EnableEdgeOnlyRemesh { get => _owner.enableEdgeOnlyRemesh; set => _owner.enableEdgeOnlyRemesh = value; }
            internal int MaxFaceRemeshPerFrame { get => _owner.maxFaceRemeshPerFrame; set => _owner.maxFaceRemeshPerFrame = value; }
            internal bool EnableSeamSkirts { get => _owner.enableSeamSkirts; set => _owner.enableSeamSkirts = value; }
            internal float SeamSkirtOffset { get => _owner.seamSkirtOffset; set => _owner.seamSkirtOffset = value; }

            internal Dictionary<ChunkCoord, Chunk> Active => _owner._active;
            internal Dictionary<ChunkCoord, CachedChunkData> DataCache => _owner._dataCache;
            internal void AddDataCacheEviction(ChunkCoord coord) => _owner.DataCacheEvictionAdd(coord);
            internal bool TryDequeueDataCacheEviction(out ChunkCoord coord) => _owner.DataCacheEvictionTryDequeue(out coord);
            internal void RemoveDataCacheEviction(ChunkCoord coord) => _owner.DataCacheEvictionRemove(coord);
            internal int DataCacheEvictionCount => _owner.DataCacheEvictionCount;
            internal Queue<ChunkCoord> Pending => _owner._pending;
            internal HashSet<ChunkCoord> PendingSet => _owner._pendingSet;
            internal Queue<ChunkCoord> Preload => _owner._preload;
            internal HashSet<ChunkCoord> PreloadSet => _owner._preloadSet;
            internal HashSet<ChunkCoord> Preloaded => _owner._preloaded;
            internal Queue<ChunkCoord> RemoveQueue => _owner._removeQueue;
            internal HashSet<ChunkCoord> RemoveSet => _owner._removeSet;
            internal Queue<ChunkCoord> FarRangeRenderQueue => _owner._farRangeRenderQueue;
            internal HashSet<ChunkCoord> FarRangeRenderSet => _owner._farRangeRenderSet;
            internal HashSet<ChunkCoord> RemeshSet => _owner._remeshSet;
            internal Dictionary<ChunkCoord, GenTask> GenJobs => _owner._genJobs;
            internal Dictionary<ChunkCoord, MeshTask> MeshJobs => _owner._meshJobs;
            internal List<ChunkCoord> GenCompleted => _owner._genCompleted;
            internal List<ChunkCoord> MeshCompleted => _owner._meshCompleted;
            internal HashSet<ChunkCoord> MeshedOnce => _owner._meshedOnce;
            internal ConcurrentQueue<ChunkCoord> IntegrationQueue => _owner._integrationQueue;
            internal ConcurrentDictionary<ChunkCoord, byte> IntegrationSet => _owner._integrationSet;
            internal Dictionary<ChunkCoord, ChunkMeshJobHandle> PendingMeshJobs => _owner._pendingMeshJobs;
            internal Dictionary<ChunkCoord, PendingCachedMesh> PendingCachedMeshes => _owner._pendingCachedMeshes;
            internal Dictionary<ulong, CachedMeshEntry> MeshCache => _owner._meshCache;
            internal Dictionary<ChunkCoord, ulong> ChunkMeshHashes => _owner._chunkMeshHashes;
            internal NativeArray<ushort> EmptyMaterials => _owner._emptyMaterials;
            internal HashSet<ChunkCoord> RemeshAfterIntegration => _owner._remeshAfterIntegration;
            internal Dictionary<ChunkCoord, int> NeighborDirtyFaces => _owner._neighborDirtyFaces;
            internal Dictionary<ChunkCoord, MeshData[]> ChunkFaceCache => _owner._chunkFaceCache;
            internal Queue<ChunkCoord> FaceRemeshQueue => _owner._faceRemeshQueue;
            internal HashSet<ChunkCoord> FaceRemeshSet => _owner._faceRemeshSet;
            internal Dictionary<ChunkCoord, FaceMeshTask> FaceMeshJobs => _owner._faceMeshJobs;
            internal List<RemoveCandidate> RemoveCandidates => _owner._removeCandidates;
            internal List<ChunkCoord> DropBufferPendingKeep => _owner._dropPendingKeep;
            internal List<ChunkCoord> DropBufferPreloadKeep => _owner._dropPreloadKeep;
            internal List<ChunkCoord> DropBufferRemeshKeep => _owner._dropRemeshKeep;
            internal List<(ChunkCoord, int)> DropBufferFaceRemeshKeep => _owner._dropFaceRemeshKeep;
            internal List<ChunkCoord> DropBufferFaceMeshStale => _owner._dropFaceMeshStale;
            internal List<ChunkCoord> DropBufferStale => _owner._dropStale;
            internal List<ChunkCoord> DropBufferCachedStale => _owner._dropCachedStale;
            internal List<ChunkCoord> DropBufferRemeshAfter => _owner._dropRemeshAfter;

            internal int CacheOpsThisFrame { get => _owner._cacheOpsThisFrame; set => _owner._cacheOpsThisFrame = value; }
            internal int IntegrationsLastFrame { get => _owner._integrationsLastFrame; set => _owner._integrationsLastFrame = value; }
            internal ChunkPool Pool { get => _owner._pool; set => _owner._pool = value; }
            internal IChunkGenerator Generator { get => _owner._generator; set => _owner._generator = value; }
            internal long LastGenMs { get => _owner._lastGenMs; set => _owner._lastGenMs = value; }
            internal long LastMeshMs { get => _owner._lastMeshMs; set => _owner._lastMeshMs = value; }
            internal long LastTotalMs { get => _owner._lastTotalMs; set => _owner._lastTotalMs = value; }
            internal long LastIntegrationMs { get => _owner._lastIntegrationMs; set => _owner._lastIntegrationMs = value; }
            internal ChunkCoord LastSpawnCoord { get => _owner._lastSpawnCoord; set => _owner._lastSpawnCoord = value; }
            internal ChunkCoord LastPendingCenter { get => _owner._lastPendingCenter; set => _owner._lastPendingCenter = value; }
            internal bool HasPendingCenter { get => _owner._hasPendingCenter; set => _owner._hasPendingCenter = value; }
            internal int SpawnedLastFrame { get => _owner._spawnedLastFrame; set => _owner._spawnedLastFrame = value; }
            internal int StreamingEpoch { get => _owner._streamingEpoch; set => _owner._streamingEpoch = value; }
            internal int BaseMaxGenJobsInFlight { get => _owner._baseMaxGenJobsInFlight; set => _owner._baseMaxGenJobsInFlight = value; }
            internal int BaseMaxMeshJobsInFlight { get => _owner._baseMaxMeshJobsInFlight; set => _owner._baseMaxMeshJobsInFlight = value; }
            internal int BaseMaxIntegrationsPerFrame { get => _owner._baseMaxIntegrationsPerFrame; set => _owner._baseMaxIntegrationsPerFrame = value; }
            internal int BaseMaxPreloadsPerFrame { get => _owner._baseMaxPreloadsPerFrame; set => _owner._baseMaxPreloadsPerFrame = value; }
            internal int RuntimeMaxGenJobsInFlight { get => _owner._runtimeMaxGenJobsInFlight; set => _owner._runtimeMaxGenJobsInFlight = value; }
            internal int RuntimeMaxMeshJobsInFlight { get => _owner._runtimeMaxMeshJobsInFlight; set => _owner._runtimeMaxMeshJobsInFlight = value; }
            internal int RuntimeMaxIntegrationsPerFrame { get => _owner._runtimeMaxIntegrationsPerFrame; set => _owner._runtimeMaxIntegrationsPerFrame = value; }
            internal int RuntimeMaxPreloadsPerFrame { get => _owner._runtimeMaxPreloadsPerFrame; set => _owner._runtimeMaxPreloadsPerFrame = value; }
            internal double AdaptiveUntil { get => _owner._adaptiveUntil; set => _owner._adaptiveUntil = value; }
            internal bool AdaptiveInitialized { get => _owner._adaptiveInitialized; set => _owner._adaptiveInitialized = value; }
            internal ChunkCoord LastDropCenter { get => _owner._lastDropCenter; set => _owner._lastDropCenter = value; }
            internal bool HasDropCenter { get => _owner._hasDropCenter; set => _owner._hasDropCenter = value; }
            internal Vector3 LastDropForward { get => _owner._lastDropForward; set => _owner._lastDropForward = value; }
            internal bool HasDropForward { get => _owner._hasDropForward; set => _owner._hasDropForward = value; }
            internal double LastDropTime { get => _owner._lastDropTime; set => _owner._lastDropTime = value; }
            internal bool WarnedLodStepMismatch { get => _owner._warnedLodStepMismatch; set => _owner._warnedLodStepMismatch = value; }
            internal bool WarnedChunkPrefabNull { get => ChunkManager._warnedChunkPrefabNull; set => ChunkManager._warnedChunkPrefabNull = value; }

            internal bool SafeSpawnInitialized { get => _owner._safeSpawnInitialized; set => _owner._safeSpawnInitialized = value; }
            internal int SafeSpawnWorldX0 { get => _owner._safeSpawnWorldX0; set => _owner._safeSpawnWorldX0 = value; }
            internal int SafeSpawnWorldZ0 { get => _owner._safeSpawnWorldZ0; set => _owner._safeSpawnWorldZ0 = value; }
            internal int SafeSpawnSizeVoxels { get => _owner._safeSpawnSizeVoxels; set => _owner._safeSpawnSizeVoxels = value; }
            internal int SafeSpawnBaseY { get => _owner._safeSpawnBaseY; set => _owner._safeSpawnBaseY = value; }
            internal int SafeSpawnTopY { get => _owner._safeSpawnTopY; set => _owner._safeSpawnTopY = value; }
            internal bool PendingSafeSpawnSnap { get => _owner._pendingSafeSpawnSnap; set => _owner._pendingSafeSpawnSnap = value; }
            internal bool WaitingSafeSpawnMesh { get => _owner._waitingSafeSpawnMesh; set => _owner._waitingSafeSpawnMesh = value; }
            internal ChunkCoord SafeSpawnAnchorCoord { get => _owner._safeSpawnAnchorCoord; set => _owner._safeSpawnAnchorCoord = value; }
            internal bool PlayerFrozenForSafeSpawn { get => _owner._playerFrozenForSafeSpawn; set => _owner._playerFrozenForSafeSpawn = value; }
            internal bool SavedPlayerControllerEnabled { get => _owner._savedPlayerControllerEnabled; set => _owner._savedPlayerControllerEnabled = value; }
            internal bool SavedCharacterControllerEnabled { get => _owner._savedCharacterControllerEnabled; set => _owner._savedCharacterControllerEnabled = value; }
            internal double SafeSpawnWaitStart { get => _owner._safeSpawnWaitStart; set => _owner._safeSpawnWaitStart = value; }

            internal int RebuildNeighborsDepth { get => _owner._rebuildNeighborsDepth; set => _owner._rebuildNeighborsDepth = value; }
            internal int RequestRemeshDepth { get => _owner._requestRemeshDepth; set => _owner._requestRemeshDepth = value; }

            internal void EnsurePrefab() => _owner.EnsurePrefab();
            internal bool BudgetExceeded() => _owner.BudgetExceeded();
            internal int EffectiveUnloadRadius() => _owner.EffectiveUnloadRadius();
            internal int EffectivePreloadRadius() => _owner.EffectivePreloadRadius();
            internal int CurrentMaxGenJobsInFlight => _owner.CurrentMaxGenJobsInFlight;
            internal int CurrentMaxMeshJobsInFlight => _owner.CurrentMaxMeshJobsInFlight;
            internal int CurrentMaxIntegrationsPerFrame => _owner.CurrentMaxIntegrationsPerFrame;
            internal int CurrentMaxPreloadsPerFrame => _owner.CurrentMaxPreloadsPerFrame;
            internal bool IsChunkBusy(ChunkCoord coord) => _owner.IsChunkBusy(coord);
            internal bool IsChunkGenerating(ChunkCoord coord) => _owner.IsChunkGenerating(coord);
            internal bool IsWithinKeepRadius(ChunkCoord coord, ChunkCoord center, int keepRadius) => _owner.IsWithinKeepRadius(coord, center, keepRadius);
            internal bool IsWithinLoadRadius(ChunkCoord coord, ChunkCoord center, int radius) => _owner.IsWithinLoadRadius(coord, center, radius);
            internal bool TryGetChunk(ChunkCoord coord, out Chunk chunk) => _owner.TryGetChunk(coord, out chunk);
            internal void QueueRemesh(ChunkCoord coord) => _owner.QueueRemesh(coord);
            internal bool ScheduleMeshForChunk(ChunkCoord coord, double spawnStart, int lodStep = 1) => _owner.ScheduleMeshForChunk(coord, spawnStart, lodStep);
            internal void GetMeshMaterialSettings(Chunk chunk, out byte maxMaterialIndex, out byte fallbackMaterialIndex) => _owner.GetMeshMaterialSettings(chunk, out maxMaterialIndex, out fallbackMaterialIndex);
            internal void ReleaseMeshCacheForChunk(ChunkCoord coord) => _owner.ReleaseMeshCacheForChunk(coord);
            internal void ReleaseFaceCacheForChunk(ChunkCoord coord) => _owner.ReleaseFaceCacheForChunk(coord);
            internal void RegisterMeshCacheForChunk(ChunkCoord coord, ulong hash, Mesh mesh, bool markShared, bool addCollider) => _owner.RegisterMeshCacheForChunk(coord, hash, mesh, markShared, addCollider);
            internal bool HasAllNeighbors(GreedyMesher.NeighborData data) => _owner.HasAllNeighbors(data);
            internal void RebuildNeighbors(ChunkCoord coord) => _owner.RebuildNeighbors(coord);
        }
    }
}
