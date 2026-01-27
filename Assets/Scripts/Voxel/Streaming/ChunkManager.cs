using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Generation;
using TerraVoxel.Voxel.Meshing;
using TerraVoxel.Voxel.Rendering;
using TerraVoxel.Voxel.Save;
using Unity.Collections;
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>
    /// Maintains active chunks around a tracked transform. Spawns limited count per frame.
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
        [SerializeField] int maxRemeshPerFrame = 2;
        [SerializeField] int maxRemovalsPerFrame = 2;
        [SerializeField] int maxGenJobsInFlight = 2;
        [SerializeField] int maxMeshJobsInFlight = 2;
        [SerializeField] int maxIntegrationsPerFrame = 1;
        [Header("Streaming Control")]
        [SerializeField] bool streamingPaused = false;
        [Header("Preload")]
        [SerializeField] bool enablePreload = false;
        [SerializeField] int preloadRadius = 4;
        [SerializeField] int maxPreloadsPerFrame = 1;
        [Header("Pending Queue")]
        [SerializeField] int pendingQueueCap = 4096;
        [SerializeField] int pendingResetDistance = 8;
        [Header("View Cone")]
        [SerializeField] ChunkViewConePrioritizer viewCone;
        [Header("Streaming Budget")]
        [SerializeField] StreamingTimeBudget streamingBudget = new StreamingTimeBudget();
        [SerializeField] string chunkLayerName = "Terrain";
        [SerializeField] ChunkSaveManager saveManager;
        [SerializeField] ChunkModManager modManager;
        [SerializeField] ChunkHybridSaveManager hybridSave;
        [Header("Chunk Data Cache")]
        [SerializeField] bool enableDataCache = true;
        [SerializeField] int maxCachedChunks = 500;
        [SerializeField] int maxCacheOpsPerFrame = 2;

        readonly Dictionary<ChunkCoord, Chunk> _active = new Dictionary<ChunkCoord, Chunk>();
        readonly Dictionary<ChunkCoord, CachedChunkData> _dataCache = new Dictionary<ChunkCoord, CachedChunkData>();
        int _cacheOpsThisFrame;
        readonly Queue<ChunkCoord> _pending = new Queue<ChunkCoord>();
        readonly Queue<ChunkCoord> _preload = new Queue<ChunkCoord>();
        readonly Queue<ChunkCoord> _removeQueue = new Queue<ChunkCoord>();
        readonly Queue<ChunkCoord> _remeshQueue = new Queue<ChunkCoord>();
        readonly HashSet<ChunkCoord> _preloadSet = new HashSet<ChunkCoord>();
        readonly HashSet<ChunkCoord> _preloaded = new HashSet<ChunkCoord>();
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
        int _integrationsLastFrame;
        ChunkPool _pool;
        IChunkGenerator _generator;
        long _lastGenMs;
        long _lastMeshMs;
        long _lastTotalMs;
        ChunkCoord _lastSpawnCoord;
        ChunkCoord _lastPendingCenter;
        bool _hasPendingCenter;
        int _spawnedLastFrame;

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
                
                if (Materials.IsCreated) Materials.Dispose();
                Materials = new NativeArray<ushort>(source.Materials.Length, Allocator.Persistent);
                NativeArray<ushort>.Copy(source.Materials, Materials);

                if (HasDensity)
                {
                    if (Density.IsCreated) Density.Dispose();
                    Density = new NativeArray<float>(source.Density.Length, Allocator.Persistent);
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

        public int ActiveCount => _active.Count;
        public int PendingCount => _pending.Count;
        public int SpawnedLastFrame => _spawnedLastFrame;
        public int IntegrationQueueCount => _integrationQueue.Count;
        public int IntegrationsLastFrame => _integrationsLastFrame;
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
        public Transform PlayerTransform => player;
        public IEnumerable<KeyValuePair<ChunkCoord, Chunk>> ActiveChunks => _active;
        public bool IsPreloaded(ChunkCoord coord) => _preloaded.Contains(coord);
        public int CachedChunksCount => _dataCache.Count;

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
            streamingBudget?.BeginFrame();
            _cacheOpsThisFrame = 0;
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
            if (physicsOptimizer != null)
                physicsOptimizer.Tick(this);
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

        void ActivatePreloadedChunk(ChunkCoord coord, Chunk chunk)
        {
            if (!_preloaded.Remove(coord)) return;
            if (chunk == null) return;
            chunk.SetRendererEnabled(true);
            if (addColliders)
                chunk.SetColliderEnabled(true);
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
            if (ShouldRebuildPending(center))
                RebuildPendingQueue(center);
            int effectivePreloadRadius = EffectivePreloadRadius();
            var needed = new HashSet<ChunkCoord>();

            for (int dz = -loadRadius; dz <= loadRadius; dz++)
            {
                for (int dx = -loadRadius; dx <= loadRadius; dx++)
                {
                    for (int dy = 0; dy < worldGen.ColumnChunks; dy++)
                    {
                        var coord = new ChunkCoord(center.X + dx, dy, center.Z + dz);
                        needed.Add(coord);

                        if (_active.TryGetValue(coord, out var existing))
                        {
                            if (_preloaded.Contains(coord))
                                ActivatePreloadedChunk(coord, existing);
                            continue;
                        }
                        if (_pending.Contains(coord)) continue;
                        _pending.Enqueue(coord);
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
                            if (_pending.Contains(coord)) continue;
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
            var keep = new HashSet<ChunkCoord>();
            for (int dz = -keepRadius; dz <= keepRadius; dz++)
            {
                for (int dx = -keepRadius; dx <= keepRadius; dx++)
                {
                    for (int dy = 0; dy < worldGen.ColumnChunks; dy++)
                    {
                        var coord = new ChunkCoord(center.X + dx, dy, center.Z + dz);
                        keep.Add(coord);
                    }
                }
            }

            var toRemove = new List<RemoveCandidate>();
            foreach (var kvp in _active)
            {
                if (keep.Contains(kvp.Key)) continue;
                int dx = kvp.Key.X - center.X;
                int dy = kvp.Key.Y - center.Y;
                int dz = kvp.Key.Z - center.Z;
                int dist = dx * dx + dy * dy + dz * dz;
                toRemove.Add(new RemoveCandidate(kvp.Key, dist));
            }

            toRemove.Sort((a, b) => b.Distance.CompareTo(a.Distance));
            foreach (var c in toRemove)
                QueueRemoval(c.Coord);
        }

        void ProcessPending()
        {
            if (player == null || worldGen == null) return;
            ChunkCoord center = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize);

            int spawned = 0;
            while (_pending.Count > 0 && spawned < maxSpawnsPerFrame)
            {
                if (BudgetExceeded()) break;
                if (_genJobs.Count >= maxGenJobsInFlight) break;
                if (!TryDequeuePending(center, out var coord))
                    break;
                if (!IsWithinLoadRadius(coord, center, loadRadius)) continue;
                if (_active.ContainsKey(coord)) continue;
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
            while (_preload.Count > 0 && spawned < maxPreloadsPerFrame)
            {
                if (BudgetExceeded()) break;
                if (_genJobs.Count >= maxGenJobsInFlight) break;
                var coord = _preload.Dequeue();
                _preloadSet.Remove(coord);

                if (!IsWithinLoadRadius(coord, center, effectivePreloadRadius)) continue;
                if (IsWithinLoadRadius(coord, center, loadRadius))
                {
                    if (!_active.ContainsKey(coord) && !_pending.Contains(coord))
                        _pending.Enqueue(coord);
                    continue;
                }
                if (_active.ContainsKey(coord)) continue;

                SpawnChunk(coord, preload: true);
                spawned++;
            }
        }

        void ProcessGenJobs()
        {
            if (_genJobs.Count == 0) return;
            _genCompleted.Clear();
            foreach (var kvp in _genJobs)
            {
                if (kvp.Value.Job.Handle.IsCompleted)
                    _genCompleted.Add(kvp.Key);
            }

            foreach (var coord in _genCompleted)
            {
                if (!_genJobs.TryGetValue(coord, out var task)) continue;
                task.Job.Handle.Complete();
                task.Job.Dispose();
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

                if (!ScheduleMeshForChunk(coord, task.SpawnStart))
                    QueueRemesh(coord);
            }
        }

        void ProcessMeshJobs()
        {
            if (_meshJobs.Count == 0) return;
            _meshCompleted.Clear();
            foreach (var kvp in _meshJobs)
            {
                if (kvp.Value.Job.Handle.IsCompleted)
                    _meshCompleted.Add(kvp.Key);
            }

            foreach (var coord in _meshCompleted)
            {
                if (!_meshJobs.TryGetValue(coord, out var task)) continue;
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

                // Відкласти інтеграцію в окрему чергу
                if (!_integrationSet.Contains(coord))
                {
                    _pendingMeshJobs[coord] = task.Job;
                    _integrationQueue.Enqueue(coord);
                    _integrationSet.Add(coord);
                }
                else
                {
                    // Якщо вже в черзі, dispose старий job
                    if (_pendingMeshJobs.TryGetValue(coord, out var oldJob))
                    {
                        oldJob.Dispose();
                    }
                    _pendingMeshJobs[coord] = task.Job;
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

            while (_integrationQueue.Count > 0 && integrationsThisFrame < maxIntegrationsPerFrame)
            {
                if (streamingBudget != null && streamingBudget.IsExceeded())
                    break;

                ChunkCoord coord = _integrationQueue.Dequeue();
                _integrationSet.Remove(coord);

                // Логування для дебагу (можна видалити пізніше)
                if (integrationsThisFrame == 0 && _integrationQueue.Count > 5)
                {
                    Debug.Log($"[ChunkManager] Integration queue: {_integrationQueue.Count} pending, processing {coord}");
                }

                if (!_active.TryGetValue(coord, out var chunk))
                {
                    // Чанк видалено: dispose job
                    if (_pendingMeshJobs.TryGetValue(coord, out var job))
                    {
                        job.Dispose();
                        _pendingMeshJobs.Remove(coord);
                    }
                    continue;
                }

                if (!_pendingMeshJobs.TryGetValue(coord, out var meshJob))
                {
                    continue; // Job втрачено
                }

                bool applyCollider = addColliders && !_preloaded.Contains(coord);
                chunk.ApplyMesh(meshJob.MeshData, applyCollider);

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

            int count = 0;
            int guard = _removeQueue.Count;
            while (_removeQueue.Count > 0 && count < maxRemovalsPerFrame && guard-- > 0)
            {
                if (BudgetExceeded()) break;
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

            if (pendingQueueCap > 0 && _pending.Count > pendingQueueCap)
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
                        _pending.Enqueue(coord);
                    }
                }
            }
        }

        bool TryDequeuePending(ChunkCoord center, out ChunkCoord coord)
        {
            if (_pending.Count == 0)
            {
                coord = default;
                return false;
            }
            if (viewCone != null && viewCone.Enabled)
                return viewCone.TryDequeue(_pending, center, player, out coord);
            coord = _pending.Dequeue();
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

            var handle = _generator.Schedule(chunk.Data, coord, worldGen, noiseStack, out var layers);
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
                ApplyDelta = applyDelta
            };
        }

        bool ScheduleMeshForChunk(ChunkCoord coord, double spawnStart)
        {
            if (!_active.TryGetValue(coord, out var chunk)) return false;
            if (!chunk.Data.IsCreated) return false;
            if (IsChunkGenerating(coord)) return false;
            if (_meshJobs.ContainsKey(coord)) return false;
            if (_meshJobs.Count >= maxMeshJobsInFlight) return false;

            var neighbors = GatherNeighborCopies(coord);
            var meshData = new MeshData(Unity.Collections.Allocator.Persistent);
            var materialsCopy = new NativeArray<ushort>(chunk.Data.Materials.Length, Allocator.Persistent);
            NativeArray<ushort>.Copy(chunk.Data.Materials, materialsCopy);
            var dataCopy = new ChunkData { Materials = materialsCopy, Size = chunk.Data.Size };
            var mask = new NativeArray<GreedyMesher.MaskCell>(chunk.Data.Size * chunk.Data.Size, Allocator.Persistent);
            var empty = new NativeArray<ushort>(0, Allocator.Persistent);

            GetMeshMaterialSettings(chunk, out var maxMaterialIndex, out var fallbackMaterialIndex);
            var handle = GreedyMesher.Schedule(dataCopy, neighbors.Data, maxMaterialIndex, fallbackMaterialIndex, mask, empty, ref meshData);

            var meshJob = new ChunkMeshJobHandle
            {
                Handle = handle,
                MeshData = meshData,
                MaterialsCopy = materialsCopy,
                Mask = mask,
                Empty = empty,
                Neighbors = neighbors
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
                if (_meshJobs.Count >= maxMeshJobsInFlight) break;

                var coord = _remeshQueue.Dequeue();
                if (!_active.ContainsKey(coord))
                {
                    _remeshSet.Remove(coord);
                    continue;
                }
                if (_meshJobs.ContainsKey(coord))
                {
                    _remeshQueue.Enqueue(coord);
                    continue;
                }
                if (IsChunkGenerating(coord))
                {
                    _remeshQueue.Enqueue(coord);
                    continue;
                }

                if (ScheduleMeshForChunk(coord, 0))
                {
                    _remeshSet.Remove(coord);
                    count++;
                }
                else
                {
                    _remeshQueue.Enqueue(coord);
                }
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

                if (!ScheduleMeshForChunk(coord, spawnStart))
                    QueueRemesh(coord);
            }
            else
            {
                ScheduleGenJob(coord, chunk, spawnStart, applySafeSpawn, applyDelta);
            }

        }

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

            // Remove oldest entries if cache is full
            while (_dataCache.Count >= maxCachedChunks && _dataCache.Count > 0)
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

        bool TryLoadFromCache(ChunkCoord coord, ChunkData data)
        {
            if (!enableDataCache) return false;
            if (!_dataCache.TryGetValue(coord, out var cached)) return false;
            if (!cached.IsValid) return false;

            cached.CopyTo(data);
            return true;
        }

        public bool TryGetChunk(ChunkCoord coord, out Chunk chunk)
        {
            if (!_active.TryGetValue(coord, out chunk)) return false;
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
            if (!includeNeighbors) return;
            QueueRemesh(new ChunkCoord(coord.X + 1, coord.Y, coord.Z));
            QueueRemesh(new ChunkCoord(coord.X - 1, coord.Y, coord.Z));
            QueueRemesh(new ChunkCoord(coord.X, coord.Y + 1, coord.Z));
            QueueRemesh(new ChunkCoord(coord.X, coord.Y - 1, coord.Z));
            QueueRemesh(new ChunkCoord(coord.X, coord.Y, coord.Z + 1));
            QueueRemesh(new ChunkCoord(coord.X, coord.Y, coord.Z - 1));
        }

        void ApplyChunkLayer(Chunk chunk)
        {
            if (chunk == null) return;
            if (string.IsNullOrWhiteSpace(chunkLayerName)) return;
            int layer = LayerMask.NameToLayer(chunkLayerName);
            if (layer < 0) return;
            chunk.gameObject.layer = layer;
        }

        void RebuildNeighbors(ChunkCoord coord)
        {
            QueueRemesh(new ChunkCoord(coord.X + 1, coord.Y, coord.Z));
            QueueRemesh(new ChunkCoord(coord.X - 1, coord.Y, coord.Z));
            QueueRemesh(new ChunkCoord(coord.X, coord.Y + 1, coord.Z));
            QueueRemesh(new ChunkCoord(coord.X, coord.Y - 1, coord.Z));
            QueueRemesh(new ChunkCoord(coord.X, coord.Y, coord.Z + 1));
            QueueRemesh(new ChunkCoord(coord.X, coord.Y, coord.Z - 1));
        }

        void QueueRemesh(ChunkCoord coord)
        {
            if (!_active.ContainsKey(coord)) return;
            if (_remeshSet.Add(coord))
                _remeshQueue.Enqueue(coord);
        }
    }
}

