using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Generation;
using TerraVoxel.Voxel.Streaming;
using UnityEngine;

namespace TerraVoxel.Voxel.Save
{
    public class ChunkModManager : MonoBehaviour
    {
        [SerializeField] WorldGenConfig worldGen;
        [SerializeField] ChunkManager chunkManager;
        [SerializeField] string worldIdOverride = "";
        [SerializeField] bool useSeedAsWorldId = true;
        [SerializeField] string rootFolderName = "Worlds";
        [SerializeField] string modsFolderName = "mods";
        [SerializeField] bool loadOnSpawn = true;
        [SerializeField] bool saveOnUnload = true;
        [SerializeField] bool saveOnDestroy = true;
        [SerializeField] bool compress = true;
        [SerializeField] bool asyncWrite = true;
        [SerializeField] bool useRegionFolders = true;
        [SerializeField] int regionSize = 32;
        [SerializeField] bool unloadModsOnChunkUnload = true;
        [SerializeField] int workerJoinTimeoutMs = 200;

        readonly Dictionary<ChunkCoord, Dictionary<int, ushort>> _mods = new Dictionary<ChunkCoord, Dictionary<int, ushort>>();
        readonly Dictionary<ChunkCoord, ChunkMeta> _meta = new Dictionary<ChunkCoord, ChunkMeta>();
        readonly HashSet<ChunkCoord> _dirty = new HashSet<ChunkCoord>();
        readonly HashSet<ChunkCoord> _loaded = new HashSet<ChunkCoord>();

        readonly ConcurrentQueue<ModSaveRequest> _queue = new ConcurrentQueue<ModSaveRequest>();
        AutoResetEvent _signal;
        Thread _worker;
        volatile bool _stop;
        volatile bool _accepting = true;

        public bool LoadOnSpawn => loadOnSpawn;
        public bool SaveOnUnload => saveOnUnload;
        public bool SaveOnDestroy => saveOnDestroy;

        void Awake()
        {
            if (chunkManager == null) chunkManager = GetComponent<ChunkManager>();
        }

        void OnEnable()
        {
            if (asyncWrite) StartWorker();
        }

        void OnDisable()
        {
            StopWorker(flush: true);
        }

        public bool ApplyModsToChunk(ChunkCoord coord, ChunkData data)
        {
            return ApplyModsToChunk(coord, data, out _);
        }

        public bool ApplyModsToChunk(ChunkCoord coord, ChunkData data, out ChunkMeta meta)
        {
            meta = default;
            if (!loadOnSpawn) return false;
            EnsureLoaded(coord, force: true);
            if (!_mods.TryGetValue(coord, out var dict) || dict.Count == 0) return false;

            foreach (var kvp in dict)
            {
                if (kvp.Key >= 0 && kvp.Key < data.Materials.Length)
                    data.Materials[kvp.Key] = kvp.Value;
            }

            if (!_meta.TryGetValue(coord, out meta))
                meta = ChunkMeta.Default(ChunkSaveMode.DeltaBacked, GetGeneratorVersion());

            meta.SaveMode = ChunkSaveMode.DeltaBacked;
            meta.DeltaCount = dict.Count;
            _meta[coord] = meta;
            return true;
        }

        public void HandleChunkUnloaded(ChunkCoord coord)
        {
            if (saveOnUnload)
            {
                var meta = GetMetaOrDefault(coord);
                SaveMods(coord, meta);
            }

            if (unloadModsOnChunkUnload)
            {
                _mods.Remove(coord);
                _dirty.Remove(coord);
                _loaded.Remove(coord);
                _meta.Remove(coord);
            }
        }

        public void SaveDirtyAll()
        {
            if (_dirty.Count == 0) return;
            var list = new List<ChunkCoord>(_dirty);
            foreach (var coord in list)
            {
                var meta = GetMetaOrDefault(coord);
                SaveMods(coord, meta);
            }
        }

        public void SetVoxelWorld(Vector3 worldPos, byte material)
        {
            SetVoxelWorld(worldPos, (ushort)material);
        }

        public void SetVoxelWorld(Vector3 worldPos, ushort material)
        {
            WorldToVoxel(worldPos, out var coord, out int lx, out int ly, out int lz);
            SetVoxel(coord, lx, ly, lz, material);
        }

        public void SetVoxelsWorld(IReadOnlyList<Vector3Int> worldVoxels, ushort material, bool includeNeighbors = true)
        {
            if (worldVoxels == null || worldVoxels.Count == 0) return;

            int chunkSize = GetChunkSize();
            var ensured = new HashSet<ChunkCoord>();
            var touched = new HashSet<ChunkCoord>();

            for (int i = 0; i < worldVoxels.Count; i++)
            {
                Vector3Int v = worldVoxels[i];
                ChunkCoord coord = WorldToChunk(v.x, v.y, v.z, chunkSize);
                int lx = v.x - coord.X * chunkSize;
                int ly = v.y - coord.Y * chunkSize;
                int lz = v.z - coord.Z * chunkSize;
                if (lx < 0 || ly < 0 || lz < 0 || lx >= chunkSize || lz >= chunkSize || ly >= chunkSize)
                    continue;

                if (!ensured.Contains(coord))
                {
                    EnsureLoaded(coord, force: true);
                    ensured.Add(coord);
                }

                if (!_mods.TryGetValue(coord, out var dict))
                {
                    dict = new Dictionary<int, ushort>();
                    _mods[coord] = dict;
                }

                int index = lx + chunkSize * (ly + chunkSize * lz);
                dict[index] = material;
                _dirty.Add(coord);

                if (!_meta.TryGetValue(coord, out var meta))
                    meta = ChunkMeta.Default(ChunkSaveMode.DeltaBacked, GetGeneratorVersion());
                meta.SaveMode = ChunkSaveMode.DeltaBacked;
                meta.DeltaCount = dict.Count;
                _meta[coord] = meta;

                if (chunkManager != null && chunkManager.TryGetChunk(coord, out var chunk) && chunk.Data.IsCreated)
                {
                    if (index >= 0 && index < chunk.Data.Materials.Length)
                        chunk.Data.Materials[index] = material;
                }

                touched.Add(coord);
            }

            if (chunkManager != null)
            {
                foreach (var coord in touched)
                    chunkManager.RequestRemesh(coord, includeNeighbors);
            }
        }

        public void SetVoxelWorld(int worldX, int worldY, int worldZ, byte material)
        {
            SetVoxelWorld(worldX, worldY, worldZ, (ushort)material);
        }

        public void SetVoxelWorld(int worldX, int worldY, int worldZ, ushort material)
        {
            int chunkSize = GetChunkSize();
            ChunkCoord coord = WorldToChunk(worldX, worldY, worldZ, chunkSize);
            int lx = worldX - coord.X * chunkSize;
            int ly = worldY - coord.Y * chunkSize;
            int lz = worldZ - coord.Z * chunkSize;
            SetVoxel(coord, lx, ly, lz, material);
        }

        public void SetVoxel(ChunkCoord coord, int lx, int ly, int lz, byte material)
        {
            SetVoxel(coord, lx, ly, lz, (ushort)material);
        }

        public void SetVoxel(ChunkCoord coord, int lx, int ly, int lz, ushort material)
        {
            int chunkSize = GetChunkSize();
            if (lx < 0 || ly < 0 || lz < 0 || lx >= chunkSize || ly >= chunkSize || lz >= chunkSize)
                return;

            EnsureLoaded(coord, force: true);
            if (!_mods.TryGetValue(coord, out var dict))
            {
                dict = new Dictionary<int, ushort>();
                _mods[coord] = dict;
            }

            int index = lx + chunkSize * (ly + chunkSize * lz);
            dict[index] = material;
            _dirty.Add(coord);
            if (!_meta.TryGetValue(coord, out var meta))
                meta = ChunkMeta.Default(ChunkSaveMode.DeltaBacked, GetGeneratorVersion());
            meta.SaveMode = ChunkSaveMode.DeltaBacked;
            meta.DeltaCount = dict.Count;
            _meta[coord] = meta;

            if (chunkManager != null && chunkManager.TryGetChunk(coord, out var chunk) && chunk.Data.IsCreated)
            {
                if (index >= 0 && index < chunk.Data.Materials.Length)
                    chunk.Data.Materials[index] = material;
                chunkManager.RequestRemesh(coord, includeNeighbors: true);
            }
        }

        void EnsureLoaded(ChunkCoord coord, bool force)
        {
            if (_loaded.Contains(coord)) return;
            if (!force && !loadOnSpawn) return;

            _loaded.Add(coord);
            string path = GetChunkPath(coord);
            if (!File.Exists(path)) return;

            byte[] bytes = File.ReadAllBytes(path);
            if (!ChunkModBinary.TryDeserialize(bytes, out var payload)) return;
            if (payload.ChunkSize != GetChunkSize()) return;

            if (!_mods.TryGetValue(coord, out var dict))
            {
                dict = new Dictionary<int, ushort>();
                _mods[coord] = dict;
            }
            payload.Meta.SaveMode = ChunkSaveMode.DeltaBacked;
            _meta[coord] = payload.Meta;

            if (payload.Entries != null)
            {
                foreach (var entry in payload.Entries)
                    dict[entry.Index] = entry.Material;
            }
        }

        public void SaveMods(ChunkCoord coord, ChunkMeta meta)
        {
            if (!_accepting) return;
            SaveChunkMods(coord, meta);
        }

        void SaveChunkMods(ChunkCoord coord, ChunkMeta meta)
        {
            if (!_mods.TryGetValue(coord, out var dict) || dict.Count == 0)
            {
                DeleteChunkFile(coord);
                _dirty.Remove(coord);
                return;
            }

            var entries = new ChunkModBinary.ModEntry[dict.Count];
            int i = 0;
            foreach (var kvp in dict)
            {
                entries[i++] = new ChunkModBinary.ModEntry { Index = kvp.Key, Material = kvp.Value };
            }

            meta.SaveMode = ChunkSaveMode.DeltaBacked;
            meta.DeltaCount = dict.Count;
            if (meta.GeneratorVersion == 0) meta.GeneratorVersion = GetGeneratorVersion();
            _meta[coord] = meta;

            var payload = new ChunkModBinary.Payload
            {
                Coord = coord,
                ChunkSize = GetChunkSize(),
                Entries = entries,
                Meta = meta
            };

            var request = new ModSaveRequest
            {
                Path = GetChunkPath(coord),
                Payload = payload,
                Compress = compress
            };

            if (asyncWrite)
            {
                _queue.Enqueue(request);
                _signal.Set();
            }
            else
            {
                WriteRequest(request);
            }

            _dirty.Remove(coord);
        }

        void DeleteChunkFile(ChunkCoord coord)
        {
            string path = GetChunkPath(coord);
            if (File.Exists(path))
                File.Delete(path);
        }

        void StartWorker()
        {
            if (_worker != null) return;
            _accepting = true;
            _stop = false;
            _signal = new AutoResetEvent(false);
            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "ChunkModsWriter"
            };
            _worker.Start();
        }

        void StopWorker(bool flush)
        {
            _accepting = false;
            _stop = true;
            _signal?.Set();
            if (_worker != null)
            {
                if (!_worker.Join(workerJoinTimeoutMs))
                    Debug.LogWarning("[ChunkModManager] Mod worker did not stop in time.");
                _worker = null;
            }
            _signal?.Dispose();
            _signal = null;

            if (flush)
                FlushPendingOnMainThread();
        }

        void WorkerLoop()
        {
            while (true)
            {
                _signal.WaitOne();
                while (_queue.TryDequeue(out var request))
                {
                    WriteRequest(request);
                }

                if (_stop && _queue.IsEmpty)
                    return;
            }
        }

        void FlushPendingOnMainThread()
        {
            while (_queue.TryDequeue(out var request))
            {
                WriteRequest(request);
            }
        }

        void WriteRequest(ModSaveRequest request)
        {
            try
            {
                string dir = Path.GetDirectoryName(request.Path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                byte[] bytes = ChunkModBinary.Serialize(request.Payload, request.Compress);
                string tempPath = request.Path + ".tmp";
                File.WriteAllBytes(tempPath, bytes);

                if (File.Exists(request.Path))
                {
                    File.Replace(tempPath, request.Path, null);
                }
                else
                {
                    File.Move(tempPath, request.Path);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ChunkMods] Write failed: {ex.Message}");
            }
        }

        public bool ModsFileExists(ChunkCoord coord)
        {
            return File.Exists(GetChunkPath(coord));
        }

        public void DeleteMods(ChunkCoord coord)
        {
            DeleteChunkFile(coord);
            _mods.Remove(coord);
            _dirty.Remove(coord);
            _loaded.Remove(coord);
            _meta.Remove(coord);
        }

        public int GetDeltaCount(ChunkCoord coord)
        {
            return _mods.TryGetValue(coord, out var dict) ? dict.Count : 0;
        }

        public bool TryGetMeta(ChunkCoord coord, out ChunkMeta meta)
        {
            return _meta.TryGetValue(coord, out meta);
        }

        public void SetMeta(ChunkCoord coord, ChunkMeta meta)
        {
            _meta[coord] = meta;
        }

        int GetChunkSize()
        {
            return worldGen != null ? worldGen.ChunkSize : VoxelConstants.ChunkSize;
        }

        int GetGeneratorVersion()
        {
            return worldGen != null ? worldGen.GeneratorVersion : 0;
        }

        static ChunkCoord WorldToChunk(int wx, int wy, int wz, int chunkSize)
        {
            int cx = VoxelMath.FloorToIntClamped((double)wx / chunkSize);
            int cy = VoxelMath.FloorToIntClamped((double)wy / chunkSize);
            int cz = VoxelMath.FloorToIntClamped((double)wz / chunkSize);
            return new ChunkCoord(cx, cy, cz);
        }

        void WorldToVoxel(Vector3 worldPos, out ChunkCoord coord, out int lx, out int ly, out int lz)
        {
            double inv = 1d / VoxelConstants.VoxelSize;
            int wx = VoxelMath.FloorToIntClamped(worldPos.x * inv);
            int wy = VoxelMath.FloorToIntClamped(worldPos.y * inv);
            int wz = VoxelMath.FloorToIntClamped(worldPos.z * inv);

            int chunkSize = GetChunkSize();
            coord = WorldToChunk(wx, wy, wz, chunkSize);
            lx = wx - coord.X * chunkSize;
            ly = wy - coord.Y * chunkSize;
            lz = wz - coord.Z * chunkSize;
        }

        string GetChunkPath(ChunkCoord coord)
        {
            string worldId = GetWorldId();
            string worldPath = Path.Combine(Application.persistentDataPath, rootFolderName, worldId, modsFolderName);

            if (useRegionFolders && regionSize > 0)
            {
                int rx = Mathf.FloorToInt((float)coord.X / regionSize);
                int rz = Mathf.FloorToInt((float)coord.Z / regionSize);
                worldPath = Path.Combine(worldPath, $"r.{rx}.{rz}");
            }

            string file = $"m.{coord.X}.{coord.Y}.{coord.Z}.tvxm";
            return Path.Combine(worldPath, file);
        }

        string GetWorldId()
        {
            string id = worldIdOverride;
            if (string.IsNullOrWhiteSpace(id) && useSeedAsWorldId && worldGen != null)
                id = $"seed_{worldGen.Seed}";

            if (string.IsNullOrWhiteSpace(id))
                id = "default";

            return SanitizeFileName(id);
        }

        static string SanitizeFileName(string value)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (var c in invalid)
            {
                value = value.Replace(c, '_');
            }
            return value;
        }

        ChunkMeta GetMetaOrDefault(ChunkCoord coord)
        {
            if (_meta.TryGetValue(coord, out var meta))
                return meta;

            return ChunkMeta.Default(ChunkSaveMode.DeltaBacked, GetGeneratorVersion());
        }

        struct ModSaveRequest
        {
            public string Path;
            public ChunkModBinary.Payload Payload;
            public bool Compress;
        }
    }
}

