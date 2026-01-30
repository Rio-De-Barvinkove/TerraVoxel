using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Generation;
using TerraVoxel.Voxel.Lod;
using TerraVoxel.Voxel.Meshing;
using TerraVoxel.Voxel.Occlusion;
using TerraVoxel.Voxel.Rendering;
using TerraVoxel.Voxel.Save;
using TerraVoxel.Voxel.Svo;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Serialization;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>
    /// Maintains active chunks around a tracked transform. Spawns limited count per frame.
    /// Monolithic: streaming, LOD, physics, caching, save, adaptive limits in one class (consider splitting into ChunkLoader/LodManager etc.).
    /// Intended to run on main thread (Update); job handles are only completed on main thread.
    /// _pendingSet + _pending duplicate coords for O(1) membership; data/mesh caches use eviction (LRU-style). NativeArray/Dispose is caller responsibility where applicable.
    /// </summary>
    public class ChunkManager : MonoBehaviour
    {
        [SerializeField] Transform player;
        [SerializeField] Chunk chunkPrefab;
        [SerializeField] WorldGenConfig worldGen;
        [SerializeField] NoiseStack noiseStack;
        [SerializeField] int loadRadius = 2;
        [SerializeField] int unloadRadius = 3;
        [SerializeField] bool addColliders = false;
        [Header("Physics")]
        [SerializeField] ChunkPhysicsOptimizer physicsOptimizer;
        [SerializeField] int maxSpawnsPerFrame = 1;
        [SerializeField] int maxRemeshPerFrame = 10;
        [SerializeField] int maxRemovalsPerFrame = 2;
        [SerializeField] int maxGenJobsInFlight = 2;
        [SerializeField] int maxMeshJobsInFlight = 2;
        [SerializeField] int maxIntegrationsPerFrame = 10;
        [SerializeField] bool dynamicIntegrationLimit = true;
        [SerializeField] int maxIntegrationQueueSize = 2000;
        [Header("Streaming Control")]
        [SerializeField] bool streamingPaused = false;
        [Header("Preload")]
        [SerializeField] bool enablePreload = false;
        [SerializeField] int preloadRadius = 4;
        [SerializeField] int maxPreloadsPerFrame = 1;
        [Header("Removal Budget")]
        [SerializeField] float removalBudgetMs = 0.75f;
        [Header("Work Dropping")]
        [Tooltip("If player moves this many chunks (XZ), consider dropping queues.")]
        [SerializeField] int workDropDistance = 8;
        [Tooltip("If view angle changes by this many degrees, consider dropping queues.")]
        [SerializeField] float workDropAngleDeg = 70f;
        [Tooltip("If move direction differs from view by this many degrees, consider dropping.")]
        [SerializeField] float workDropMoveAngleDeg = 70f;
        [SerializeField] float workDropCooldown = 0.5f;
        [Header("Pending Queue")]
        [SerializeField] int pendingQueueCap = 4096;
        [Tooltip("If player center moves this many chunks (XZ), pending queue is rebuilt.")]
        [SerializeField] int pendingResetDistance = 8;
        [Header("View Cone")]
        [SerializeField] ChunkViewConePrioritizer viewCone;
        [Header("Full LOD System")]
        [SerializeField] bool enableFullLod = false;
        [Tooltip("Far-range pipeline: render-only chunks beyond unloadRadius with low LOD/SVO (queue stub only).")]
        [SerializeField] bool enableFarRangeLod = false;
        [SerializeField] int farRangeRadius = 6;
        [SerializeField] ChunkLodSettings lodSettings;
        [SerializeField] int maxLodTransitionsPerFrame = 2;
        [SerializeField] float lodTransitionCooldown = 0.2f;
        [SerializeField] int maxSvoBuildsPerFrame = 1;
        [Header("Occlusion")]
        [SerializeField] ChunkOcclusionCuller occlusionCuller;
        [Header("SVO")]
        [SerializeField] SvoManager svoManager;
        [Header("Streaming Budget")]
        [SerializeField] StreamingTimeBudget streamingBudget = new StreamingTimeBudget();
        [SerializeField] string chunkLayerName = "Terrain";
        [SerializeField] ChunkSaveManager saveManager;
        [SerializeField] ChunkModManager modManager;
        [SerializeField] ChunkHybridSaveManager hybridSave;
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
        [SerializeField] int adaptiveDrawCallThreshold = 0;
        [SerializeField] long memoryPressureThresholdMb = 0;
        [Tooltip("Throttle streaming when graphics memory (MB) exceeds this. 0 = disabled.")]
        [SerializeField] long graphicsMemoryThresholdMb = 0;
        [Header("Safe Spawn")]
        [SerializeField] float safeSpawnTimeoutSeconds = 10f;
        [Header("Integration / Remesh Guards")]
        [SerializeField] int maxRebuildNeighborsDepth = 2;
        [SerializeField] int maxRequestRemeshNeighborsDepth = 1;

        readonly Dictionary<ChunkCoord, Chunk> _active = new Dictionary<ChunkCoord, Chunk>();
        readonly Dictionary<ChunkCoord, CachedChunkData> _dataCache = new Dictionary<ChunkCoord, CachedChunkData>();
        int _cacheOpsThisFrame;
        readonly Queue<ChunkCoord> _pending = new Queue<ChunkCoord>();
        readonly HashSet<ChunkCoord> _pendingSet = new HashSet<ChunkCoord>();
        readonly Queue<ChunkCoord> _preload = new Queue<ChunkCoord>();
        readonly Queue<ChunkCoord> _removeQueue = new Queue<ChunkCoord>();
        readonly Queue<ChunkCoord> _remeshQueue = new Queue<ChunkCoord>();
        readonly HashSet<ChunkCoord> _preloadSet = new HashSet<ChunkCoord>();
        readonly HashSet<ChunkCoord> _preloaded = new HashSet<ChunkCoord>();
        readonly Queue<ChunkCoord> _farRangeRenderQueue = new Queue<ChunkCoord>();
        readonly HashSet<ChunkCoord> _farRangeRenderSet = new HashSet<ChunkCoord>();
        readonly HashSet<ChunkCoord> _removeSet = new HashSet<ChunkCoord>();
        readonly HashSet<ChunkCoord> _remeshSet = new HashSet<ChunkCoord>();
        readonly Dictionary<ChunkCoord, GenTask> _genJobs = new Dictionary<ChunkCoord, GenTask>();
        readonly Dictionary<ChunkCoord, MeshTask> _meshJobs = new Dictionary<ChunkCoord, MeshTask>();
        readonly List<ChunkCoord> _genCompleted = new List<ChunkCoord>();
        readonly List<ChunkCoord> _meshCompleted = new List<ChunkCoord>();
        readonly HashSet<ChunkCoord> _meshedOnce = new HashSet<ChunkCoord>();
        readonly Queue<ChunkCoord> _integrationQueue = new Queue<ChunkCoord>();
        readonly HashSet<ChunkCoord> _integrationSet = new HashSet<ChunkCoord>();
        readonly Dictionary<ChunkCoord, ChunkMeshJobHandle> _pendingMeshJobs = new Dictionary<ChunkCoord, ChunkMeshJobHandle>();
        readonly Dictionary<ChunkCoord, PendingCachedMesh> _pendingCachedMeshes = new Dictionary<ChunkCoord, PendingCachedMesh>();
        readonly Dictionary<ulong, CachedMeshEntry> _meshCache = new Dictionary<ulong, CachedMeshEntry>();
        readonly Dictionary<ChunkCoord, ulong> _chunkMeshHashes = new Dictionary<ChunkCoord, ulong>();
        NativeArray<ushort> _emptyMaterials;
        readonly HashSet<ChunkCoord> _remeshAfterIntegration = new HashSet<ChunkCoord>();
        readonly List<RemoveCandidate> _removeCandidates = new List<RemoveCandidate>(256);
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
        readonly object _integrationLock = new object();
        int _rebuildNeighborsDepth;
        int _requestRemeshDepth;

        struct RemoveCandidate
        {
            public ChunkCoord Coord;
            public int Distance;

            public RemoveCandidate(ChunkCoord coord, int distance)
            {
                Coord = coord;
                Distance = distance;
            }
        }

        struct GenTask
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
        }

        struct MeshTask
        {
            public ChunkCoord Coord;
            public Chunk Chunk;
            public ChunkMeshJobHandle Job;
            public double StartTime;
            public double SpawnStart;
        }

        struct CachedChunkData
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

        struct CachedMeshEntry
        {
            public Mesh Mesh;
            public int RefCount;
            public int LastUsedFrame;
        }

        struct PendingCachedMesh
        {
            public Mesh Mesh;
            public ulong Hash;
            public int Epoch;
        }

        public int ActiveCount => _active.Count;
        public int PendingCount => (viewCone != null && viewCone.Enabled) ? viewCone.Count : _pending.Count;
        public int SpawnedLastFrame => _spawnedLastFrame;
        public int IntegrationQueueCount => _integrationQueue.Count;
        public int IntegrationsLastFrame => _integrationsLastFrame;
        public int GenJobsCount => _genJobs.Count;
        public int MeshJobsCount => _meshJobs.Count;
        public int PreloadQueueCount => _preload.Count;
        public int PreloadedCount => _preloaded.Count;
        public int RemeshQueueCount => _remeshQueue.Count;
        public int RemoveQueueCount => _removeQueue.Count;
        public int LoadRadius => loadRadius;
        public int MaxSpawnsPerFrame => maxSpawnsPerFrame;
        public int MaxRemeshPerFrame => maxRemeshPerFrame;
        public int ChunkSize => worldGen != null ? worldGen.ChunkSize : 0;
        public int ColumnChunks => worldGen != null ? worldGen.ColumnChunks : 0;
        public bool AddColliders => addColliders;
        public bool StreamingPaused => streamingPaused;
        public ChunkCoord LastSpawnCoord => _lastSpawnCoord;
        public long LastGenMs => _lastGenMs;
        public long LastMeshMs => _lastMeshMs;
        public long LastTotalMs => _lastTotalMs;
        public long LastIntegrationMs => _lastIntegrationMs;
        public Transform PlayerTransform => player;
        public IEnumerable<KeyValuePair<ChunkCoord, Chunk>> ActiveChunks => _active;
        public bool IsPreloaded(ChunkCoord coord) => _preloaded.Contains(coord);
        public int CachedChunksCount => _dataCache.Count;
        int CurrentMaxGenJobsInFlight => enableAdaptiveLimits ? _runtimeMaxGenJobsInFlight : maxGenJobsInFlight;
        int CurrentMaxMeshJobsInFlight => enableAdaptiveLimits ? _runtimeMaxMeshJobsInFlight : maxMeshJobsInFlight;
        int CurrentMaxIntegrationsPerFrame => enableAdaptiveLimits ? _runtimeMaxIntegrationsPerFrame : maxIntegrationsPerFrame;
        int CurrentMaxPreloadsPerFrame => enableAdaptiveLimits ? _runtimeMaxPreloadsPerFrame : maxPreloadsPerFrame;

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
            if (player == null) return;
            Behaviour controller = player.GetComponent("PlayerSimpleController") as Behaviour;
            if (controller == null)
            {
                var behaviours = player.GetComponentsInChildren<Behaviour>(true);
                for (int i = 0; i < behaviours.Length; i++)
                {
                    var b = behaviours[i];
                    if (b != null && b.GetType().Name == "PlayerSimpleController")
                    {
                        controller = b;
                        break;
                    }
                }
            }
            var cc = player.GetComponent<CharacterController>() ?? player.GetComponentInChildren<CharacterController>();

            if (frozen)
            {
                if (_playerFrozenForSafeSpawn) return;
                if (controller != null)
                {
                    _savedPlayerControllerEnabled = controller.enabled;
                    controller.enabled = false;
                }
                if (cc != null)
                {
                    _savedCharacterControllerEnabled = cc.enabled;
                    cc.enabled = false;
                }
                _playerFrozenForSafeSpawn = true;
            }
            else
            {
                if (!_playerFrozenForSafeSpawn) return;
                if (controller != null) controller.enabled = _savedPlayerControllerEnabled;
                if (cc != null) cc.enabled = _savedCharacterControllerEnabled;
                _playerFrozenForSafeSpawn = false;
            }
        }

        void Awake()
        {
            EnsurePrefab();
            _pool = new ChunkPool(chunkPrefab, transform);
            _generator = new ChunkGenerator();
            if (saveManager == null) saveManager = GetComponent<ChunkSaveManager>();
            if (modManager == null) modManager = GetComponent<ChunkModManager>();
            if (hybridSave == null) hybridSave = GetComponent<ChunkHybridSaveManager>();
            if (physicsOptimizer == null) physicsOptimizer = GetComponent<ChunkPhysicsOptimizer>();
            if (viewCone == null) viewCone = GetComponent<ChunkViewConePrioritizer>();
            if (occlusionCuller == null) occlusionCuller = GetComponent<ChunkOcclusionCuller>();
            if (svoManager == null) svoManager = GetComponent<SvoManager>();
            if (!_emptyMaterials.IsCreated)
                _emptyMaterials = new NativeArray<ushort>(0, Allocator.Persistent);
            InitAdaptiveLimits();
        }

        void InitAdaptiveLimits()
        {
            if (_adaptiveInitialized) return;
            _baseMaxGenJobsInFlight = maxGenJobsInFlight;
            _baseMaxMeshJobsInFlight = maxMeshJobsInFlight;
            _baseMaxIntegrationsPerFrame = maxIntegrationsPerFrame;
            _baseMaxPreloadsPerFrame = maxPreloadsPerFrame;
            _runtimeMaxGenJobsInFlight = _baseMaxGenJobsInFlight;
            _runtimeMaxMeshJobsInFlight = _baseMaxMeshJobsInFlight;
            _runtimeMaxIntegrationsPerFrame = _baseMaxIntegrationsPerFrame;
            _runtimeMaxPreloadsPerFrame = _baseMaxPreloadsPerFrame;
            _adaptiveInitialized = true;
        }

        void EnsurePrefab()
        {
            if (chunkPrefab == null)
            {
                var go = new GameObject("ChunkPrefab (auto)");
                chunkPrefab = go.AddComponent<Chunk>();
                go.SetActive(false);
            }
        }

        void Update()
        {
            if (player == null || worldGen == null) return;
            if (!_safeSpawnInitialized) TryInitSafeSpawn();
            if (_waitingSafeSpawnMesh && safeSpawnTimeoutSeconds > 0 && (Time.realtimeSinceStartupAsDouble - _safeSpawnWaitStart) > safeSpawnTimeoutSeconds)
            {
                SnapPlayerToSafeSpawn();
                SetPlayerFrozen(false);
                _waitingSafeSpawnMesh = false;
            }
            streamingBudget?.BeginFrame();
            _cacheOpsThisFrame = 0;
            UpdateAdaptiveLimits();
            ProcessGenJobs();
            ProcessMeshJobs();
            ProcessIntegrationQueue();
            if (streamingPaused)
            {
                ProcessRemeshQueue();
                return;
            }
            MaintainRadius();
            ProcessPending();
            ProcessPreload();
            ProcessRemovalQueue();
            ProcessRemeshQueue();
            if (enableFullLod)
                ProcessFullLod();
            else
                ProcessLodUpgrades();
            if (enableFarRangeLod)
                ProcessFarRangeLod();
            if (occlusionCuller != null)
                occlusionCuller.Tick(this);
            if (physicsOptimizer != null)
                physicsOptimizer.Tick(this);
        }

        /// <summary>Resets limits to base each frame; reduces them if over gen/mesh/integration/memory/GPU threshold. Limits recover when not throttled (cooldown expires).</summary>
        void UpdateAdaptiveLimits()
        {
            if (!enableAdaptiveLimits)
            {
                _runtimeMaxGenJobsInFlight = maxGenJobsInFlight;
                _runtimeMaxMeshJobsInFlight = maxMeshJobsInFlight;
                _runtimeMaxIntegrationsPerFrame = maxIntegrationsPerFrame;
                _runtimeMaxPreloadsPerFrame = maxPreloadsPerFrame;
                return;
            }

            InitAdaptiveLimits();
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < _adaptiveUntil)
                return;

            _runtimeMaxGenJobsInFlight = _baseMaxGenJobsInFlight;
            _runtimeMaxMeshJobsInFlight = _baseMaxMeshJobsInFlight;
            _runtimeMaxIntegrationsPerFrame = _baseMaxIntegrationsPerFrame;
            _runtimeMaxPreloadsPerFrame = _baseMaxPreloadsPerFrame;

            bool throttled = false;
            if (genSlowMs > 0 && _lastGenMs > genSlowMs)
            {
                _runtimeMaxGenJobsInFlight = Mathf.Max(1, _baseMaxGenJobsInFlight / 2);
                throttled = true;
            }
            if (meshSlowMs > 0 && _lastMeshMs > meshSlowMs)
            {
                _runtimeMaxMeshJobsInFlight = Mathf.Max(1, _baseMaxMeshJobsInFlight / 2);
                throttled = true;
            }
            if (integrationSlowMs > 0 && _lastIntegrationMs > integrationSlowMs)
            {
                _runtimeMaxIntegrationsPerFrame = Mathf.Max(1, _baseMaxIntegrationsPerFrame / 2);
                _runtimeMaxPreloadsPerFrame = 0;
                throttled = true;
            }

            if (memoryPressureThresholdMb > 0)
            {
#if UNITY_EDITOR || true
                long memMb = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024);
                if (memMb > memoryPressureThresholdMb)
                {
                    _runtimeMaxGenJobsInFlight = Mathf.Max(1, _baseMaxGenJobsInFlight / 2);
                    _runtimeMaxMeshJobsInFlight = Mathf.Max(1, _baseMaxMeshJobsInFlight / 2);
                    _runtimeMaxIntegrationsPerFrame = Mathf.Max(1, _baseMaxIntegrationsPerFrame / 2);
                    throttled = true;
                }
#endif
            }

            if (graphicsMemoryThresholdMb > 0 && SystemInfo.graphicsMemorySize > 0)
            {
                long gpuMb = SystemInfo.graphicsMemorySize;
                if (gpuMb > graphicsMemoryThresholdMb)
                {
                    _runtimeMaxMeshJobsInFlight = Mathf.Max(1, _baseMaxMeshJobsInFlight / 2);
                    _runtimeMaxIntegrationsPerFrame = Mathf.Max(1, _baseMaxIntegrationsPerFrame / 2);
                    throttled = true;
                }
            }

            if (throttled && adaptiveCooldown > 0f)
                _adaptiveUntil = now + adaptiveCooldown;
            // Limits recover next frame: base values are reapplied at start of UpdateAdaptiveLimits, then reduced only if over threshold.
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
            if (workDropDistance <= 0 && workDropAngleDeg <= 0f)
            {
                _lastDropCenter = center;
                _hasDropCenter = true;
                _lastDropForward = ResolveViewForward();
                _hasDropForward = true;
                return;
            }

            bool drop = false;
            if (_hasDropCenter && workDropDistance > 0)
            {
                int dx = Mathf.Abs(center.X - _lastDropCenter.X);
                int dz = Mathf.Abs(center.Z - _lastDropCenter.Z);
                if (dx > workDropDistance || dz > workDropDistance)
                    drop = true;
            }

            Vector3 forward = ResolveViewForward();
            if (_hasDropForward && workDropAngleDeg > 0f)
            {
                float angle = Vector3.Angle(_lastDropForward, forward);
                if (angle >= workDropAngleDeg)
                    drop = true;
            }

            if (!drop && _hasDropCenter && workDropMoveAngleDeg > 0f)
            {
                Vector3 move = new Vector3(center.X - _lastDropCenter.X, 0f, center.Z - _lastDropCenter.Z);
                if (move.sqrMagnitude > 0.0001f)
                {
                    move.Normalize();
                    float moveAngle = Vector3.Angle(forward, move);
                    if (moveAngle >= workDropMoveAngleDeg)
                        drop = true;
                }
            }

            if (drop)
            {
                double now = Time.realtimeSinceStartupAsDouble;
                if (workDropCooldown <= 0f || now - _lastDropTime >= workDropCooldown)
                {
                    _lastDropTime = now;
                    _streamingEpoch++;
                    DropWorkQueues(center);
                }
            }

            _lastDropCenter = center;
            _hasDropCenter = true;
            if (forward.sqrMagnitude > 0.0001f)
            {
                _lastDropForward = forward;
                _hasDropForward = true;
            }
        }

        Vector3 ResolveViewForward()
        {
            Vector3 forward = Vector3.forward;
            if (Camera.main != null)
                forward = Camera.main.transform.forward;
            else if (player != null)
                forward = player.forward;

            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                forward = Vector3.forward;
            else
                forward.Normalize();
            return forward;
        }

        /// <summary>Clears pending/preload/remove/integration queues and keeps only in-range remesh/mesh jobs. Does not check if chunks are still needed for render/physics; MaintainRadius repopulates pending.</summary>
        void DropWorkQueues(ChunkCoord center)
        {
            _pending.Clear();
            _pendingSet.Clear();
            _preload.Clear();
            _preloadSet.Clear();
            _removeQueue.Clear();
            _removeSet.Clear();
            _integrationQueue.Clear();
            _integrationSet.Clear();

            int keepRadius = EffectiveUnloadRadius();
            if (enablePreload)
                keepRadius = Mathf.Max(keepRadius, EffectivePreloadRadius());

            var remeshKeep = new List<ChunkCoord>();
            foreach (var coord in _remeshSet)
            {
                if (_active.ContainsKey(coord) && IsWithinKeepRadius(coord, center, keepRadius))
                    remeshKeep.Add(coord);
            }
            _remeshQueue.Clear();
            _remeshSet.Clear();
            for (int i = 0; i < remeshKeep.Count; i++)
            {
                _remeshSet.Add(remeshKeep[i]);
                _remeshQueue.Enqueue(remeshKeep[i]);
            }

            var stale = new List<ChunkCoord>();
            foreach (var kvp in _pendingMeshJobs)
            {
                var coord = kvp.Key;
                if (!_active.ContainsKey(coord) || !IsWithinKeepRadius(coord, center, keepRadius))
                {
                    kvp.Value.Dispose();
                    stale.Add(coord);
                }
                else
                {
                    lock (_integrationLock)
                    {
                        if (!_integrationSet.Contains(coord))
                        {
                            _integrationQueue.Enqueue(coord);
                            _integrationSet.Add(coord);
                        }
                    }
                }
            }
            for (int i = 0; i < stale.Count; i++)
                _pendingMeshJobs.Remove(stale[i]);

            var cachedStale = new List<ChunkCoord>();
            foreach (var kvp in _pendingCachedMeshes)
            {
                var coord = kvp.Key;
                if (!_active.ContainsKey(coord) || !IsWithinKeepRadius(coord, center, keepRadius))
                {
                    cachedStale.Add(coord);
                }
                else
                {
                    lock (_integrationLock)
                    {
                        if (!_integrationSet.Contains(coord))
                        {
                            _integrationQueue.Enqueue(coord);
                            _integrationSet.Add(coord);
                        }
                    }
                }
            }
            for (int i = 0; i < cachedStale.Count; i++)
                _pendingCachedMeshes.Remove(cachedStale[i]);

            var remeshAfter = new List<ChunkCoord>();
            foreach (var coord in _remeshAfterIntegration)
            {
                if (_active.ContainsKey(coord) && IsWithinKeepRadius(coord, center, keepRadius))
                    remeshAfter.Add(coord);
            }
            _remeshAfterIntegration.Clear();
            for (int i = 0; i < remeshAfter.Count; i++)
                _remeshAfterIntegration.Add(remeshAfter[i]);
        }

        /// <summary>Activates a preloaded chunk (renderer/collider). Handles chunk/mesh null; queues remesh if mesh missing or low-LOD.</summary>
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
            
            // Force remesh if chunk has low-LOD mesh when activating (fixes "cubes that don't transform back")
            if (chunk.IsLowLod || (enableReverseLod && reverseLodStep > 1))
            {
                chunk.IsLowLod = false;
                chunk.LodStartTime = 0;
                QueueRemesh(coord);
            }
        }

        void OnDestroy()
        {
            CompleteAllJobs();

            if (hybridSave != null)
                hybridSave.HandleAllChunksDestroyed(_active.Values);
            else
            {
                if (saveManager != null && saveManager.SaveOnDestroy)
                    saveManager.SaveAll(_active.Values);
                if (modManager != null && modManager.SaveOnDestroy)
                    modManager.SaveDirtyAll();
            }

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

            if (_emptyMaterials.IsCreated)
                _emptyMaterials.Dispose();

            // Dispose any pooled/inactive chunks just in case.
            foreach (var chunk in GetComponentsInChildren<Chunk>(true))
            {
                if (chunk != null && chunk.Data.IsCreated)
                    chunk.Data.Dispose();
            }
        }

        void CompleteAllJobs()
        {
            foreach (var kvp in _genJobs)
            {
                var job = kvp.Value.Job;
                job.Handle.Complete();
                job.Dispose();
            }
            foreach (var kvp in _meshJobs)
            {
                var job = kvp.Value.Job;
                job.Handle.Complete();
                job.Dispose();
            }
            _genJobs.Clear();
            _meshJobs.Clear();
            _genCompleted.Clear();
            _meshCompleted.Clear();
        }

        void MaintainRadius()
        {
            ChunkCoord center = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize);
            MaybeDropWork(center);
            if (ShouldRebuildPending(center))
                RebuildPendingQueue(center);
            int effectivePreloadRadius = EffectivePreloadRadius();

            for (int dz = -loadRadius; dz <= loadRadius; dz++)
            {
                for (int dx = -loadRadius; dx <= loadRadius; dx++)
                {
                    for (int dy = 0; dy < worldGen.ColumnChunks; dy++)
                    {
                        var coord = new ChunkCoord(center.X + dx, dy, center.Z + dz);
                        if (_active.TryGetValue(coord, out var existing))
                        {
                            if (_preloaded.Contains(coord))
                                ActivatePreloadedChunk(coord, existing);
                            continue;
                        }
                        if (_pendingSet.Contains(coord)) continue;
                        if (pendingQueueCap > 0 && PendingCount >= pendingQueueCap)
                            DropOnePendingOldest();
                        if (viewCone != null && viewCone.Enabled)
                            viewCone.EnqueueWithPriority(coord, center, player);
                        else
                            _pending.Enqueue(coord);
                        _pendingSet.Add(coord);
                    }
                }
            }

            if (enablePreload && effectivePreloadRadius > loadRadius)
            {
                for (int dz = -effectivePreloadRadius; dz <= effectivePreloadRadius; dz++)
                {
                    for (int dx = -effectivePreloadRadius; dx <= effectivePreloadRadius; dx++)
                    {
                        if (Mathf.Abs(dx) <= loadRadius && Mathf.Abs(dz) <= loadRadius) continue;
                        for (int dy = 0; dy < worldGen.ColumnChunks; dy++)
                        {
                            var coord = new ChunkCoord(center.X + dx, dy, center.Z + dz);
                            if (_active.ContainsKey(coord)) continue;
                            if (_pendingSet.Contains(coord)) continue;
                            if (_preloadSet.Contains(coord)) continue;
                            _preload.Enqueue(coord);
                            _preloadSet.Add(coord);
                        }
                    }
                }
            }

            int keepRadius = EffectiveUnloadRadius();
            if (enablePreload)
                keepRadius = Mathf.Max(keepRadius, effectivePreloadRadius);

            _removeCandidates.Clear();
            foreach (var kvp in _active)
            {
                if (IsWithinKeepRadius(kvp.Key, center, keepRadius)) continue;
                int dx = kvp.Key.X - center.X;
                int dy = kvp.Key.Y - center.Y;
                int dz = kvp.Key.Z - center.Z;
                int dist = dx * dx + dy * dy + dz * dz;
                _removeCandidates.Add(new RemoveCandidate(kvp.Key, dist));
            }

            _removeCandidates.Sort((a, b) => b.Distance.CompareTo(a.Distance));
            foreach (var c in _removeCandidates)
                QueueRemoval(c.Coord);

            if (enableFarRangeLod && farRangeRadius > keepRadius)
            {
                int farR = Mathf.Min(farRangeRadius, 32);
                for (int dz = -farR; dz <= farR; dz++)
                for (int dx = -farR; dx <= farR; dx++)
                {
                    int dist = dx * dx + dz * dz;
                    if (dist <= keepRadius * keepRadius) continue;
                    if (dist > farR * farR) continue;
                    for (int dy = 0; dy < worldGen.ColumnChunks; dy++)
                    {
                        var coord = new ChunkCoord(center.X + dx, dy, center.Z + dz);
                        if (_active.ContainsKey(coord)) continue;
                        if (_farRangeRenderSet.Add(coord))
                            _farRangeRenderQueue.Enqueue(coord);
                    }
                }
            }
        }

        /// <summary>Stub: render-only chunks beyond unloadRadius with low LOD/SVO not yet implemented. Queue capped to avoid unbounded growth.</summary>
        void ProcessFarRangeLod()
        {
            const int farRangeQueueCap = 1024;
            while (_farRangeRenderQueue.Count > farRangeQueueCap)
            {
                var coord = _farRangeRenderQueue.Dequeue();
                _farRangeRenderSet.Remove(coord);
            }
        }

        void ProcessPending()
        {
            if (player == null || worldGen == null) return;
            ChunkCoord center = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize);

            int spawned = 0;
            while (PendingCount > 0 && spawned < maxSpawnsPerFrame)
            {
                if (BudgetExceeded()) break;
                if (_genJobs.Count >= CurrentMaxGenJobsInFlight) break;
                if (!TryDequeuePending(center, out var coord))
                    break;
                if (!IsWithinLoadRadius(coord, center, loadRadius)) continue;
                if (_active.ContainsKey(coord)) continue;
                // Work dropping: skip spawning out-of-view-cone chunks (they get re-queued by MaintainRadius)
                if (viewCone != null && viewCone.Enabled && workDropAngleDeg > 0f && !viewCone.IsInViewCone(coord, center, player))
                    continue;
                SpawnChunk(coord);
                spawned++;
            }
            _spawnedLastFrame = spawned;
        }

        void ProcessPreload()
        {
            if (!enablePreload) return;
            if (player == null || worldGen == null) return;
            if (_preload.Count == 0) return;
            if (BudgetExceeded()) return;

            ChunkCoord center = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize);
            int effectivePreloadRadius = EffectivePreloadRadius();

            int spawned = 0;
            while (_preload.Count > 0 && spawned < CurrentMaxPreloadsPerFrame)
            {
                if (BudgetExceeded()) break;
                if (_genJobs.Count >= CurrentMaxGenJobsInFlight) break;
                var coord = _preload.Dequeue();
                _preloadSet.Remove(coord);

                if (!IsWithinLoadRadius(coord, center, effectivePreloadRadius)) continue;
                if (IsWithinLoadRadius(coord, center, loadRadius))
                {
                    if (!_active.ContainsKey(coord) && !_pendingSet.Contains(coord))
                    {
                        if (viewCone != null && viewCone.Enabled)
                            viewCone.EnqueueWithPriority(coord, center, player);
                        else
                            _pending.Enqueue(coord);
                        _pendingSet.Add(coord);
                    }
                    continue;
                }
                if (_active.ContainsKey(coord)) continue;

                SpawnChunk(coord, preload: true);
                spawned++;
            }
        }

        /// <summary>Completes finished gen jobs on main thread; applies safe spawn, delta, schedules mesh. Exceptions in Complete() or subsequent logic are not caught.</summary>
        void ProcessGenJobs()
        {
            if (_genJobs.Count == 0) return;
            _genCompleted.Clear();
            foreach (var kvp in _genJobs)
            {
                if (kvp.Value.Job.Handle.IsCompleted)
                    _genCompleted.Add(kvp.Key);
            }

            ChunkCoord center = default;
            int keepRadius = 0;
            bool hasCenter = player != null && worldGen != null;
            if (hasCenter)
            {
                center = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize);
                keepRadius = EffectiveUnloadRadius();
                if (enablePreload)
                    keepRadius = Mathf.Max(keepRadius, EffectivePreloadRadius());
            }

            foreach (var coord in _genCompleted)
            {
                if (!_genJobs.TryGetValue(coord, out var task)) continue;
                if (task.Epoch != _streamingEpoch && hasCenter && !IsWithinKeepRadius(coord, center, keepRadius))
                {
                    task.Job.Handle.Complete();
                    task.Job.Dispose();
                    _genJobs.Remove(coord);
                    if (_active.ContainsKey(coord))
                        QueueRemoval(coord);
                    continue;
                }
                task.Job.Handle.Complete();
                task.Job.Dispose();

                if (task.UseSlices && task.SliceIndex + 1 < task.SliceCount)
                {
                    if (_generator != null && worldGen != null && task.Chunk != null && task.Chunk.Data.IsCreated)
                    {
                        int nextIndex = task.SliceIndex + 1;
                        int startIndex = nextIndex * task.SliceSize;
                        int total = task.Chunk.Data.Materials.Length;
                        int count = Mathf.Min(task.SliceSize, total - startIndex);
                        if (count > 0)
                        {
                            var handle = _generator.Schedule(task.Chunk.Data, coord, worldGen, noiseStack, out var layers, startIndex, count);
                            task.Job = new ChunkGenJobHandle
                            {
                                Handle = handle,
                                Layers = layers
                            };
                            task.SliceIndex = nextIndex;
                            _genJobs[coord] = task;
                            continue;
                        }
                    }
                }

                _genJobs.Remove(coord);

                if (!_active.TryGetValue(coord, out var chunk) || chunk != task.Chunk)
                    continue;

                _lastGenMs = (long)((Time.realtimeSinceStartupAsDouble - task.StartTime) * 1000.0);

                bool appliedSafeSpawn = false;
                if (task.ApplySafeSpawn)
                    appliedSafeSpawn = ApplySafeSpawnToChunk(chunk, coord);

                if (hybridSave != null && task.ApplyDelta)
                {
                    hybridSave.ApplyDeltaIfAny(coord, chunk.Data);
                    if (modManager != null && modManager.GetDeltaCount(coord) > 0)
                        modManager.ApplyModsToChunk(coord, chunk.Data);
                }
                else if (hybridSave == null && modManager != null)
                {
                    modManager.ApplyModsToChunk(coord, chunk.Data);
                }

                if (appliedSafeSpawn && _pendingSafeSpawnSnap)
                {
                    SnapPlayerToSafeSpawn();
                    _pendingSafeSpawnSnap = false;
                }

                if (!ScheduleMeshForChunk(coord, task.SpawnStart, GetInitialLodStep(coord)))
                    QueueRemesh(coord);
            }
        }

        /// <summary>Completes finished mesh jobs on main thread; queues integration. Exceptions in Complete() or subsequent logic are not caught.</summary>
        void ProcessMeshJobs()
        {
            if (_meshJobs.Count == 0) return;
            _meshCompleted.Clear();
            foreach (var kvp in _meshJobs)
            {
                if (kvp.Value.Job.Handle.IsCompleted)
                    _meshCompleted.Add(kvp.Key);
            }

            ChunkCoord center = default;
            int keepRadius = 0;
            bool hasCenter = player != null && worldGen != null;
            if (hasCenter)
            {
                center = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize);
                keepRadius = EffectiveUnloadRadius();
                if (enablePreload)
                    keepRadius = Mathf.Max(keepRadius, EffectivePreloadRadius());
            }

            foreach (var coord in _meshCompleted)
            {
                if (!_meshJobs.TryGetValue(coord, out var task)) continue;
                if (task.Job.Epoch != _streamingEpoch && hasCenter && !IsWithinKeepRadius(coord, center, keepRadius))
                {
                    task.Job.Handle.Complete();
                    task.Job.Dispose();
                    _meshJobs.Remove(coord);
                    if (_active.ContainsKey(coord))
                        QueueRemoval(coord);
                    continue;
                }
                task.Job.Handle.Complete();
                _meshJobs.Remove(coord);

                if (!_active.TryGetValue(coord, out var chunk) || chunk != task.Chunk)
                {
                    task.Job.Dispose();
                    continue;
                }

                if (worldGen != null && worldGen.EnableSafeSpawn && worldGen.SafeSpawnRevalidate && _safeSpawnInitialized)
                {
                    if (ReapplySafeSpawnToChunk(chunk, coord, out var changed) && changed)
                    {
                        task.Job.Dispose();
                        RequestRemesh(coord, includeNeighbors: true);
                        continue;
                    }
                }

                lock (_integrationLock)
                {
                    if (!_integrationSet.Contains(coord))
                    {
                        _pendingMeshJobs[coord] = task.Job;
                        _integrationQueue.Enqueue(coord);
                        _integrationSet.Add(coord);
                    }
                    else
                    {
                        if (_pendingMeshJobs.TryGetValue(coord, out var oldJob))
                            oldJob.Dispose();
                        _pendingMeshJobs[coord] = task.Job;
                    }
                }

                _lastMeshMs = (long)((Time.realtimeSinceStartupAsDouble - task.StartTime) * 1000.0);
                _lastTotalMs = task.SpawnStart > 0
                    ? (long)((Time.realtimeSinceStartupAsDouble - task.SpawnStart) * 1000.0)
                    : _lastMeshMs;
                _lastSpawnCoord = coord;
            }
        }

        void ProcessIntegrationQueue()
        {
            int integrationsThisFrame = 0;
            ChunkCoord center = default;
            int keepRadius = 0;
            bool hasCenter = player != null && worldGen != null;
            if (hasCenter)
            {
                center = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize);
                keepRadius = EffectiveUnloadRadius();
                if (enablePreload)
                    keepRadius = Mathf.Max(keepRadius, EffectivePreloadRadius());
            }

            // Dynamic limit: if queue is very large, process more per frame to catch up
            int integrationLimit = CurrentMaxIntegrationsPerFrame;
            if (dynamicIntegrationLimit && _integrationQueue.Count > maxIntegrationQueueSize * 0.5f)
            {
                // Process more aggressively when queue is large
                integrationLimit = Mathf.Min(CurrentMaxIntegrationsPerFrame * 3, _integrationQueue.Count / 10);
            }

            // Clean up stale entries while processing (skip them instead of rebuilding queue)
            int processed = 0;
            int maxIterations = Mathf.Min(_integrationQueue.Count, integrationLimit * 3); // Prevent infinite loop

            while (_integrationQueue.Count > 0 && integrationsThisFrame < integrationLimit && processed < maxIterations)
            {
                if (streamingBudget != null && streamingBudget.IsExceeded())
                    break;

                processed++;
                ChunkCoord coord;
                lock (_integrationLock)
                {
                    if (_integrationQueue.Count == 0) break;
                    coord = _integrationQueue.Dequeue();
                    _integrationSet.Remove(coord);
                }

                // Skip stale entries (no longer active or out of range)
                if (!_active.TryGetValue(coord, out var chunk))
                {
                    // Чанк видалено: dispose job
                    if (_pendingMeshJobs.TryGetValue(coord, out var job))
                    {
                        job.Dispose();
                        _pendingMeshJobs.Remove(coord);
                    }
                    _pendingCachedMeshes.Remove(coord);
                    continue;
                }
                if (hasCenter && !IsWithinKeepRadius(coord, center, keepRadius))
                {
                    // Out of range: dispose job
                    if (_pendingMeshJobs.TryGetValue(coord, out var job))
                    {
                        job.Dispose();
                        _pendingMeshJobs.Remove(coord);
                    }
                    _pendingCachedMeshes.Remove(coord);
                    continue;
                }

                if (_pendingCachedMeshes.TryGetValue(coord, out var cachedMesh))
                {
                    if (cachedMesh.Epoch != _streamingEpoch)
                    {
                        _pendingCachedMeshes.Remove(coord);
                        QueueRemesh(coord);
                        continue;
                    }

                    // Validate cached mesh before applying
                    if (cachedMesh.Mesh == null || cachedMesh.Mesh.vertexCount == 0)
                    {
                        // Invalid mesh - queue remesh instead
                        _pendingCachedMeshes.Remove(coord);
                        QueueRemesh(coord);
                        continue;
                    }
                    
                    // Re-validate hash matches current chunk data
                    // If neighbors changed, hash might be stale
                    var currentNeighbors = GatherNeighborCopies(coord);
                    bool hashStillValid = false;
                    if (HasAllNeighbors(currentNeighbors.Data))
                    {
                        ulong currentHash = ComputeMeshCacheHash(chunk.Data.Materials, chunk.Data.Size, currentNeighbors, chunk.LodStep, chunk.Data.Density);
                        hashStillValid = (currentHash == cachedMesh.Hash);
                    }
                    currentNeighbors.Dispose();
                    
                    if (!hashStillValid)
                    {
                        // Hash mismatch - neighbors or materials changed, need remesh
                        _pendingCachedMeshes.Remove(coord);
                        QueueRemesh(coord);
                        continue;
                    }

                    bool cachedApplyCollider = addColliders && !_preloaded.Contains(coord);
                    double cachedIntegrationStart = Time.realtimeSinceStartupAsDouble;
                    chunk.ApplySharedMesh(cachedMesh.Mesh, cachedApplyCollider);
                    _lastIntegrationMs = (long)((Time.realtimeSinceStartupAsDouble - cachedIntegrationStart) * 1000.0);

                    RegisterMeshCacheForChunk(coord, cachedMesh.Hash, cachedMesh.Mesh, markShared: false, addCollider: cachedApplyCollider);
                    _pendingCachedMeshes.Remove(coord);
                    chunk.IsLowLod = false;
                    chunk.LodStartTime = 0;
                    chunk.LodStep = 1;
                    chunk.UsesSvo = false;

                    // Ensure renderer is enabled for non-preloaded chunks with valid mesh
                    if (!_preloaded.Contains(coord) && cachedMesh.Mesh != null && cachedMesh.Mesh.vertexCount > 0)
                    {
                        chunk.SetRendererEnabled(true);
                    }

                    if (_preloaded.Contains(coord))
                    {
                        chunk.SetRendererEnabled(false);
                        chunk.SetColliderEnabled(false);
                    }

                    if (_meshedOnce.Add(coord))
                        RebuildNeighbors(coord);

                    if (_waitingSafeSpawnMesh && coord.Equals(_safeSpawnAnchorCoord))
                    {
                        SnapPlayerToSafeSpawn();
                        SetPlayerFrozen(false);
                        _waitingSafeSpawnMesh = false;
                    }

                    if (_remeshAfterIntegration.Remove(coord))
                        QueueRemesh(coord);

                    integrationsThisFrame++;
                    continue;
                }

                if (!_pendingMeshJobs.TryGetValue(coord, out var meshJob))
                {
                    continue; // Job втрачено
                }
                if (meshJob.Epoch != _streamingEpoch)
                {
                    meshJob.Dispose();
                    _pendingMeshJobs.Remove(coord);
                    QueueRemesh(coord);
                    continue;
                }

                bool applyCollider = addColliders && !_preloaded.Contains(coord);
                double integrationStart = Time.realtimeSinceStartupAsDouble;
                chunk.ApplyMesh(meshJob.MeshData, applyCollider);
                _lastIntegrationMs = (long)((Time.realtimeSinceStartupAsDouble - integrationStart) * 1000.0);

                if (enableMeshCache && meshJob.LodStep <= 1 && meshJob.MaterialsHash != 0)
                {
                    Mesh renderMesh = chunk.GetRenderMesh();
                    RegisterMeshCacheForChunk(coord, meshJob.MaterialsHash, renderMesh, markShared: true, addCollider: applyCollider);
                }
                chunk.IsLowLod = meshJob.LodStep > 1;
                chunk.LodStartTime = chunk.IsLowLod ? Time.realtimeSinceStartupAsDouble : 0;
                chunk.LodStep = meshJob.LodStep;
                chunk.UsesSvo = false;
                
                // Force remesh if mesh appears empty or invalid (fixes holes)
                Mesh checkMesh = chunk.GetRenderMesh();
                if (checkMesh == null || checkMesh.vertexCount == 0)
                {
                    // Empty mesh - queue remesh immediately
                    QueueRemesh(coord);
                }
                else
                {
                    // Ensure renderer is enabled for non-preloaded chunks with valid mesh
                    if (!_preloaded.Contains(coord))
                    {
                        chunk.SetRendererEnabled(true);
                    }
                }

                if (_preloaded.Contains(coord))
                {
                    chunk.SetRendererEnabled(false);
                    chunk.SetColliderEnabled(false);
                }

                if (_meshedOnce.Add(coord))
                    RebuildNeighbors(coord);

                if (_waitingSafeSpawnMesh && coord.Equals(_safeSpawnAnchorCoord))
                {
                    SnapPlayerToSafeSpawn();
                    SetPlayerFrozen(false);
                    _waitingSafeSpawnMesh = false;
                }

                meshJob.Dispose();
                _pendingMeshJobs.Remove(coord);

                if (_remeshAfterIntegration.Remove(coord))
                    QueueRemesh(coord);

                integrationsThisFrame++;
            }

            _integrationsLastFrame = integrationsThisFrame;
        }

        void ProcessRemovalQueue()
        {
            if (player == null || worldGen == null) return;
            ChunkCoord center = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize);
            int keepRadius = EffectiveUnloadRadius();
            if (enablePreload)
                keepRadius = Mathf.Max(keepRadius, EffectivePreloadRadius());

            double removalStart = Time.realtimeSinceStartupAsDouble;
            int count = 0;
            int guard = _removeQueue.Count;
            while (_removeQueue.Count > 0 && count < maxRemovalsPerFrame && guard-- > 0)
            {
                if (BudgetExceeded()) break;
                if (removalBudgetMs > 0f && (Time.realtimeSinceStartupAsDouble - removalStart) * 1000.0 >= removalBudgetMs)
                    break;
                var coord = _removeQueue.Dequeue();

                if (!_active.ContainsKey(coord))
                {
                    _removeSet.Remove(coord);
                    continue;
                }
                if (IsWithinKeepRadius(coord, center, keepRadius))
                {
                    _removeSet.Remove(coord);
                    continue;
                }
                if (IsChunkBusy(coord))
                {
                    _removeQueue.Enqueue(coord);
                    continue;
                }

                RemoveChunk(coord);
                _removeSet.Remove(coord);
                count++;
            }
        }

        void QueueRemoval(ChunkCoord coord)
        {
            if (!_active.ContainsKey(coord)) return;
            if (_removeSet.Add(coord))
                _removeQueue.Enqueue(coord);
        }

        bool ShouldRebuildPending(ChunkCoord center)
        {
            if (!_hasPendingCenter)
            {
                _lastPendingCenter = center;
                _hasPendingCenter = true;
                return false;
            }

            if (pendingQueueCap > 0 && PendingCount > pendingQueueCap)
                return true;

            if (pendingResetDistance > 0)
            {
                int dx = Mathf.Abs(center.X - _lastPendingCenter.X);
                int dz = Mathf.Abs(center.Z - _lastPendingCenter.Z);
                if (dx > pendingResetDistance || dz > pendingResetDistance)
                    return true;
            }

            return false;
        }

        void RebuildPendingQueue(ChunkCoord center)
        {
            _pending.Clear();
            _pendingSet.Clear();
            if (viewCone != null && viewCone.Enabled)
                viewCone.Clear();
            _lastPendingCenter = center;
            _hasPendingCenter = true;

            for (int dz = -loadRadius; dz <= loadRadius; dz++)
            {
                for (int dx = -loadRadius; dx <= loadRadius; dx++)
                {
                    for (int dy = 0; dy < worldGen.ColumnChunks; dy++)
                    {
                        var coord = new ChunkCoord(center.X + dx, dy, center.Z + dz);
                        if (_active.ContainsKey(coord)) continue;
                        if (viewCone != null && viewCone.Enabled)
                            viewCone.EnqueueWithPriority(coord, center, player);
                        else
                            _pending.Enqueue(coord);
                        _pendingSet.Add(coord);
                    }
                }
            }
        }

        void DropOnePendingOldest()
        {
            if (PendingCount == 0) return;
            ChunkCoord dropped;
            if (viewCone != null && viewCone.Enabled)
            {
                if (!viewCone.TryRemoveLowestPriority(out dropped)) return;
            }
            else
            {
                dropped = _pending.Dequeue();
            }
            _pendingSet.Remove(dropped);
        }

        bool TryDequeuePending(ChunkCoord center, out ChunkCoord coord)
        {
            if (PendingCount == 0)
            {
                coord = default;
                return false;
            }
            if (viewCone != null && viewCone.Enabled)
            {
                if (!viewCone.TryDequeue(out coord))
                    return false;
                _pendingSet.Remove(coord);
                return true;
            }
            coord = _pending.Dequeue();
            _pendingSet.Remove(coord);
            return true;
        }

        bool IsWithinKeepRadius(ChunkCoord coord, ChunkCoord center, int keepRadius)
        {
            if (worldGen == null) return false;
            if (coord.Y < 0 || coord.Y >= worldGen.ColumnChunks) return false;
            int dx = Mathf.Abs(coord.X - center.X);
            int dz = Mathf.Abs(coord.Z - center.Z);
            return dx <= keepRadius && dz <= keepRadius;
        }

        bool IsWithinLoadRadius(ChunkCoord coord, ChunkCoord center, int radius)
        {
            if (worldGen == null) return false;
            if (coord.Y < 0 || coord.Y >= worldGen.ColumnChunks) return false;
            int dx = Mathf.Abs(coord.X - center.X);
            int dz = Mathf.Abs(coord.Z - center.Z);
            return dx <= radius && dz <= radius;
        }

        int GetInitialLodStep(ChunkCoord coord)
        {
            if (enableFullLod && lodSettings != null && player != null && worldGen != null)
            {
                ChunkCoord center = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize);
                int dx = Mathf.Abs(coord.X - center.X);
                int dz = Mathf.Abs(coord.Z - center.Z);
                int dist = Mathf.Max(dx, dz);
                var desired = lodSettings.ResolveLevel(dist, 1, ChunkLodMode.Mesh);
                return Mathf.Max(1, desired.LodStep);
            }

            if (!enableReverseLod) return 1;
            if (reverseLodStep <= 1) return 1;
            if (player == null || worldGen == null) return 1;

            ChunkCoord playerChunk = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize);
            int dxx = Mathf.Abs(coord.X - playerChunk.X);
            int dzz = Mathf.Abs(coord.Z - playerChunk.Z);
            int dist2 = Mathf.Max(dxx, dzz);
            if (dist2 <= reverseLodMinDistance) return 1;
            return reverseLodStep;
        }

        bool IsChunkBusy(ChunkCoord coord)
        {
            return _genJobs.ContainsKey(coord) || _meshJobs.ContainsKey(coord);
        }

        bool IsChunkGenerating(ChunkCoord coord)
        {
            return _genJobs.ContainsKey(coord);
        }

        void ScheduleGenJob(ChunkCoord coord, Chunk chunk, double spawnStart, bool applySafeSpawn, bool applyDelta)
        {
            if (_genJobs.ContainsKey(coord)) return;
            if (_generator == null || worldGen == null) return;
            if (chunk == null || !chunk.Data.IsCreated) return;
            bool useSlices = enableGenSlicing && genSliceCount > 1;
            int total = chunk.Data.Materials.Length;
            int slices = useSlices ? Mathf.Max(1, genSliceCount) : 1;
            int sliceSize = useSlices ? (total + slices - 1) / slices : total;
            int count = useSlices ? Mathf.Min(sliceSize, total) : total;

            var handle = _generator.Schedule(chunk.Data, coord, worldGen, noiseStack, out var layers, 0, count);
            var job = new ChunkGenJobHandle
            {
                Handle = handle,
                Layers = layers
            };

            _genJobs[coord] = new GenTask
            {
                Coord = coord,
                Chunk = chunk,
                Job = job,
                StartTime = Time.realtimeSinceStartupAsDouble,
                SpawnStart = spawnStart,
                ApplySafeSpawn = applySafeSpawn,
                ApplyDelta = applyDelta,
                Epoch = _streamingEpoch,
                UseSlices = useSlices,
                SliceIndex = 0,
                SliceCount = slices,
                SliceSize = sliceSize
            };
        }

        bool ScheduleMeshForChunk(ChunkCoord coord, double spawnStart, int lodStep = 1)
        {
            if (!_active.TryGetValue(coord, out var chunk)) return false;
            if (!chunk.Data.IsCreated) return false;
            if (IsChunkGenerating(coord)) return false;
            if (_meshJobs.ContainsKey(coord)) return false;
            if (_integrationSet.Contains(coord) || _pendingCachedMeshes.ContainsKey(coord))
            {
                _remeshAfterIntegration.Add(coord);
                return false;
            }
            if (_meshJobs.Count >= CurrentMaxMeshJobsInFlight) return false;

            lodStep = Mathf.Max(1, lodStep);
            int chunkSize = chunk.Data.Size;
            if (lodStep > 1 && (lodStep > chunkSize || (chunkSize % lodStep) != 0))
            {
                if (!_warnedLodStepMismatch)
                {
                    Debug.LogWarning($"[ChunkManager] Reverse LOD step {lodStep} is invalid for chunk size {chunkSize}. Falling back to full detail.");
                    _warnedLodStepMismatch = true;
                }
                lodStep = 1;
            }

            ulong materialsHash = 0;
            bool useCache = enableMeshCache && maxMeshCacheEntries > 0 && lodStep == 1;

            NeighborDataBuffers neighbors = default;
            var meshData = new MeshData(Unity.Collections.Allocator.Persistent);
            NativeArray<ushort> materialsCopy;
            ChunkData dataCopy;
            float voxelScale = VoxelConstants.VoxelSize;

            if (lodStep > 1)
            {
                int srcSize = chunk.Data.Size;
                int lodSize = Mathf.Max(1, srcSize / lodStep);
                materialsCopy = new NativeArray<ushort>(lodSize * lodSize * lodSize, Allocator.Persistent);
                DownsampleMaterials(chunk.Data.Materials, srcSize, lodStep, materialsCopy);
                dataCopy = new ChunkData { Materials = materialsCopy, Size = lodSize };
                voxelScale = VoxelConstants.VoxelSize * lodStep;
                neighbors = GatherNeighborCopiesLod(coord, lodStep, lodSize, srcSize);
            }
            else
            {
                neighbors = GatherNeighborCopies(coord);
                materialsCopy = new NativeArray<ushort>(chunk.Data.Materials.Length, Allocator.Persistent);
                NativeArray<ushort>.Copy(chunk.Data.Materials, materialsCopy);
                dataCopy = new ChunkData { Materials = materialsCopy, Size = chunk.Data.Size };
            }

            if (useCache)
            {
                if (!HasAllNeighbors(neighbors.Data))
                {
                    useCache = false;
                }
                else
                {
                    materialsHash = ComputeMeshCacheHash(chunk.Data.Materials, chunk.Data.Size, neighbors, lodStep, chunk.Data.Density);
                    
                    // Before using cached mesh, verify neighbors haven't changed
                    // This prevents using stale meshes when neighbors were updated
                    if (_meshCache.TryGetValue(materialsHash, out var cachedEntry) && cachedEntry.Mesh != null)
                    {
                        // Re-validate neighbors are still the same
                        // If any neighbor is generating or missing, don't use cache
                        var negXCoord = new ChunkCoord(coord.X - 1, coord.Y, coord.Z);
                        var posXCoord = new ChunkCoord(coord.X + 1, coord.Y, coord.Z);
                        var negYCoord = new ChunkCoord(coord.X, coord.Y - 1, coord.Z);
                        var posYCoord = new ChunkCoord(coord.X, coord.Y + 1, coord.Z);
                        var negZCoord = new ChunkCoord(coord.X, coord.Y, coord.Z - 1);
                        var posZCoord = new ChunkCoord(coord.X, coord.Y, coord.Z + 1);
                        
                        bool neighborsValid = true;
                        neighborsValid &= !IsChunkGenerating(negXCoord) && _active.ContainsKey(negXCoord);
                        neighborsValid &= !IsChunkGenerating(posXCoord) && _active.ContainsKey(posXCoord);
                        neighborsValid &= !IsChunkGenerating(negYCoord) && _active.ContainsKey(negYCoord);
                        neighborsValid &= !IsChunkGenerating(posYCoord) && _active.ContainsKey(posYCoord);
                        neighborsValid &= !IsChunkGenerating(negZCoord) && _active.ContainsKey(negZCoord);
                        neighborsValid &= !IsChunkGenerating(posZCoord) && _active.ContainsKey(posZCoord);
                        
                        if (!neighborsValid)
                        {
                            useCache = false;
                        }
                    }
                }
                
                if (useCache && _meshCache.TryGetValue(materialsHash, out var cachedMesh) && cachedMesh.Mesh != null)
                {
                    // Final validation: mesh must have vertices
                    if (cachedMesh.Mesh.vertexCount == 0)
                    {
                        useCache = false;
                    }
                    else
                    {
                        cachedMesh.LastUsedFrame = Time.frameCount;
                        _meshCache[materialsHash] = cachedMesh;
                        if (TryQueueCachedMesh(coord, materialsHash, cachedMesh.Mesh))
                        {
                            neighbors.Dispose();
                            materialsCopy.Dispose();
                            meshData.Dispose();
                            return true;
                        }
                    }
                }
            }

            var mask = new NativeArray<GreedyMesher.MaskCell>(dataCopy.Size * dataCopy.Size, Allocator.Persistent);
            if (!_emptyMaterials.IsCreated)
                _emptyMaterials = new NativeArray<ushort>(0, Allocator.Persistent);
            var empty = _emptyMaterials;

            GetMeshMaterialSettings(chunk, out var maxMaterialIndex, out var fallbackMaterialIndex);
            var handle = GreedyMesher.Schedule(dataCopy, neighbors.Data, maxMaterialIndex, fallbackMaterialIndex, mask, empty, ref meshData, voxelScale);

            var meshJob = new ChunkMeshJobHandle
            {
                Handle = handle,
                MeshData = meshData,
                MaterialsCopy = materialsCopy,
                Mask = mask,
                Empty = empty,
                Neighbors = neighbors,
                Epoch = _streamingEpoch,
                MaterialsHash = materialsHash,
                LodStep = lodStep,
                OwnsEmpty = false
            };

            _meshJobs[coord] = new MeshTask
            {
                Coord = coord,
                Chunk = chunk,
                Job = meshJob,
                StartTime = Time.realtimeSinceStartupAsDouble,
                SpawnStart = spawnStart
            };

            return true;
        }

        NeighborDataBuffers GatherNeighborCopies(ChunkCoord coord)
        {
            var buffers = new NeighborDataBuffers();
            var data = new GreedyMesher.NeighborData();

            var negXCoord = new ChunkCoord(coord.X - 1, coord.Y, coord.Z);
            if (_active.TryGetValue(negXCoord, out var negX) && negX.Data.IsCreated && !IsChunkGenerating(negXCoord))
            {
                data.HasNegX = true;
                buffers.NegX = new NativeArray<ushort>(negX.Data.Materials.Length, Allocator.Persistent);
                NativeArray<ushort>.Copy(negX.Data.Materials, buffers.NegX);
                data.NegX = buffers.NegX;
            }
            var posXCoord = new ChunkCoord(coord.X + 1, coord.Y, coord.Z);
            if (_active.TryGetValue(posXCoord, out var posX) && posX.Data.IsCreated && !IsChunkGenerating(posXCoord))
            {
                data.HasPosX = true;
                buffers.PosX = new NativeArray<ushort>(posX.Data.Materials.Length, Allocator.Persistent);
                NativeArray<ushort>.Copy(posX.Data.Materials, buffers.PosX);
                data.PosX = buffers.PosX;
            }
            var negYCoord = new ChunkCoord(coord.X, coord.Y - 1, coord.Z);
            if (_active.TryGetValue(negYCoord, out var negY) && negY.Data.IsCreated && !IsChunkGenerating(negYCoord))
            {
                data.HasNegY = true;
                buffers.NegY = new NativeArray<ushort>(negY.Data.Materials.Length, Allocator.Persistent);
                NativeArray<ushort>.Copy(negY.Data.Materials, buffers.NegY);
                data.NegY = buffers.NegY;
            }
            var posYCoord = new ChunkCoord(coord.X, coord.Y + 1, coord.Z);
            if (_active.TryGetValue(posYCoord, out var posY) && posY.Data.IsCreated && !IsChunkGenerating(posYCoord))
            {
                data.HasPosY = true;
                buffers.PosY = new NativeArray<ushort>(posY.Data.Materials.Length, Allocator.Persistent);
                NativeArray<ushort>.Copy(posY.Data.Materials, buffers.PosY);
                data.PosY = buffers.PosY;
            }
            var negZCoord = new ChunkCoord(coord.X, coord.Y, coord.Z - 1);
            if (_active.TryGetValue(negZCoord, out var negZ) && negZ.Data.IsCreated && !IsChunkGenerating(negZCoord))
            {
                data.HasNegZ = true;
                buffers.NegZ = new NativeArray<ushort>(negZ.Data.Materials.Length, Allocator.Persistent);
                NativeArray<ushort>.Copy(negZ.Data.Materials, buffers.NegZ);
                data.NegZ = buffers.NegZ;
            }
            var posZCoord = new ChunkCoord(coord.X, coord.Y, coord.Z + 1);
            if (_active.TryGetValue(posZCoord, out var posZ) && posZ.Data.IsCreated && !IsChunkGenerating(posZCoord))
            {
                data.HasPosZ = true;
                buffers.PosZ = new NativeArray<ushort>(posZ.Data.Materials.Length, Allocator.Persistent);
                NativeArray<ushort>.Copy(posZ.Data.Materials, buffers.PosZ);
                data.PosZ = buffers.PosZ;
            }

            buffers.Data = data;
            return buffers;
        }

        NeighborDataBuffers GatherNeighborCopiesLod(ChunkCoord coord, int lodStep, int lodSize, int srcSize)
        {
            var buffers = new NeighborDataBuffers();
            var data = new GreedyMesher.NeighborData();
            int lodCount = lodSize * lodSize * lodSize;

            var negXCoord = new ChunkCoord(coord.X - 1, coord.Y, coord.Z);
            if (_active.TryGetValue(negXCoord, out var negX) && negX.Data.IsCreated && !IsChunkGenerating(negXCoord))
            {
                data.HasNegX = true;
                buffers.NegX = new NativeArray<ushort>(lodCount, Allocator.Persistent);
                DownsampleMaterials(negX.Data.Materials, srcSize, lodStep, buffers.NegX);
                data.NegX = buffers.NegX;
            }
            var posXCoord = new ChunkCoord(coord.X + 1, coord.Y, coord.Z);
            if (_active.TryGetValue(posXCoord, out var posX) && posX.Data.IsCreated && !IsChunkGenerating(posXCoord))
            {
                data.HasPosX = true;
                buffers.PosX = new NativeArray<ushort>(lodCount, Allocator.Persistent);
                DownsampleMaterials(posX.Data.Materials, srcSize, lodStep, buffers.PosX);
                data.PosX = buffers.PosX;
            }
            var negYCoord = new ChunkCoord(coord.X, coord.Y - 1, coord.Z);
            if (_active.TryGetValue(negYCoord, out var negY) && negY.Data.IsCreated && !IsChunkGenerating(negYCoord))
            {
                data.HasNegY = true;
                buffers.NegY = new NativeArray<ushort>(lodCount, Allocator.Persistent);
                DownsampleMaterials(negY.Data.Materials, srcSize, lodStep, buffers.NegY);
                data.NegY = buffers.NegY;
            }
            var posYCoord = new ChunkCoord(coord.X, coord.Y + 1, coord.Z);
            if (_active.TryGetValue(posYCoord, out var posY) && posY.Data.IsCreated && !IsChunkGenerating(posYCoord))
            {
                data.HasPosY = true;
                buffers.PosY = new NativeArray<ushort>(lodCount, Allocator.Persistent);
                DownsampleMaterials(posY.Data.Materials, srcSize, lodStep, buffers.PosY);
                data.PosY = buffers.PosY;
            }
            var negZCoord = new ChunkCoord(coord.X, coord.Y, coord.Z - 1);
            if (_active.TryGetValue(negZCoord, out var negZ) && negZ.Data.IsCreated && !IsChunkGenerating(negZCoord))
            {
                data.HasNegZ = true;
                buffers.NegZ = new NativeArray<ushort>(lodCount, Allocator.Persistent);
                DownsampleMaterials(negZ.Data.Materials, srcSize, lodStep, buffers.NegZ);
                data.NegZ = buffers.NegZ;
            }
            var posZCoord = new ChunkCoord(coord.X, coord.Y, coord.Z + 1);
            if (_active.TryGetValue(posZCoord, out var posZ) && posZ.Data.IsCreated && !IsChunkGenerating(posZCoord))
            {
                data.HasPosZ = true;
                buffers.PosZ = new NativeArray<ushort>(lodCount, Allocator.Persistent);
                DownsampleMaterials(posZ.Data.Materials, srcSize, lodStep, buffers.PosZ);
                data.PosZ = buffers.PosZ;
            }

            buffers.Data = data;
            return buffers;
        }

        void DownsampleMaterials(NativeArray<ushort> source, int srcSize, int lodStep, NativeArray<ushort> dest)
        {
            if (!source.IsCreated || source.Length == 0 || lodStep <= 1)
            {
                if (source.IsCreated && dest.Length == source.Length)
                    NativeArray<ushort>.Copy(source, dest);
                return;
            }

            int lodSize = srcSize / lodStep;
            for (int z = 0; z < lodSize; z++)
            {
                int sz = z * lodStep;
                for (int y = 0; y < lodSize; y++)
                {
                    int sy = y * lodStep;
                    for (int x = 0; x < lodSize; x++)
                    {
                        int sx = x * lodStep;
                        int dstIndex = x + lodSize * (y + lodSize * z);
                        ushort material = 0;
                        int maxX = Mathf.Min(srcSize, sx + lodStep);
                        int maxY = Mathf.Min(srcSize, sy + lodStep);
                        int maxZ = Mathf.Min(srcSize, sz + lodStep);
                        bool found = false;
                        for (int zz = sz; zz < maxZ && !found; zz++)
                        {
                            for (int yy = sy; yy < maxY && !found; yy++)
                            {
                                int baseIndex = srcSize * (yy + srcSize * zz);
                                for (int xx = sx; xx < maxX; xx++)
                                {
                                    ushort m = source[baseIndex + xx];
                                    if (m != 0)
                                    {
                                        material = m;
                                        found = true;
                                        break;
                                    }
                                }
                            }
                        }
                        dest[dstIndex] = material;
                    }
                }
            }
        }

        void GetMeshMaterialSettings(Chunk chunk, out byte maxMaterialIndex, out byte fallbackMaterialIndex)
        {
            maxMaterialIndex = 255;
            fallbackMaterialIndex = 1;
            if (worldGen != null)
            {
                int defaultIndex = worldGen.DefaultMaterialIndex <= 0 ? 1 : Mathf.Clamp(worldGen.DefaultMaterialIndex, 1, 255);
                fallbackMaterialIndex = (byte)defaultIndex;
            }

            var binder = chunk != null ? chunk.GetComponent<VoxelMaterialBinder>() : null;
            if (binder != null && binder.Library != null)
            {
                if (binder.Library.TextureArray != null)
                {
                    int maxLayerIndex = Mathf.Clamp(binder.Library.TextureArray.depth - 1, 0, 255);
                    maxMaterialIndex = (byte)maxLayerIndex;
                }

                int fallbackIndex = Mathf.Clamp(binder.Library.DefaultLayerIndex, 0, maxMaterialIndex);
                fallbackMaterialIndex = (byte)fallbackIndex;
            }
        }

        void ProcessRemeshQueue()
        {
            int count = 0;
            int guard = _remeshQueue.Count;
            while (_remeshQueue.Count > 0 && count < maxRemeshPerFrame && guard-- > 0)
            {
                if (BudgetExceeded()) break;
                if (_meshJobs.Count >= CurrentMaxMeshJobsInFlight) break;

                var coord = _remeshQueue.Dequeue();
                _remeshSet.Remove(coord);
                
                if (!_active.ContainsKey(coord))
                {
                    continue;
                }
                if (_meshJobs.ContainsKey(coord))
                {
                    // Already meshing, will be handled when mesh completes
                    continue;
                }
                if (IsChunkGenerating(coord))
                {
                    // Still generating, will remesh after generation completes
                    // Re-enqueue to retry later
                    if (_remeshSet.Add(coord))
                        _remeshQueue.Enqueue(coord);
                    continue;
                }
                if (_integrationSet.Contains(coord) || _pendingCachedMeshes.ContainsKey(coord))
                {
                    // Already integrating, defer remesh until after integration
                    _remeshAfterIntegration.Add(coord);
                    continue;
                }

                if (ScheduleMeshForChunk(coord, 0, 1))
                {
                    count++;
                }
                else
                {
                    // ScheduleMeshForChunk failed - likely already in integration or cache pending
                    // It will be handled via _remeshAfterIntegration or retried later
                    // Don't re-enqueue to prevent infinite loops
                }
            }
        }

        void ProcessFullLod()
        {
            if (!enableFullLod) return;
            if (lodSettings == null) return;
            if (player == null || worldGen == null) return;

            ChunkCoord center = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize);
            int transitions = 0;
            int svoBuilds = 0;
            double now = Time.realtimeSinceStartupAsDouble;

            foreach (var kvp in _active)
            {
                if (transitions >= maxLodTransitionsPerFrame) break;
                if (BudgetExceeded()) break;
                if (_meshJobs.Count >= CurrentMaxMeshJobsInFlight) break;

                var coord = kvp.Key;
                var chunk = kvp.Value;
                if (chunk == null) continue;
                if (_preloaded.Contains(coord)) continue;

                int dist = Mathf.Max(Mathf.Abs(coord.X - center.X), Mathf.Abs(coord.Z - center.Z));
                ChunkLodMode currentMode = chunk.UsesSvo ? ChunkLodMode.Svo : ChunkLodMode.Mesh;
                int currentStep = Mathf.Max(1, chunk.LodStep);

                var desired = lodSettings.ResolveLevel(dist, currentStep, currentMode);
                if (desired.Mode == currentMode && desired.LodStep == currentStep) continue;
                if (lodTransitionCooldown > 0f && now - chunk.LodStartTime < lodTransitionCooldown) continue;
                if (IsChunkBusy(coord) || _integrationSet.Contains(coord) || _pendingCachedMeshes.ContainsKey(coord)) continue;

                if (desired.Mode == ChunkLodMode.Svo)
                {
                    if (svoManager == null) continue;
                    if (svoBuilds >= maxSvoBuildsPerFrame) continue;
                    GetMeshMaterialSettings(chunk, out var maxMaterialIndex, out var fallbackMaterialIndex);
                    if (svoManager.TryGetOrBuildMesh(coord, chunk.Data, desired.LodStep, maxMaterialIndex, fallbackMaterialIndex, out var svoMesh))
                    {
                        chunk.ApplySharedMesh(svoMesh, addCollider: false);
                        chunk.UsesSvo = true;
                        chunk.LodStep = desired.LodStep;
                        chunk.IsLowLod = true;
                        chunk.LodStartTime = now;
                        transitions++;
                        svoBuilds++;
                    }
                    continue;
                }

                if (ScheduleMeshForChunk(coord, 0, desired.LodStep))
                {
                    chunk.UsesSvo = false;
                    chunk.LodStep = desired.LodStep;
                    chunk.IsLowLod = desired.LodStep > 1;
                    chunk.LodStartTime = now;
                    transitions++;
                }
            }
        }

        void ProcessLodUpgrades()
        {
            if (!enableReverseLod) return;
            if (reverseLodStep <= 1) return;
            if (reverseLodUpgradeSeconds <= 0f) return;
            if (maxLodUpgradesPerFrame <= 0) return;
            if (player == null || worldGen == null) return;

            ChunkCoord center = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize);
            int upgrades = 0;
            foreach (var kvp in _active)
            {
                if (upgrades >= maxLodUpgradesPerFrame) break;
                if (BudgetExceeded()) break;
                if (_meshJobs.Count >= CurrentMaxMeshJobsInFlight) break;

                var coord = kvp.Key;
                var chunk = kvp.Value;
                if (chunk == null) continue;
                
                // Check if chunk has low-LOD mesh (either flag or by checking mesh vertex count vs expected)
                bool isLowLod = chunk.IsLowLod;
                if (!isLowLod)
                {
                    Mesh mesh = chunk.GetRenderMesh();
                    if (mesh != null && mesh.vertexCount > 0)
                    {
                        // Heuristic: low-LOD meshes have significantly fewer vertices
                        // Also check if mesh looks "blocky" (low detail) - this catches artifacts under terrain
                        int expectedFullVertices = chunk.Data.Size * chunk.Data.Size * 6 * 4; // rough estimate
                        if (mesh.vertexCount < expectedFullVertices * 0.3f)
                            isLowLod = true;
                    }
                    else if (mesh == null || mesh.vertexCount == 0)
                    {
                        // Empty mesh - force remesh
                        QueueRemesh(coord);
                        continue;
                    }
                }
                
                if (!isLowLod) continue;
                if (chunk.IsLowLod && reverseLodUpgradeSeconds > 0f && (Time.realtimeSinceStartupAsDouble - chunk.LodStartTime) < reverseLodUpgradeSeconds) continue;
                if (!IsWithinLoadRadius(coord, center, loadRadius)) continue;
                if (IsChunkBusy(coord)) continue;
                if (_integrationSet.Contains(coord) || _pendingCachedMeshes.ContainsKey(coord)) continue;

                chunk.IsLowLod = false;
                chunk.LodStartTime = 0;
                if (ScheduleMeshForChunk(coord, 0, 1))
                    upgrades++;
            }
        }

        void SpawnChunk(ChunkCoord coord, bool preload = false)
        {
            EnsurePrefab();
            if (_pool == null) _pool = new ChunkPool(chunkPrefab, transform);
            if (_generator == null) _generator = new ChunkGenerator();

            var chunk = _pool.Get();
            chunk.Initialize(coord);
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
                // Remove from cache after loading
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
                // Mods are already applied in cached data or snapshot, but we need to check for new mods
                if (!loadedFromCache && hybridSave == null && modManager != null)
                    modManager.ApplyModsToChunk(coord, chunk.Data);

                // Always mesh saved chunks with full detail (lodStep=1) to avoid "cubes that don't transform back"
                if (!ScheduleMeshForChunk(coord, spawnStart, 1))
                    QueueRemesh(coord);
            }
            else
            {
                ScheduleGenJob(coord, chunk, spawnStart, applySafeSpawn, applyDelta);
            }

        }

        /// <summary>Initializes safe spawn region and optionally freezes player until anchor chunk is meshed. Assumes chunks/mesh will be generated; timeout unfreezes if not ready.</summary>
        void TryInitSafeSpawn()
        {
            if (worldGen == null || !worldGen.EnableSafeSpawn || player == null) return;

            int chunkSize = worldGen.ChunkSize;
            float voxelSize = VoxelConstants.VoxelSize;
            float sizeChunks = Mathf.Max(0.1f, worldGen.SafeSpawnSizeChunks);
            _safeSpawnSizeVoxels = Mathf.Max(1, Mathf.RoundToInt(sizeChunks * chunkSize));

            double scale = chunkSize * voxelSize;
            int baseChunkX = VoxelMath.FloorToIntClamped(player.position.x / scale);
            int baseChunkZ = VoxelMath.FloorToIntClamped(player.position.z / scale);

            _safeSpawnWorldX0 = baseChunkX * chunkSize;
            _safeSpawnWorldZ0 = baseChunkZ * chunkSize;

            int maxH = 0;
            for (int x = 0; x < _safeSpawnSizeVoxels; x++)
            {
                for (int z = 0; z < _safeSpawnSizeVoxels; z++)
                {
                    float h = ChunkGenerator.SampleHeightAt(_safeSpawnWorldX0 + x, _safeSpawnWorldZ0 + z, worldGen, noiseStack);
                    int hi = Mathf.FloorToInt(h);
                    if (hi > maxH) maxH = hi;
                }
            }

            _safeSpawnBaseY = maxH + 1;
            _safeSpawnTopY = _safeSpawnBaseY + Mathf.Max(1, worldGen.SafeSpawnThickness) - 1;
            _safeSpawnInitialized = true;

            _pendingSafeSpawnSnap = worldGen.SnapPlayerToSafeSpawn;
            if (_pendingSafeSpawnSnap)
            {
                int centerX = _safeSpawnWorldX0 + _safeSpawnSizeVoxels / 2;
                int centerZ = _safeSpawnWorldZ0 + _safeSpawnSizeVoxels / 2;
                int anchorY = _safeSpawnBaseY;
                _safeSpawnAnchorCoord = new ChunkCoord(
                    Mathf.FloorToInt((float)centerX / chunkSize),
                    Mathf.FloorToInt((float)anchorY / chunkSize),
                    Mathf.FloorToInt((float)centerZ / chunkSize));
                _waitingSafeSpawnMesh = true;
                _safeSpawnWaitStart = Time.realtimeSinceStartupAsDouble;
                SetPlayerFrozen(true);
            }
        }

        bool ApplySafeSpawnToChunk(Chunk chunk, ChunkCoord coord)
        {
            int chunkSize = worldGen.ChunkSize;
            int worldX0 = coord.X * chunkSize;
            int worldZ0 = coord.Z * chunkSize;
            int worldX1 = worldX0 + chunkSize - 1;
            int worldZ1 = worldZ0 + chunkSize - 1;

            int spawnX1 = _safeSpawnWorldX0 + _safeSpawnSizeVoxels - 1;
            int spawnZ1 = _safeSpawnWorldZ0 + _safeSpawnSizeVoxels - 1;

            if (worldX1 < _safeSpawnWorldX0 || worldX0 > spawnX1) return false;
            if (worldZ1 < _safeSpawnWorldZ0 || worldZ0 > spawnZ1) return false;

            int worldY0 = coord.Y * chunkSize;
            int worldY1 = worldY0 + chunkSize - 1;
            if (worldY1 < _safeSpawnBaseY || worldY0 > _safeSpawnTopY) return false;

            int matIndex = worldGen.SafeSpawnMaterialIndex <= 0
                ? 200
                : Mathf.Clamp(worldGen.SafeSpawnMaterialIndex, 1, ushort.MaxValue);
            ushort mat = (ushort)matIndex;

            int startX = Mathf.Max(worldX0, _safeSpawnWorldX0);
            int endX = Mathf.Min(worldX1, spawnX1);
            int startZ = Mathf.Max(worldZ0, _safeSpawnWorldZ0);
            int endZ = Mathf.Min(worldZ1, spawnZ1);
            int startY = Mathf.Max(worldY0, _safeSpawnBaseY);
            int endY = Mathf.Min(worldY1, _safeSpawnTopY);

            for (int wx = startX; wx <= endX; wx++)
            {
                int lx = wx - worldX0;
                for (int wz = startZ; wz <= endZ; wz++)
                {
                    int lz = wz - worldZ0;
                    for (int wy = startY; wy <= endY; wy++)
                    {
                        int ly = wy - worldY0;
                        int idx = chunk.Data.Index(lx, ly, lz);
                        chunk.Data.Materials[idx] = mat;
                    }
                }
            }
            return true;
        }

        bool ReapplySafeSpawnToChunk(Chunk chunk, ChunkCoord coord, out bool changed)
        {
            changed = false;
            if (worldGen == null || !worldGen.EnableSafeSpawn) return false;

            int chunkSize = worldGen.ChunkSize;
            int worldX0 = coord.X * chunkSize;
            int worldZ0 = coord.Z * chunkSize;
            int worldX1 = worldX0 + chunkSize - 1;
            int worldZ1 = worldZ0 + chunkSize - 1;

            int spawnX1 = _safeSpawnWorldX0 + _safeSpawnSizeVoxels - 1;
            int spawnZ1 = _safeSpawnWorldZ0 + _safeSpawnSizeVoxels - 1;

            if (worldX1 < _safeSpawnWorldX0 || worldX0 > spawnX1) return false;
            if (worldZ1 < _safeSpawnWorldZ0 || worldZ0 > spawnZ1) return false;

            int worldY0 = coord.Y * chunkSize;
            int worldY1 = worldY0 + chunkSize - 1;
            if (worldY1 < _safeSpawnBaseY || worldY0 > _safeSpawnTopY) return false;

            int matIndex = worldGen.SafeSpawnMaterialIndex <= 0
                ? 200
                : Mathf.Clamp(worldGen.SafeSpawnMaterialIndex, 1, ushort.MaxValue);
            ushort mat = (ushort)matIndex;

            int startX = Mathf.Max(worldX0, _safeSpawnWorldX0);
            int endX = Mathf.Min(worldX1, spawnX1);
            int startZ = Mathf.Max(worldZ0, _safeSpawnWorldZ0);
            int endZ = Mathf.Min(worldZ1, spawnZ1);
            int startY = Mathf.Max(worldY0, _safeSpawnBaseY);
            int endY = Mathf.Min(worldY1, _safeSpawnTopY);

            for (int wx = startX; wx <= endX; wx++)
            {
                int lx = wx - worldX0;
                for (int wz = startZ; wz <= endZ; wz++)
                {
                    int lz = wz - worldZ0;
                    for (int wy = startY; wy <= endY; wy++)
                    {
                        int ly = wy - worldY0;
                        int idx = chunk.Data.Index(lx, ly, lz);
                        if (chunk.Data.Materials[idx] != mat)
                        {
                            chunk.Data.Materials[idx] = mat;
                            changed = true;
                        }
                    }
                }
            }

            return true;
        }

        void SnapPlayerToSafeSpawn()
        {
            if (player == null) return;
            float voxelSize = VoxelConstants.VoxelSize;
            float cx = (_safeSpawnWorldX0 + _safeSpawnSizeVoxels * 0.5f) * voxelSize;
            float cz = (_safeSpawnWorldZ0 + _safeSpawnSizeVoxels * 0.5f) * voxelSize;
            float surfaceY = (_safeSpawnTopY + 1) * voxelSize;

            float y = surfaceY + 0.1f;
            var cc = player.GetComponent<CharacterController>();
            if (cc == null)
                cc = player.GetComponentInChildren<CharacterController>();

            if (cc != null)
            {
                float bottomOffset = (cc.height * 0.5f) - cc.center.y;
                y = surfaceY + bottomOffset + 0.05f;
            }

            player.position = new Vector3(cx, y, cz);
        }

        void RemoveChunk(ChunkCoord coord)
        {
            if (!_active.TryGetValue(coord, out var chunk)) return;
            _active.Remove(coord);
            _meshedOnce.Remove(coord);
            _preloaded.Remove(coord);
            _preloadSet.Remove(coord);
            if (hybridSave != null)
            {
                hybridSave.HandleChunkUnloaded(coord, chunk.Data);
            }
            else
            {
                if (saveManager != null && saveManager.SaveOnUnload)
                    saveManager.EnqueueSave(coord, chunk.Data);
                if (modManager != null)
                    modManager.HandleChunkUnloaded(coord);
            }

            // Cache chunk data in RAM before disposing
            if (enableDataCache && chunk.Data.IsCreated)
            {
                CacheChunkData(coord, chunk.Data);
            }

            if (chunk.Data.IsCreated) chunk.Data.Dispose();
            ReleaseMeshCacheForChunk(coord);
            if (svoManager != null)
                svoManager.ReleaseForChunk(coord);
            if (_pendingCachedMeshes.ContainsKey(coord))
                _pendingCachedMeshes.Remove(coord);

            // Очистити pending mesh job якщо чанк в черзі інтеграції
            if (_integrationSet.Contains(coord))
            {
                _integrationSet.Remove(coord);
            }
            if (_pendingMeshJobs.TryGetValue(coord, out var meshJob))
            {
                meshJob.Dispose();
                _pendingMeshJobs.Remove(coord);
            }

            _pool.Return(chunk);
            RebuildNeighbors(coord);
        }

        void CacheChunkData(ChunkCoord coord, ChunkData data)
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

            while (_dataCache.Count >= cacheCap && _dataCache.Count > 0)
            {
                var first = default(ChunkCoord);
                foreach (var key in _dataCache.Keys)
                {
                    first = key;
                    break;
                }
                if (_dataCache.TryGetValue(first, out var oldCached))
                {
                    oldCached.Dispose();
                    _dataCache.Remove(first);
                }
            }

            // Cache the data
            var cached = new CachedChunkData();
            cached.CopyFrom(data);
            _dataCache[coord] = cached;
            _cacheOpsThisFrame++;
        }

        ulong ComputeMeshCacheHash(NativeArray<ushort> materials, int size, NeighborDataBuffers neighbors, int lodStep = 1, NativeArray<float> density = default)
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
                    hash ^= (ulong)(density[i] * 0xFFFFFF);
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

        bool HasAllNeighbors(GreedyMesher.NeighborData data)
        {
            return data.HasNegX && data.HasPosX
                && data.HasNegY && data.HasPosY
                && data.HasNegZ && data.HasPosZ;
        }

        bool TryQueueCachedMesh(ChunkCoord coord, ulong hash, Mesh mesh)
        {
            if (!_integrationSet.Contains(coord))
            {
                // Validate mesh before queuing - must have vertices
                if (mesh == null || mesh.vertexCount == 0)
                {
                    return false;
                }
                
                // Validate that chunk still exists and has data
                if (!_active.TryGetValue(coord, out var chunk) || !chunk.Data.IsCreated)
                {
                    return false;
                }
                
                _pendingCachedMeshes[coord] = new PendingCachedMesh
                {
                    Mesh = mesh,
                    Hash = hash,
                    Epoch = _streamingEpoch
                };
                lock (_integrationLock)
                {
                    _integrationQueue.Enqueue(coord);
                    _integrationSet.Add(coord);
                }
                return true;
            }
            return false;
        }

        void RegisterMeshCacheForChunk(ChunkCoord coord, ulong hash, Mesh mesh, bool markShared, bool addCollider)
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
                    _meshCache[hash] = new CachedMeshEntry
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

        void ReleaseMeshCacheForChunk(ChunkCoord coord)
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

        void EvictMeshCacheIfNeeded()
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

            while (_meshCache.Count > maxMeshCacheEntries && evictBudget-- > 0)
            {
                bool found = false;
                ulong bestKey = 0;
                int bestFrame = int.MaxValue;
                int bestVertexCount = -1;

                foreach (var kvp in _meshCache)
                {
                    if (kvp.Value.RefCount > 0) continue;
                    int vertexCount = kvp.Value.Mesh != null ? kvp.Value.Mesh.vertexCount : 0;
                    // Prefer evicting largest meshes (size-based) to free more memory; then LRU
                    bool better = !found ||
                        vertexCount > bestVertexCount ||
                        (vertexCount == bestVertexCount && kvp.Value.LastUsedFrame < bestFrame);
                    if (better)
                    {
                        found = true;
                        bestKey = kvp.Key;
                        bestFrame = kvp.Value.LastUsedFrame;
                        bestVertexCount = vertexCount;
                    }
                }

                if (!found) break;
                RemoveMeshCacheEntry(bestKey);
            }
        }

        void RemoveMeshCacheEntry(ulong hash)
        {
            if (_meshCache.TryGetValue(hash, out var entry))
            {
                if (entry.Mesh != null)
                    Destroy(entry.Mesh);
                _meshCache.Remove(hash);
            }
        }

        bool TryLoadFromCache(ChunkCoord coord, ChunkData data)
        {
            if (!enableDataCache) return false;
            if (!_dataCache.TryGetValue(coord, out var cached)) return false;
            if (!cached.IsValid) return false;
            // Invalidate cache if chunk was modified (mod/save) after being cached
            if (modManager != null && modManager.GetDeltaCount(coord) > 0)
            {
                cached.Dispose();
                _dataCache.Remove(coord);
                return false;
            }

            cached.CopyTo(data);
            return true;
        }

        public bool TryGetChunk(ChunkCoord coord, out Chunk chunk)
        {
            if (!_active.TryGetValue(coord, out chunk)) return false;
            if (chunk == null) return false;
            if (_genJobs.ContainsKey(coord))
            {
                chunk = null;
                return false;
            }
            return true;
        }

        public void RequestRemesh(ChunkCoord coord, bool includeNeighbors)
        {
            QueueRemesh(coord);
            if (!includeNeighbors || _requestRemeshDepth >= maxRequestRemeshNeighborsDepth) return;
            int columnChunks = ColumnChunks;
            if (columnChunks <= 0) return;
            _requestRemeshDepth++;
            try
            {
                var n = new ChunkCoord(coord.X + 1, coord.Y, coord.Z);
                if (n.Y >= 0 && n.Y < columnChunks) QueueRemesh(n);
                n = new ChunkCoord(coord.X - 1, coord.Y, coord.Z);
                if (n.Y >= 0 && n.Y < columnChunks) QueueRemesh(n);
                n = new ChunkCoord(coord.X, coord.Y + 1, coord.Z);
                if (n.Y >= 0 && n.Y < columnChunks) QueueRemesh(n);
                n = new ChunkCoord(coord.X, coord.Y - 1, coord.Z);
                if (n.Y >= 0 && n.Y < columnChunks) QueueRemesh(n);
                n = new ChunkCoord(coord.X, coord.Y, coord.Z + 1);
                if (n.Y >= 0 && n.Y < columnChunks) QueueRemesh(n);
                n = new ChunkCoord(coord.X, coord.Y, coord.Z - 1);
                if (n.Y >= 0 && n.Y < columnChunks) QueueRemesh(n);
            }
            finally { _requestRemeshDepth--; }
        }

        void ApplyChunkLayer(Chunk chunk)
        {
            if (chunk == null) return;
            if (string.IsNullOrWhiteSpace(chunkLayerName)) return;
            int layer = LayerMask.NameToLayer(chunkLayerName);
            if (layer < 0) return;
            SetLayerRecursively(chunk.transform, layer);
        }

        static void SetLayerRecursively(Transform t, int layer)
        {
            if (t == null) return;
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++)
                SetLayerRecursively(t.GetChild(i), layer);
        }

        void RebuildNeighbors(ChunkCoord coord)
        {
            if (_rebuildNeighborsDepth >= maxRebuildNeighborsDepth) return;
            _rebuildNeighborsDepth++;
            try
            {
                RebuildNeighborsInner(coord);
            }
            finally { _rebuildNeighborsDepth--; }
        }

        void RebuildNeighborsInner(ChunkCoord coord)
        {
            var neighbors = new[]
            {
                new ChunkCoord(coord.X + 1, coord.Y, coord.Z),
                new ChunkCoord(coord.X - 1, coord.Y, coord.Z),
                new ChunkCoord(coord.X, coord.Y + 1, coord.Z),
                new ChunkCoord(coord.X, coord.Y - 1, coord.Z),
                new ChunkCoord(coord.X, coord.Y, coord.Z + 1),
                new ChunkCoord(coord.X, coord.Y, coord.Z - 1)
            };

            foreach (var neighbor in neighbors)
            {
                if (!_active.ContainsKey(neighbor)) continue;
                if (IsChunkGenerating(neighbor)) continue;
                if (_meshJobs.ContainsKey(neighbor)) continue;
                if (_integrationSet.Contains(neighbor)) continue; // Don't remesh if already integrating
                if (_remeshSet.Contains(neighbor)) continue; // Don't add duplicate remesh requests
                
                // Invalidate mesh cache for neighbor since its neighbor (this chunk) changed
                if (enableMeshCache)
                {
                    ReleaseMeshCacheForChunk(neighbor);
                    // Also remove from pending cached meshes if present
                    if (_pendingCachedMeshes.ContainsKey(neighbor))
                    {
                        _pendingCachedMeshes.Remove(neighbor);
                        lock (_integrationLock)
                        {
                            if (_integrationSet.Contains(neighbor))
                                _integrationSet.Remove(neighbor);
                        }
                    }
                }
                
                QueueRemesh(neighbor);
            }
        }

        void QueueRemesh(ChunkCoord coord)
        {
            if (!_active.ContainsKey(coord)) return;
            if (svoManager != null)
                svoManager.ReleaseForChunk(coord);
            if (enableMeshCache && _active.TryGetValue(coord, out var chunk) && (chunk.UsesSvo || chunk.LodStep > 1))
            {
                ReleaseMeshCacheForChunk(coord);
                var n = new[] {
                    new ChunkCoord(coord.X + 1, coord.Y, coord.Z), new ChunkCoord(coord.X - 1, coord.Y, coord.Z),
                    new ChunkCoord(coord.X, coord.Y + 1, coord.Z), new ChunkCoord(coord.X, coord.Y - 1, coord.Z),
                    new ChunkCoord(coord.X, coord.Y, coord.Z + 1), new ChunkCoord(coord.X, coord.Y, coord.Z - 1)
                };
                for (int i = 0; i < 6; i++)
                    ReleaseMeshCacheForChunk(n[i]);
            }
            if (_remeshSet.Add(coord))
                _remeshQueue.Enqueue(coord);
        }
    }
}

