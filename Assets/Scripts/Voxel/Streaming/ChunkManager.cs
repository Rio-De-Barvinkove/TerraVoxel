using System.Collections.Concurrent;
using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Generation;
/* using TerraVoxel.Voxel.GPU; */
/* using TerraVoxel.Voxel.Lod; */
using TerraVoxel.Voxel.Meshing;
using TerraVoxel.Voxel.Occlusion;
using TerraVoxel.Voxel.Rendering;
/* using TerraVoxel.Voxel.Save; */
/* using TerraVoxel.Voxel.Svo; */
using TerraVoxel.Voxel.Systems;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Serialization;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>
    /// Maintains active chunks around a tracked transform. Spawns limited count per frame.
    /// Facade: delegates to ChunkLoader, ChunkJobsManager, ChunkIntegrationManager, ChunkLodManager, ChunkCacheManager, ChunkAdaptiveLimitsManager, ChunkWorkDropManager, ChunkSafeSpawnManager, ChunkPhysicsManager when present; keeps full fallback implementations in this class.
    /// Intended to run on main thread (Update); job handles are only completed on main thread. All state is accessed from main thread only; no locking. If ever used from multiple threads, add synchronization.
    /// _pendingSet + _pending duplicate coords for O(1) membership; data/mesh caches use eviction (LRU-style). _emptyMaterials uses Allocator.Persistent and must be disposed (OnDestroy).
    /// _integrationQueue/_integrationSet are main-thread only (Concurrent* used for TryAdd/TryRemove API); _active, _preloaded, _dataCache are main-thread only. Members _gpuWorldState, _gpuChunkGenerator, _gpuMesher, hybridSave, modManager, saveManager, worldGen, noiseStack, voxelMaterial are defined in this partial or assigned in Awake.
    /// </summary>
    public partial class ChunkManager : MonoBehaviour
    {
        [Tooltip("Required for streaming. Transform to track (e.g. camera or character). If null, no chunks spawn.")]
        [SerializeField] Transform player;
        [Tooltip("Optional. If null, auto-created at runtime. Chunk instances are spawned under this manager's transform.")]
        [SerializeField] Chunk chunkPrefab;
        [Tooltip("Required for streaming. Chunk size, column height, noise. If null, no chunks spawn.")]
        [SerializeField] WorldGenConfig worldGen;
        [SerializeField] NoiseStack noiseStack;
        [SerializeField] [Range(1, 64)] int loadRadius = 2;
        [SerializeField] [Range(1, 64)] int unloadRadius = 3;
        [Tooltip("Vertical load radius (chunks above/below player). Separate from horizontal loadRadius for infinite-Y worlds.")]
        [SerializeField] [Range(1, 32)] int verticalRadius = 4;
        [Tooltip("Add colliders to chunks. GPU path uses BoxCollider per chunk; CPU path uses MeshCollider.")]
        [SerializeField] bool addColliders = true;
        /* [Header("Physics")]
        [SerializeField] ChunkPhysicsOptimizer physicsOptimizer; */
        [SerializeField] [Range(1, 32)] int maxSpawnsPerFrame = 1;
        [SerializeField] [Range(1, 64)] int maxRemeshPerFrame = 10;
        [SerializeField] [Range(1, 32)] int maxRemovalsPerFrame = 2;
        [Header("Threading / Multi-core")]
        [Tooltip("Gen + Mesh jobs run on worker threads (Burst). Integration/Remove must stay on main thread (Unity API).")]
        [SerializeField] bool scaleJobsByProcessorCount = true;
        [Tooltip("Max gen jobs in parallel. Used if scaleJobsByProcessorCount=false, else computed from processor count.")]
        [SerializeField] [Range(1, 32)] int maxGenJobsInFlight = 2;
        [Tooltip("Max mesh jobs in parallel. Used if scaleJobsByProcessorCount=false, else computed from processor count.")]
        [SerializeField] [Range(1, 32)] int maxMeshJobsInFlight = 2;
        [SerializeField] [Range(1, 64)] int maxIntegrationsPerFrame = 10;
        [SerializeField] bool dynamicIntegrationLimit = true;
        [SerializeField] [Range(256, 16384)] int maxIntegrationQueueSize = 2000;
        [Header("Streaming Control")]
        [SerializeField] bool streamingPaused = false;
        [Header("Preload")]
        [SerializeField] bool enablePreload = false;
        [SerializeField] [Range(0, 32)] int preloadRadius = 4;
        [SerializeField] [Range(0, 16)] int maxPreloadsPerFrame = 1;
        [Header("Removal Budget")]
        [SerializeField] float removalBudgetMs = 0.75f;
        [Header("Work Dropping")]
        [Tooltip("If player moves this many chunks (XZ), consider dropping queues.")]
        [SerializeField] int workDropDistance = 8;
        [Tooltip("0 = spawn chunks in all directions. >0 = only spawn chunks in view cone (can prevent ANY spawns if cone is narrow or camera looks away).")]
        [SerializeField] float workDropAngleDeg = 0f;
        [Tooltip("If move direction differs from view by this many degrees, consider dropping.")]
        [SerializeField] float workDropMoveAngleDeg = 70f;
        [SerializeField] float workDropCooldown = 0.5f;
        [Header("Pending Queue")]
        [SerializeField] int pendingQueueCap = 4096;
        [Tooltip("If player center moves this many chunks (XZ), pending queue is rebuilt.")]
        [SerializeField] int pendingResetDistance = 8;
        /* [Header("View Cone")]
        [SerializeField] ChunkViewConePrioritizer viewCone; */
        [Header("Full LOD System")]
        [Tooltip("When true, ChunkLodManager.ProcessFullLod iterates 2000+ chunks; heavy for GPU. Keep disabled for GPU pipeline.")]
        [SerializeField] bool enableFullLod = false;
        [Tooltip("Resolve LOD by distance before first mesh (spawns distant chunks at coarse LOD immediately).")]
        [SerializeField] bool initialLodFromDistance = true;
        [Tooltip("Far-range pipeline: render-only chunks beyond unloadRadius with low LOD/SVO (queue stub only).")]
        [SerializeField] bool enableFarRangeLod = false;
        [SerializeField] int farRangeRadius = 6;
        /* [SerializeField] ChunkLodSettings lodSettings; */
        [Tooltip("LOD transitions per frame; higher = faster upgrades when approaching.")]
        [SerializeField] int maxLodTransitionsPerFrame = 16;
        [Tooltip("Cooldown (sec) before downgrade; upgrades (approaching) bypass cooldown.")]
        [SerializeField] float lodTransitionCooldown = 0.15f;
        [Tooltip("Max SVO builds per frame; increase if SVO chunks lag when approaching.")]
        [SerializeField] int maxSvoBuildsPerFrame = 4;
        [Tooltip("Log LOD transitions (Dist, CurrentStep, TargetStep) for debugging.")]
        [SerializeField] bool enableLodTransitionLog = false;
        [Header("Occlusion")]
        [SerializeField] ChunkOcclusionCuller occlusionCuller;
        /* [Header("GPU Pipeline")] — GPU pipeline fields commented out (CPU-only rollback) */
        [HideInInspector] [SerializeField] bool useGpuPipeline = false;
        /* [SerializeField] GpuPipelineComputeAssets gpuPipelineComputeAssets; */
        [HideInInspector] [SerializeField] ComputeShader voxelGenerationCompute;
        [HideInInspector] [SerializeField] ComputeShader chunkAnalysisCompute;
        [HideInInspector] [SerializeField] ComputeShader chunkCullingCompute;
        [HideInInspector] [SerializeField] ComputeShader voxelMeshingCompute;
        /* [SerializeField] GpuDrivenRenderer gpuDrivenRenderer; */
        [HideInInspector] [SerializeField] [Range(64, 16384)] int gpuMaxChunks = 8192;
        [HideInInspector] [SerializeField] [Range(1, 8)] int maxColliderReadbacksPerFrame = 1;
        [HideInInspector] [SerializeField] [Range(1, 8)] int gpuMaxSpawnsPerFrame = 2;
        [HideInInspector] [SerializeField] [Range(0, 32)] int dualPipelineCpuRadius = 0;
        [HideInInspector] [SerializeField] bool enableOctreeLod = false;
        [HideInInspector] [SerializeField] [Range(1, 200)] int maxOctreeNodeCreationsPerFrame = 50;
        [HideInInspector] [SerializeField] bool gpuChunkAnalyzerEnabled = false;
        [HideInInspector] [SerializeField] bool gpuFrustumCulling = true;
        [HideInInspector] [SerializeField] [Range(0f, 20f)] float gpuFrustumMarginScale = 6f;
        [HideInInspector] [SerializeField] bool gpuOcclusionCulling = false;
        /* [Header("SVO")]
        [SerializeField] SvoManager svoManager; */
        [Header("Streaming Budget")]
        [SerializeField] StreamingTimeBudget streamingBudget = new StreamingTimeBudget();
        [SerializeField] string chunkLayerName = "Terrain";
        [Header("SRP Batching")]
        [Tooltip("Preferred: SRP batching config (material + library). If null, legacy voxelMaterial used.")]
        [SerializeField] SrpBatchingConfig srpBatchingConfig;
        [Tooltip("Legacy: shared material when srpBatchingConfig is null.")]
        [SerializeField] Material voxelMaterial;
        [Tooltip("Legacy: configures voxelMaterial when srpBatchingConfig is null.")]
        [SerializeField] VoxelMaterialLibrary voxelMaterialLibrary;
        /* [SerializeField] ChunkSaveManager saveManager; */
        /* [SerializeField] ChunkModManager modManager; */
        /* [SerializeField] ChunkHybridSaveManager hybridSave; */
        [Header("Generation Slicing")]
        [SerializeField] bool enableGenSlicing = false;
        [SerializeField] int genSliceCount = 4;
        [Header("Chunk Data Cache")]
        [SerializeField] bool enableDataCache = true;
        [SerializeField] int maxCachedChunks = 500;
        [SerializeField] int maxCacheOpsPerFrame = 2;
        [Header("Mesh Cache")]
        [SerializeField] bool enableMeshCache = true;
        [SerializeField] int maxMeshCacheEntries = 512;
        [SerializeField] int meshCacheEvictPerFrame = 4;
        [Header("Reverse LOD")]
        [SerializeField] bool enableReverseLod = false;
        [SerializeField] int reverseLodStep = 2;
        [FormerlySerializedAs("reverseLodUpgradeFrames")]
        [SerializeField] float reverseLodUpgradeSeconds = 0.08f;
        [SerializeField] int maxLodUpgradesPerFrame = 1;
        [SerializeField] int reverseLodMinDistance = 1;
        [Header("Adaptive Limits")]
        [SerializeField] bool enableAdaptiveLimits = true;
        [SerializeField] int genSlowMs = 12;
        [SerializeField] int meshSlowMs = 12;
        [SerializeField] int integrationSlowMs = 4;
        [SerializeField] float adaptiveCooldown = 0.5f;
        [SerializeField] long memoryPressureThresholdMb = 0;
        [Tooltip("Throttle streaming when graphics memory (MB) exceeds this. 0 = disabled.")]
        [SerializeField] long graphicsMemoryThresholdMb = 0;
        [Header("Safe Spawn")]
        [SerializeField] float safeSpawnTimeoutSeconds = 10f;
        [Header("Integration / Remesh Guards")]
        [SerializeField] int maxRebuildNeighborsDepth = 2;
        [SerializeField] int maxRequestRemeshNeighborsDepth = 1;
        [Tooltip("When true, rebuild neighbors when a chunk appears (async jobs, no main-thread block).")]
        [SerializeField] bool enableEdgeOnlyRemesh = true;
        [Tooltip("Max face remeshes scheduled per frame when enableEdgeOnlyRemesh is true. Async jobs, no main-thread block.")]
        [SerializeField] int maxFaceRemeshPerFrame = 4;
        [Header("Seam Fix (Skirts)")]
        [Tooltip("Expand boundary quads slightly into neighbor space to hide T-junctions and seams.")]
        [SerializeField] bool enableSeamSkirts = true;
        [Tooltip("Vertex offset for skirts (voxel units). ~0.005-0.02 for microvoxel (0.1).")]
        [SerializeField] [Range(0.0001f, 0.1f)] float seamSkirtOffset = 0.008f;

        readonly Dictionary<ChunkCoord, Chunk> _active = new Dictionary<ChunkCoord, Chunk>();
        readonly Dictionary<ChunkCoord, CachedChunkData> _dataCache = new Dictionary<ChunkCoord, CachedChunkData>();
        /// <summary>FIFO eviction order for data cache; O(1) dequeue from front and remove by coord.</summary>
        readonly LinkedList<ChunkCoord> _dataCacheEvictionList = new LinkedList<ChunkCoord>();
        readonly Dictionary<ChunkCoord, LinkedListNode<ChunkCoord>> _dataCacheEvictionNodes = new Dictionary<ChunkCoord, LinkedListNode<ChunkCoord>>();
        int _cacheOpsThisFrame;
        readonly Queue<ChunkCoord> _pending = new Queue<ChunkCoord>();
        readonly HashSet<ChunkCoord> _pendingSet = new HashSet<ChunkCoord>();
        /// <summary>Min-heap by 2D distance (XZ) from _pendingDequeueCenter; used when viewCone is null/disabled. Rebuilt when center changes or after RebuildPendingQueue.</summary>
        readonly List<ChunkCoord> _pendingDistanceHeap = new List<ChunkCoord>();
        ChunkCoord _pendingDequeueCenter;
        readonly Queue<ChunkCoord> _preload = new Queue<ChunkCoord>();
        readonly Queue<ChunkCoord> _removeQueue = new Queue<ChunkCoord>();
        readonly HashSet<ChunkCoord> _preloadSet = new HashSet<ChunkCoord>();
        readonly HashSet<ChunkCoord> _preloaded = new HashSet<ChunkCoord>();
        readonly Queue<ChunkCoord> _farRangeRenderQueue = new Queue<ChunkCoord>();
        readonly HashSet<ChunkCoord> _farRangeRenderSet = new HashSet<ChunkCoord>();
        readonly HashSet<ChunkCoord> _removeSet = new HashSet<ChunkCoord>();
        /// <summary>Remesh queue; ProcessRemeshQueue picks closest by distance (min-heap semantics).</summary>
        readonly HashSet<ChunkCoord> _remeshSet = new HashSet<ChunkCoord>();
        readonly Dictionary<ChunkCoord, GenTask> _genJobs = new Dictionary<ChunkCoord, GenTask>();
        readonly Dictionary<ChunkCoord, MeshTask> _meshJobs = new Dictionary<ChunkCoord, MeshTask>();
        readonly List<ChunkCoord> _genCompleted = new List<ChunkCoord>();
        readonly List<ChunkCoord> _meshCompleted = new List<ChunkCoord>();
        readonly HashSet<ChunkCoord> _meshedOnce = new HashSet<ChunkCoord>();
        readonly ConcurrentQueue<ChunkCoord> _integrationQueue = new ConcurrentQueue<ChunkCoord>();
        readonly ConcurrentDictionary<ChunkCoord, byte> _integrationSet = new ConcurrentDictionary<ChunkCoord, byte>();
        readonly Dictionary<ChunkCoord, ChunkMeshJobHandle> _pendingMeshJobs = new Dictionary<ChunkCoord, ChunkMeshJobHandle>();
        readonly Dictionary<ChunkCoord, PendingCachedMesh> _pendingCachedMeshes = new Dictionary<ChunkCoord, PendingCachedMesh>();
        readonly Dictionary<ulong, CachedMeshEntry> _meshCache = new Dictionary<ulong, CachedMeshEntry>();
        List<(ulong hash, int vertexCount, int frame)> _evictCandidatesCache;
        readonly Dictionary<ChunkCoord, ulong> _chunkMeshHashes = new Dictionary<ChunkCoord, ulong>();
        /// <summary>Empty materials buffer for jobs; Allocator.Persistent, must be disposed in OnDestroy.</summary>
        NativeArray<ushort> _emptyMaterials;
        /// <summary>Reusable upload buffer for GPU SetVoxels (load path); avoids per-chunk array allocation.</summary>
        ushort[] _gpuUploadMaterials;
        readonly HashSet<ChunkCoord> _remeshAfterIntegration = new HashSet<ChunkCoord>();
        readonly Dictionary<ChunkCoord, int> _neighborDirtyFaces = new Dictionary<ChunkCoord, int>();
        readonly Dictionary<ChunkCoord, MeshData[]> _chunkFaceCache = new Dictionary<ChunkCoord, MeshData[]>();
        readonly Queue<ChunkCoord> _faceRemeshQueue = new Queue<ChunkCoord>();
        readonly HashSet<ChunkCoord> _faceRemeshSet = new HashSet<ChunkCoord>();
        readonly Dictionary<ChunkCoord, FaceMeshTask> _faceMeshJobs = new Dictionary<ChunkCoord, FaceMeshTask>();
        readonly List<DeferredMeshComplete> _deferredMeshComplete = new List<DeferredMeshComplete>();
        readonly Dictionary<ChunkCoord, int> _pendingLodStep = new Dictionary<ChunkCoord, int>();
        /* ChunkOctreeLodManager _octreeLodManager; */
        NativeList<RemoveCandidate> _removeCandidates;
        /// <summary>Reused buffers for DropWorkQueues; Clear() before use.</summary>
        readonly List<ChunkCoord> _dropPendingKeep = new List<ChunkCoord>();
        readonly List<ChunkCoord> _dropPreloadKeep = new List<ChunkCoord>();
        readonly List<ChunkCoord> _dropRemeshKeep = new List<ChunkCoord>();
        readonly List<(ChunkCoord coord, int mask)> _dropFaceRemeshKeep = new List<(ChunkCoord, int)>();
        readonly List<ChunkCoord> _dropFaceMeshStale = new List<ChunkCoord>();
        readonly List<ChunkCoord> _dropStale = new List<ChunkCoord>();
        readonly List<ChunkCoord> _dropCachedStale = new List<ChunkCoord>();
        readonly List<ChunkCoord> _dropRemeshAfter = new List<ChunkCoord>();
        int _integrationsLastFrame;
        ChunkPool _pool;
        IChunkGenerator _generator;
        long _lastGenMs;
        long _lastMeshMs;
        long _lastTotalMs;
        long _lastIntegrationMs;
        ChunkCoord _lastSpawnCoord;
        ChunkCoord _lastPendingCenter;
        bool _hasPendingCenter;
        int _spawnedLastFrame;
        int _streamingEpoch;
        int _baseMaxGenJobsInFlight;
        int _baseMaxMeshJobsInFlight;
        int _baseMaxIntegrationsPerFrame;
        int _baseMaxPreloadsPerFrame;
        int _runtimeMaxGenJobsInFlight;
        int _runtimeMaxMeshJobsInFlight;
        int _runtimeMaxIntegrationsPerFrame;
        int _runtimeMaxPreloadsPerFrame;
        double _adaptiveUntil;
        bool _adaptiveInitialized;
        ChunkCoord _lastDropCenter;
        bool _hasDropCenter;
        Vector3 _lastDropForward;
        bool _hasDropForward;
        double _lastDropTime;
        bool _warnedLodStepMismatch;
        static bool _warnedChunkPrefabNull;
        static bool _warnedPlayerWorldGenNull;
        static bool _warnedStreamingPaused;
        static bool _warnedGpuNotInitialized;
        static bool _warnedGpuSlotsFull;
        static bool _warnedGpuCamNull;
        static bool _warnedColumnChunksZero;
        static bool _warnedInvalidChunkLayer;
        static bool _warnedInstancedShaderForCpu;
        Material _cpuFallbackMaterial;

        bool _safeSpawnInitialized;
        int _safeSpawnWorldX0;
        int _safeSpawnWorldZ0;
        int _safeSpawnSizeVoxels;
        int _safeSpawnBaseY;
        int _safeSpawnTopY;
        bool _pendingSafeSpawnSnap;
        bool _waitingSafeSpawnMesh;
        ChunkCoord _safeSpawnAnchorCoord;
        bool _playerFrozenForSafeSpawn;
        bool _savedPlayerControllerEnabled;
        bool _savedCharacterControllerEnabled;
        double _safeSpawnWaitStart;
        int _rebuildNeighborsDepth;
        int _requestRemeshDepth;

        Context _context;
        ChunkLoader _loader;
        ChunkJobsManager _jobs;
        ChunkIntegrationManager _integration;
        /* ChunkLodManager _lod; */
        /* ChunkCacheManager _cache; */
        /* ChunkAdaptiveLimitsManager _adaptive; */
        /* ChunkWorkDropManager _workDrop; */
        ChunkSafeSpawnManager _safeSpawn;
        /* ChunkPhysicsManager _physics; */

        /* GpuWorldState _gpuWorldState; */
        /* GpuChunkGenerator _gpuChunkGenerator; */
        /* GpuChunkAnalyzer _gpuChunkAnalyzer; */
        /* GpuMesher _gpuMesher; */
        /* GpuCuller _gpuCuller; */
        /* GpuReadbackManager _gpuReadbackManager; */
        /* GpuColliderReadbackQueue _gpuColliderReadbackQueue; */

        bool HasAnySolid(ChunkData data)
        {
            if (_integration != null)
                return _integration.HasAnySolid(data);
            if (!data.Materials.IsCreated) return false;
            var mats = data.Materials;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] != 0) return true;
            }
            return false;
        }

        internal struct RemoveCandidate : System.IComparable<RemoveCandidate>
        {
            public ChunkCoord Coord;
            public int Distance;

            public RemoveCandidate(ChunkCoord coord, int distance)
            {
                Coord = coord;
                Distance = distance;
            }

            public int CompareTo(RemoveCandidate other) => other.Distance.CompareTo(Distance);
        }

        internal struct GenTask
        {
            public ChunkCoord Coord;
            public Chunk Chunk;
            public ChunkGenJobHandle Job;
            public double StartTime;
            public double SpawnStart;
            public bool ApplySafeSpawn;
            public bool ApplyDelta;
            public int Epoch;
            public bool UseSlices;
            public int SliceIndex;
            public int SliceCount;
            public int SliceSize;
            public int LodStepOverride;
        }

        internal struct MeshTask
        {
            public ChunkCoord Coord;
            public Chunk Chunk;
            public ChunkMeshJobHandle Job;
            public double StartTime;
            public double SpawnStart;
        }

        internal struct FaceMeshTask
        {
            public ChunkCoord Coord;
            public Chunk Chunk;
            public FaceMeshJobHandle Job;
            public int FaceMask;
        }

        internal struct DeferredMeshComplete
        {
            public ChunkCoord Coord;
            public MeshTask Task;
        }

        internal struct CachedChunkData
        {
            public NativeArray<ushort> Materials;
            public NativeArray<float> Density;
            public int Size;
            public bool HasDensity;

            public bool IsValid => Materials.IsCreated;

            public void CopyFrom(ChunkData source)
            {
                Size = source.Size;
                HasDensity = source.Density.IsCreated;
                
                if (!Materials.IsCreated || Materials.Length != source.Materials.Length)
                {
                    if (Materials.IsCreated) Materials.Dispose();
                    Materials = new NativeArray<ushort>(source.Materials.Length, Allocator.Persistent);
                }
                NativeArray<ushort>.Copy(source.Materials, Materials);

                if (HasDensity)
                {
                    if (!Density.IsCreated || Density.Length != source.Density.Length)
                    {
                        if (Density.IsCreated) Density.Dispose();
                        Density = new NativeArray<float>(source.Density.Length, Allocator.Persistent);
                    }
                    NativeArray<float>.Copy(source.Density, Density);
                }
                else
                {
                    if (Density.IsCreated) Density.Dispose();
                    Density = default;
                }
            }

            public void CopyTo(ChunkData target)
            {
                if (!IsValid) return;
                if (target.Size != Size) return;
                if (target.Materials.Length != Materials.Length) return;

                NativeArray<ushort>.Copy(Materials, target.Materials);
                if (HasDensity && target.Density.IsCreated && Density.IsCreated)
                {
                    if (target.Density.Length == Density.Length)
                        NativeArray<float>.Copy(Density, target.Density);
                }
            }

            public void Dispose()
            {
                if (Materials.IsCreated) Materials.Dispose();
                if (Density.IsCreated) Density.Dispose();
            }
        }

        internal struct CachedMeshEntry
        {
            public Mesh Mesh;
            public int RefCount;
            public int LastUsedFrame;
        }

        internal struct PendingCachedMesh
        {
            public Mesh Mesh;
            public ulong Hash;
            public int Epoch;
        }

        public int ActiveCount => _active.Count;
        public int PendingCount => _pendingSet.Count;
        /// <summary>Raw pending set count; when viewCone enabled, use with PendingCount so loop runs if either has entries (recovery from out-of-sync).</summary>
        public int PendingSetCount => _pendingSet.Count;
        public int SpawnedLastFrame => _spawnedLastFrame;
        public int IntegrationQueueCount => _integrationQueue.Count;
        public int IntegrationsLastFrame => _integrationsLastFrame;
        public int GenJobsCount => _genJobs.Count;
        public int MeshJobsCount => _meshJobs.Count;
        public int PreloadQueueCount => _preload.Count;
        public int PreloadedCount => _preloaded.Count;
        public int RemeshQueueCount => _remeshSet.Count;
        public int RemoveQueueCount => _removeQueue.Count;
        public int LoadRadius => loadRadius;
        public int MaxSpawnsPerFrame => maxSpawnsPerFrame;
        public int MaxRemeshPerFrame => maxRemeshPerFrame;
        public int ChunkSize => worldGen != null ? worldGen.ChunkSize : 0;
        public int ColumnChunks => worldGen != null ? worldGen.ColumnChunks : 0;
        public float VoxelSize => worldGen != null ? worldGen.VoxelSize : VoxelConstants.VoxelSize;
        public bool AddColliders => addColliders;
        public bool StreamingPaused => streamingPaused;
        public ChunkCoord LastSpawnCoord => _lastSpawnCoord;
        public long LastGenMs => _lastGenMs;
        public long LastMeshMs => _lastMeshMs;
        public long LastTotalMs => _lastTotalMs;
        public long LastIntegrationMs => _lastIntegrationMs;
        public Transform PlayerTransform => player;
        public bool UseGpuPipeline => useGpuPipeline;
        public SrpBatchingConfig SrpBatchingConfig => srpBatchingConfig;
        /* public GpuWorldState GpuWorldState => _gpuWorldState; */
        /* public GpuReadbackManager GpuReadbackManager => _gpuReadbackManager; */
        public IEnumerable<KeyValuePair<ChunkCoord, Chunk>> ActiveChunks => _active;
        public bool IsPreloaded(ChunkCoord coord) => _preloaded.Contains(coord);
        public int CachedChunksCount => _dataCache.Count;
        int CurrentMaxGenJobsInFlight => enableAdaptiveLimits ? _runtimeMaxGenJobsInFlight : maxGenJobsInFlight;
        int CurrentMaxMeshJobsInFlight => enableAdaptiveLimits ? _runtimeMaxMeshJobsInFlight : maxMeshJobsInFlight;
        int CurrentMaxIntegrationsPerFrame => enableAdaptiveLimits ? _runtimeMaxIntegrationsPerFrame : maxIntegrationsPerFrame;
        int CurrentMaxPreloadsPerFrame => enableAdaptiveLimits ? _runtimeMaxPreloadsPerFrame : maxPreloadsPerFrame;

        bool IsInIntegrationSet(ChunkCoord coord)
        {
            if (_integration != null)
                return _integration.IsInIntegrationSet(coord);
            return _integrationSet.ContainsKey(coord);
        }

        internal void DataCacheEvictionAdd(ChunkCoord coord)
        {
            if (_dataCacheEvictionNodes.TryGetValue(coord, out var node))
            {
                _dataCacheEvictionList.Remove(node);
                _dataCacheEvictionNodes.Remove(coord);
            }
            var newNode = _dataCacheEvictionList.AddLast(coord);
            _dataCacheEvictionNodes[coord] = newNode;
        }

        internal bool DataCacheEvictionTryDequeue(out ChunkCoord coord)
        {
            if (_dataCacheEvictionList.First == null)
            {
                coord = default;
                return false;
            }
            coord = _dataCacheEvictionList.First.Value;
            _dataCacheEvictionList.RemoveFirst();
            _dataCacheEvictionNodes.Remove(coord);
            return true;
        }

        internal void DataCacheEvictionRemove(ChunkCoord coord)
        {
            if (!_dataCacheEvictionNodes.TryGetValue(coord, out var node)) return;
            _dataCacheEvictionList.Remove(node);
            _dataCacheEvictionNodes.Remove(coord);
        }

        internal int DataCacheEvictionCount => _dataCacheEvictionList.Count;

        public void SetPlayer(Transform newPlayer)
        {
            player = newPlayer;
        }

        public void SetRuntimeSettings(int newRadius, int newMaxSpawnsPerFrame, bool newAddColliders)
        {
            loadRadius = newRadius;
            maxSpawnsPerFrame = newMaxSpawnsPerFrame;
            SetCollidersEnabled(newAddColliders);
        }

        public void SetStreamingPaused(bool paused)
        {
            streamingPaused = paused;
        }

        public void SetCollidersEnabled(bool enabled)
        {
            addColliders = enabled;
            foreach (var chunk in _active.Values)
            {
                if (chunk == null) continue;
                if (_preloaded.Contains(chunk.Coord))
                {
                    chunk.SetColliderEnabled(false);
                    continue;
                }
                chunk.SetColliderEnabled(enabled);
            }
        }

        /// <summary>Freezes/unfreezes player for safe spawn. Looks for PlayerSimpleController (by type name) and CharacterController; optional — no error if missing.</summary>
        void SetPlayerFrozen(bool frozen)
        {
            _safeSpawn?.SetPlayerFrozen(frozen);
        }

        void Awake()
        {
            if (!EnsurePrefab() || chunkPrefab == null)
            {
                Debug.LogError("[ChunkManager] Awake: Chunk prefab is null. Assign chunkPrefab in Inspector or ensure EnsurePrefab can create one.");
                return;
            }
            if (worldGen != null && worldGen.ChunkSize <= 0)
                Debug.LogWarning("[ChunkManager] worldGen.ChunkSize must be > 0; division by zero may occur in position math. Fix WorldGenConfig.");
            _pool = new ChunkPool(chunkPrefab, transform);
            _pool.UseGpuPipeline = false; /* CPU-only rollback */
            _generator = new ChunkGenerator();
            if (!useGpuPipeline && _generator is ChunkGenerator cg)
                cg.UseGpu = false;
            /* if (saveManager == null) saveManager = GetComponent<ChunkSaveManager>(); */
            /* if (modManager == null) modManager = GetComponent<ChunkModManager>(); */
            /* if (hybridSave == null) hybridSave = GetComponent<ChunkHybridSaveManager>(); */
            /* if (physicsOptimizer == null) physicsOptimizer = GetComponent<ChunkPhysicsOptimizer>(); */
            /* if (viewCone == null) viewCone = GetComponent<ChunkViewConePrioritizer>(); */
            if (occlusionCuller == null) occlusionCuller = GetComponent<ChunkOcclusionCuller>();
            /* if (svoManager == null) svoManager = GetComponent<SvoManager>(); */
            if (!_emptyMaterials.IsCreated)
                _emptyMaterials = new NativeArray<ushort>(0, Allocator.Persistent);
            _removeCandidates = new NativeList<RemoveCandidate>(256, Allocator.Persistent);

            _context = new Context(this);
            /* CPU-only rollback: закоментовано
            _cache = new ChunkCacheManager(_context);
            _lod = new ChunkLodManager(_context);
            _adaptive = new ChunkAdaptiveLimitsManager(_context);
            _workDrop = new ChunkWorkDropManager(_context);
            _physics = new ChunkPhysicsManager(_context);
            _context.Cache = _cache;
            _context.Lod = _lod;
            _context.Adaptive = _adaptive;
            _context.WorkDrop = _workDrop;
            _context.Physics = _physics;
            */
            _safeSpawn = new ChunkSafeSpawnManager(_context);
            _context.SafeSpawn = _safeSpawn;
            _jobs = new ChunkJobsManager(_context);
            _integration = new ChunkIntegrationManager(_context);
            _loader = new ChunkLoader(_context);
            _context.Jobs = _jobs;
            _context.Integration = _integration;
            _context.Loader = _loader;

            InitAdaptiveLimits();
            if (srpBatchingConfig != null)
                srpBatchingConfig.Configure();
            else
                ConfigureVoxelMaterialLegacy();

            /* CPU-only rollback: GPU init вимкнено
            if (useGpuPipeline && worldGen != null)
            {
                int chunkSize = worldGen.ChunkSize;
                _gpuWorldState = new GpuWorldState(gpuMaxChunks, chunkSize);
                _gpuReadbackManager = new GpuReadbackManager();
                ComputeShader voxelGen = gpuPipelineComputeAssets != null && gpuPipelineComputeAssets.HasAll ? gpuPipelineComputeAssets.voxelGeneration : voxelGenerationCompute;
                ComputeShader chunkAnal = gpuPipelineComputeAssets != null && gpuPipelineComputeAssets.HasAll ? gpuPipelineComputeAssets.chunkAnalysis : chunkAnalysisCompute;
                ComputeShader chunkCull = gpuPipelineComputeAssets != null && gpuPipelineComputeAssets.HasAll ? gpuPipelineComputeAssets.chunkCulling : chunkCullingCompute;
                ComputeShader voxelMesh = gpuPipelineComputeAssets != null && gpuPipelineComputeAssets.HasAll ? gpuPipelineComputeAssets.voxelMeshing : voxelMeshingCompute;
                if (voxelGen != null)
                {
                    _gpuChunkGenerator = new GpuChunkGenerator();
                    _gpuChunkGenerator.Initialize(voxelGen);
                    if (_generator is ChunkGenerator cg)
                        cg.SetGpuPipeline(_gpuWorldState, _gpuChunkGenerator);
                }
                if (chunkAnal != null)
                {
                    _gpuChunkAnalyzer = new GpuChunkAnalyzer();
                    _gpuChunkAnalyzer.Initialize(chunkAnal);
                }
                if (chunkCull != null)
                {
                    _gpuCuller = new GpuCuller();
                    _gpuCuller.Initialize(chunkCull);
                    if (occlusionCuller != null)
                        occlusionCuller.SetGpuPipeline(_gpuWorldState, _gpuCuller, enableGpu: true);
                }
                if (voxelMesh != null)
                {
                    _gpuMesher = new GpuMesher();
                    _gpuMesher.Initialize(voxelMesh);
                }
                _gpuColliderReadbackQueue = new GpuColliderReadbackQueue(maxColliderReadbacksPerFrame, _gpuWorldState.MaxVerticesPerChunk);
                _gpuColliderReadbackQueue.SetWorldState(_gpuWorldState);
                _gpuColliderReadbackQueue.SetChunkResolver(c => _active.TryGetValue(c, out var ch) ? ch : null);
                if (gpuDrivenRenderer != null)
                {
                    gpuDrivenRenderer.SetWorldState(_gpuWorldState);
                    if (voxelMaterialLibrary != null)
                        gpuDrivenRenderer.ConfigureFromVoxelMaterial(voxelMaterialLibrary);
                }
                if (voxelGen == null || chunkCull == null || voxelMesh == null)
                    Debug.LogWarning("[ChunkManager] GPU pipeline enabled but one or more compute shaders missing (VoxelGeneration, ChunkCulling, VoxelMeshing). Assign all in Inspector or via GpuPipelineComputeAssets. GPU spawn/rendering will be limited until then.");
            }
            */
        }

        void ConfigureVoxelMaterialLegacy()
        {
            if (voxelMaterial == null || voxelMaterialLibrary == null) return;
            voxelMaterial.SetTexture("_MainTexArr", voxelMaterialLibrary.TextureArray);
            voxelMaterial.SetFloat("_TriplanarScale", voxelMaterialLibrary.TriplanarScale);
            voxelMaterial.SetFloat("_NormalStrength", voxelMaterialLibrary.NormalStrength);
            voxelMaterial.SetInt("_LayerIndex", voxelMaterialLibrary.DefaultLayerIndex);
        }

        /// <summary>Returns material for CPU-rendered chunks. When UseGpuPipeline is false and assigned material uses Instanced shader, returns fallback with TerraVoxel/VoxelTriplanarURP to avoid D3D12 buffer error.</summary>
        Material GetMaterialForChunk()
        {
            Material source = srpBatchingConfig != null ? srpBatchingConfig.Material : voxelMaterial;
            if (source == null) return null;
            if (!useGpuPipeline && source.shader != null && source.shader.name.Contains("Instanced"))
            {
                if (!_warnedInstancedShaderForCpu)
                {
                    _warnedInstancedShaderForCpu = true;
                    Debug.LogWarning("[ChunkManager] Voxel material uses Instanced shader but UseGpuPipeline is false. Using TerraVoxel/VoxelTriplanarURP fallback. Assign non-Instanced material in Inspector to avoid this.");
                }
                return GetCpuFallbackMaterial();
            }
            return source;
        }

        Material GetCpuFallbackMaterial()
        {
            if (_cpuFallbackMaterial != null) return _cpuFallbackMaterial;
            var shader = Shader.Find("TerraVoxel/VoxelTriplanarURP");
            if (shader == null) return voxelMaterial;
            _cpuFallbackMaterial = new Material(shader);
            if (voxelMaterialLibrary != null)
            {
                _cpuFallbackMaterial.SetTexture("_MainTexArr", voxelMaterialLibrary.TextureArray);
                _cpuFallbackMaterial.SetFloat("_TriplanarScale", voxelMaterialLibrary.TriplanarScale);
                _cpuFallbackMaterial.SetFloat("_NormalStrength", voxelMaterialLibrary.NormalStrength);
                _cpuFallbackMaterial.SetInt("_LayerIndex", voxelMaterialLibrary.DefaultLayerIndex);
            }
            return _cpuFallbackMaterial;
        }

        void InitAdaptiveLimits()
        {
            /* _adaptive?.InitAdaptiveLimits(); */
        }

        void Update()
        {
            if (player == null || worldGen == null)
            {
                if (!_warnedPlayerWorldGenNull)
                {
                    _warnedPlayerWorldGenNull = true;
                    Debug.LogWarning("[ChunkManager] Player or WorldGen is not assigned. Assign in Inspector to spawn chunks. Streaming disabled until then.");
                }
                return;
            }
            if (_pool == null)
            {
                if (EnsurePrefab() && chunkPrefab != null)
                {
                    _pool = new ChunkPool(chunkPrefab, transform);
                    _pool.UseGpuPipeline = false; /* CPU-only rollback */
                    if (_generator == null) _generator = new ChunkGenerator();
                }
                if (_pool == null) return;
            }
            if (chunkPrefab == null)
            {
                EnsurePrefab();
                if (chunkPrefab == null)
                {
                    if (!_warnedChunkPrefabNull)
                    {
                        _warnedChunkPrefabNull = true;
                        Debug.LogWarning("[ChunkManager] chunkPrefab is null and could not be auto-created. Streaming disabled.");
                    }
                    return;
                }
            }
            if (!_safeSpawnInitialized) TryInitSafeSpawn();
            if (_waitingSafeSpawnMesh && safeSpawnTimeoutSeconds > 0 && (Time.realtimeSinceStartupAsDouble - _safeSpawnWaitStart) > safeSpawnTimeoutSeconds)
            {
                SnapPlayerToSafeSpawn();
                SetPlayerFrozen(false);
                _waitingSafeSpawnMesh = false;
            }
            Profiler.BeginSample("ChunkManager.Update");
            /* CPU-only rollback: streamingBudget?.BeginFrame(); */
            _cacheOpsThisFrame = 0;
            /* UpdateAdaptiveLimits(); */
            if (!useGpuPipeline)
            {
                Profiler.BeginSample("ProcessGenJobs");
                if (_jobs != null) _jobs.ProcessGenJobs();
                else ProcessGenJobs();
                Profiler.EndSample();
                Profiler.BeginSample("ProcessMeshJobs");
                if (_jobs != null) _jobs.ProcessMeshJobs();
                else ProcessMeshJobs();
                Profiler.EndSample();
                if (_integration != null) _integration.ProcessIntegrationQueue();
            }
            if (streamingPaused)
            {
                if (!_warnedStreamingPaused)
                {
                    _warnedStreamingPaused = true;
                    Debug.LogWarning("[ChunkManager] Streaming is paused. No new chunks will spawn until Streaming Paused is unchecked.");
                }
                /* CPU-only rollback: enableEdgeOnlyRemesh
                if (enableEdgeOnlyRemesh)
                {
                    if (_jobs != null) _jobs.ProcessFaceMeshJobs();
                    else ProcessFaceMeshJobs();
                    if (_jobs != null) _jobs.ProcessFaceRemeshQueue();
                    else ProcessFaceRemeshQueue();
                }
                */
                if (_jobs != null) _jobs.ProcessRemeshQueue();
                else ProcessRemeshQueue();
                Profiler.EndSample();
                return;
            }
            Profiler.BeginSample("MaintainRadius");
            if (_loader != null) _loader.MaintainRadius();
            else MaintainRadius();
            Profiler.EndSample();
            Profiler.BeginSample("ProcessPending");
            if (_loader != null) _loader.ProcessPending();
            else ProcessPending();
            Profiler.EndSample();
            if (_loader != null) _loader.ProcessPreload();
            else ProcessPreload();
            if (_loader != null) _loader.ProcessRemovalQueue();
            else ProcessRemovalQueue();
            /* CPU-only rollback: enableEdgeOnlyRemesh
            if (enableEdgeOnlyRemesh)
            {
                if (_jobs != null) _jobs.ProcessFaceMeshJobs();
                else ProcessFaceMeshJobs();
                if (_jobs != null) _jobs.ProcessFaceRemeshQueue();
                else ProcessFaceRemeshQueue();
            }
            */
            if (_jobs != null) _jobs.ProcessRemeshQueue();
            else ProcessRemeshQueue();
            Profiler.BeginSample("OcclusionCuller");
            if (occlusionCuller != null)
                occlusionCuller.Tick(this);
            Profiler.EndSample();
            /* CPU-only rollback: LOD, far range, physics, UpdateGpuPipeline
            Profiler.BeginSample("ChunkLodManager");
            if (enableFullLod)
            {
                if (_lod != null) _lod.ProcessFullLod();
            }
            else
            {
                if (_lod != null) _lod.ProcessLodUpgrades();
            }
            Profiler.EndSample();
            if (enableFarRangeLod)
            {
                if (_lod != null) _lod.ProcessFarRangeLod();
                else ProcessFarRangeLod();
            }
            if (useGpuPipeline)
                UpdateGpuPipeline();
            if (_physics != null)
                _physics.Tick();
            else if (physicsOptimizer != null)
                physicsOptimizer.Tick(this);
            */

            /* PerformanceDiagnostics.Record(
                ActiveCount, PendingCount,
                _gpuWorldState?.ChunkCount ?? 0,
                _gpuColliderReadbackQueue?.PendingCount ?? 0,
                0, _lastGenMs, _lastMeshMs, 0, 0, 0,
                Time.unscaledDeltaTime * 1000f,
                -1f,
                (long)(UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver() / (1024 * 1024)),
                (long)(UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024)));
            */
            Profiler.EndSample();
        }

        void LateUpdate()
        {
            if (!useGpuPipeline && _deferredMeshComplete.Count > 0)
            {
                Profiler.BeginSample("ProcessDeferredMeshComplete");
                ProcessDeferredMeshComplete();
                Profiler.EndSample();
            }
        }

        /* GPU pipeline method fully commented out (CPU-only rollback)
        void UpdateGpuPipeline() { ... }
        */

        /// <summary>Resets limits to base each frame; reduces them if over gen/mesh/integration/memory/GPU threshold. Limits recover when not throttled (cooldown expires).</summary>
        void UpdateAdaptiveLimits()
        {
            /* _adaptive?.UpdateAdaptiveLimits(); */
        }

        int EffectiveUnloadRadius()
        {
            return Mathf.Max(unloadRadius, loadRadius + 1);
        }

        int EffectivePreloadRadius()
        {
            if (!enablePreload) return loadRadius;
            return Mathf.Max(preloadRadius, loadRadius);
        }

        bool BudgetExceeded()
        {
            return streamingBudget != null && streamingBudget.IsExceeded();
        }

        /// <summary>When player moved far (workDropDistance) or view angle changed (workDropAngleDeg) or move vs view (workDropMoveAngleDeg), drops queues after cooldown.</summary>
        void MaybeDropWork(ChunkCoord center)
        {
            /* _workDrop?.MaybeDropWork(center); */
        }

        Vector3 ResolveViewForward()
        {
            return Vector3.forward;
        }

        /// <summary>Clears or filters pending/preload/remove/integration queues; keeps only in-range remesh/mesh jobs and in-range pending/preload coords. MaintainRadius may still repopulate pending.</summary>
        void DropWorkQueues(ChunkCoord center)
        {
            int keepRadius = EffectiveUnloadRadius();
            if (enablePreload)
                keepRadius = Mathf.Max(keepRadius, EffectivePreloadRadius());

            _dropPendingKeep.Clear();
            _dropPreloadKeep.Clear();
            _dropRemeshKeep.Clear();
            _dropFaceRemeshKeep.Clear();
            _dropFaceMeshStale.Clear();
            _dropStale.Clear();
            _dropCachedStale.Clear();
            _dropRemeshAfter.Clear();

            foreach (var coord in _pendingSet)
            {
                if (IsWithinLoadRadius(coord, center, loadRadius))
                    _dropPendingKeep.Add(coord);
            }
            _pending.Clear();
            _pendingSet.Clear();
            for (int i = 0; i < _dropPendingKeep.Count; i++)
                _pendingSet.Add(_dropPendingKeep[i]);

            foreach (var coord in _preloadSet)
            {
                if (IsWithinLoadRadius(coord, center, EffectivePreloadRadius()))
                    _dropPreloadKeep.Add(coord);
            }
            _preload.Clear();
            _preloadSet.Clear();
            for (int i = 0; i < _dropPreloadKeep.Count; i++)
            {
                var c = _dropPreloadKeep[i];
                _preloadSet.Add(c);
                _preload.Enqueue(c);
            }
            _removeQueue.Clear();
            _removeSet.Clear();
            while (_integrationQueue.TryDequeue(out _)) { }
            _integrationSet.Clear();

            foreach (var coord in _remeshSet)
            {
                if (_active.ContainsKey(coord) && IsWithinKeepRadius(coord, center, keepRadius))
                    _dropRemeshKeep.Add(coord);
            }
            _remeshSet.Clear();
            for (int i = 0; i < _dropRemeshKeep.Count; i++)
                _remeshSet.Add(_dropRemeshKeep[i]);

            foreach (var coord in _faceRemeshSet)
            {
                if (_active.ContainsKey(coord) && IsWithinKeepRadius(coord, center, keepRadius))
                {
                    int mask = _neighborDirtyFaces.TryGetValue(coord, out int m) ? m : 0;
                    _dropFaceRemeshKeep.Add((coord, mask));
                }
                else
                {
                    ReleaseFaceCacheForChunk(coord);
                }
            }
            _faceRemeshQueue.Clear();
            _faceRemeshSet.Clear();
            _neighborDirtyFaces.Clear();
            for (int i = 0; i < _dropFaceRemeshKeep.Count; i++)
            {
                var (c, mask) = _dropFaceRemeshKeep[i];
                _neighborDirtyFaces[c] = mask;
                _faceRemeshSet.Add(c);
                _faceRemeshQueue.Enqueue(c);
            }

            foreach (var kvp in _faceMeshJobs)
            {
                var coord = kvp.Key;
                if (!_active.ContainsKey(coord) || !IsWithinKeepRadius(coord, center, keepRadius))
                {
                    kvp.Value.Job.Handle.Complete();
                    kvp.Value.Job.Dispose();
                    _dropFaceMeshStale.Add(coord);
                }
            }
            for (int i = 0; i < _dropFaceMeshStale.Count; i++)
                _faceMeshJobs.Remove(_dropFaceMeshStale[i]);

            foreach (var kvp in _pendingMeshJobs)
            {
                var coord = kvp.Key;
                if (!_active.ContainsKey(coord) || !IsWithinKeepRadius(coord, center, keepRadius))
                {
                    kvp.Value.Dispose();
                    _dropStale.Add(coord);
                }
                else
                {
                    if (_integrationSet.TryAdd(coord, 0))
                    {
                        _integrationQueue.Enqueue(coord);
                    }
                }
            }
            for (int i = 0; i < _dropStale.Count; i++)
                _pendingMeshJobs.Remove(_dropStale[i]);

            foreach (var kvp in _pendingCachedMeshes)
            {
                var coord = kvp.Key;
                if (!_active.ContainsKey(coord) || !IsWithinKeepRadius(coord, center, keepRadius))
                {
                    _dropCachedStale.Add(coord);
                }
                else
                {
                    if (_integrationSet.TryAdd(coord, 0))
                    {
                        _integrationQueue.Enqueue(coord);
                    }
                }
            }
            for (int i = 0; i < _dropCachedStale.Count; i++)
                _pendingCachedMeshes.Remove(_dropCachedStale[i]);

            foreach (var coord in _remeshAfterIntegration)
            {
                if (_active.ContainsKey(coord) && IsWithinKeepRadius(coord, center, keepRadius))
                    _dropRemeshAfter.Add(coord);
            }
            _remeshAfterIntegration.Clear();
            for (int i = 0; i < _dropRemeshAfter.Count; i++)
                _remeshAfterIntegration.Add(_dropRemeshAfter[i]);
        }

        /// <summary>Activates a preloaded chunk (renderer/collider). Handles chunk/mesh null; queues remesh if mesh missing or low-LOD.</summary>
        void OnDestroy()
        {
            CompleteAllJobs();
            /* _gpuChunkAnalyzer?.Dispose();
            _gpuChunkAnalyzer = null;
            _gpuMesher?.Dispose();
            _gpuMesher = null;
            _gpuWorldState?.Dispose();
            _gpuWorldState = null;
            if (_gpuColliderReadbackQueue != null)
            {
                _gpuColliderReadbackQueue.SetWorldState(null);
                _gpuColliderReadbackQueue.SetChunkResolver(null);
            }

            if (hybridSave != null)
                hybridSave.HandleAllChunksDestroyed(_active.Values);
            else
            {
                if (saveManager != null && saveManager.SaveOnDestroy)
                    saveManager.SaveAll(_active.Values);
                if (modManager != null && modManager.SaveOnDestroy)
                    modManager.SaveDirtyAll();
            } */

            // Dispose all chunk data to avoid Persistent allocator leaks on exit.
            foreach (var chunk in _active.Values)
            {
                if (chunk != null && chunk.Data.IsCreated)
                    chunk.Data.Dispose();
            }
            _active.Clear();

            // Dispose cached data
            foreach (var cached in _dataCache.Values)
            {
                cached.Dispose();
            }
            _dataCache.Clear();

            // Dispose cached meshes
            foreach (var entry in _meshCache.Values)
            {
                if (entry.Mesh != null)
                    Destroy(entry.Mesh);
            }
            _meshCache.Clear();
            _chunkMeshHashes.Clear();
            _pendingCachedMeshes.Clear();
            _remeshAfterIntegration.Clear();

            foreach (var kvp in _chunkFaceCache)
            {
                if (kvp.Value != null)
                {
                    for (int i = 0; i < kvp.Value.Length; i++)
                    {
                        if (kvp.Value[i].Vertices.IsCreated)
                            kvp.Value[i].Dispose();
                    }
                }
            }
            _chunkFaceCache.Clear();

            if (_emptyMaterials.IsCreated)
                _emptyMaterials.Dispose();
            if (_removeCandidates.IsCreated)
                _removeCandidates.Dispose();

            if (_cpuFallbackMaterial != null)
            {
                Destroy(_cpuFallbackMaterial);
                _cpuFallbackMaterial = null;
            }

            // Dispose any pooled/inactive chunks just in case.
            foreach (var chunk in GetComponentsInChildren<Chunk>(true))
            {
                if (chunk != null && chunk.Data.IsCreated)
                    chunk.Data.Dispose();
            }
        }

        /// <summary>Initializes safe spawn region and optionally freezes player until anchor chunk is meshed. Assumes chunks/mesh will be generated; timeout unfreezes if not ready.</summary>
        void TryInitSafeSpawn()
        {
            _safeSpawn?.TryInitSafeSpawn();
        }

        bool ApplySafeSpawnToChunk(Chunk chunk, ChunkCoord coord)
        {
            return _safeSpawn != null && _safeSpawn.ApplySafeSpawnToChunk(chunk, coord);
        }

        bool ReapplySafeSpawnToChunk(Chunk chunk, ChunkCoord coord, out bool changed)
        {
            if (_safeSpawn == null)
            {
                changed = false;
                return false;
            }
            return _safeSpawn.ReapplySafeSpawnToChunk(chunk, coord, out changed);
        }

        void SnapPlayerToSafeSpawn()
        {
            _safeSpawn?.SnapPlayerToSafeSpawn();
        }

    }
}

