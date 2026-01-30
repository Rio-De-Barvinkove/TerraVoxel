using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Generation;
using Unity.Collections;
using UnityEngine;
using IoCompressionLevel = System.IO.Compression.CompressionLevel;

namespace TerraVoxel.Voxel.Save
{
    public class ChunkSaveManager : MonoBehaviour
    {
        [SerializeField] WorldGenConfig worldGen;
        [SerializeField] string worldIdOverride = "";
        [SerializeField] bool useSeedAsWorldId = true;
        [SerializeField] string rootFolderName = "Worlds";
        [SerializeField] bool loadOnSpawn = true;
        [SerializeField] bool saveOnUnload = true;
        [SerializeField] bool saveOnDestroy = true;
        [SerializeField] bool compress = true;
        [SerializeField] IoCompressionLevel compressionLevel = IoCompressionLevel.Fastest;
        [SerializeField] bool saveDensity = false;
        [SerializeField] bool asyncWrite = true;
        [SerializeField] bool useRegionFolders = true;
        [SerializeField] int regionSize = 32;
        [SerializeField] int workerJoinTimeoutMs = 200;

        readonly ConcurrentQueue<ChunkSaveRequest> _queue = new ConcurrentQueue<ChunkSaveRequest>();
        AutoResetEvent _signal;
        Thread _worker;
        volatile bool _stop;
        volatile bool _accepting = true;

        public bool LoadOnSpawn => loadOnSpawn;
        public bool SaveOnUnload => saveOnUnload;
        public bool SaveOnDestroy => saveOnDestroy;
        public bool SaveDensity => saveDensity;

        void OnEnable()
        {
            if (asyncWrite) StartWorker();
        }

        void OnDisable()
        {
            StopWorker(flush: true);
        }

        public bool TryLoadInto(ChunkCoord coord, ChunkData data)
        {
            return TryLoadInto(coord, data, out _);
        }

        public bool TryLoadInto(ChunkCoord coord, ChunkData data, out ChunkMeta meta)
        {
            meta = default;
            string path = GetChunkPath(coord);
            if (!File.Exists(path)) return false;

            byte[] bytes = File.ReadAllBytes(path);
            if (!ChunkSaveBinary.TryDeserialize(bytes, out var payload)) return false;
            if (payload.ChunkSize != data.Size) return false;
            if (payload.Materials == null || payload.Materials.Length != data.Materials.Length) return false;

            data.Materials.CopyFrom(payload.Materials);

            if (saveDensity && payload.DensityBytes != null && data.Density.IsCreated)
            {
                int expected = data.Density.Length * sizeof(float);
                if (payload.DensityBytes.Length == expected)
                {
                    var density = new float[data.Density.Length];
                    Buffer.BlockCopy(payload.DensityBytes, 0, density, 0, expected);
                    data.Density.CopyFrom(density);
                }
            }

            meta = payload.Meta;
            return true;
        }

        public void EnqueueSave(ChunkCoord coord, ChunkData data)
        {
            int generatorVersion = worldGen != null ? worldGen.GeneratorVersion : 0;
            var meta = ChunkMeta.Default(ChunkSaveMode.SnapshotBacked, generatorVersion);
            EnqueueSave(coord, data, meta);
        }

        public void EnqueueSave(ChunkCoord coord, ChunkData data, ChunkMeta meta)
        {
            if (!_accepting) return;
            if (!data.IsCreated) return;

            var payload = new ChunkSaveBinary.Payload
            {
                Coord = coord,
                ChunkSize = data.Size,
                Materials = data.Materials.ToArray(),
                DensityBytes = saveDensity ? ToBytes(data.Density) : null,
                Meta = meta
            };

            var request = new ChunkSaveRequest
            {
                Path = GetChunkPath(coord),
                Payload = payload,
                Compress = compress,
                CompressionLevel = compressionLevel
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
        }

        public void SaveAll(IEnumerable<Chunk> chunks)
        {
            if (chunks == null) return;
            foreach (var chunk in chunks)
            {
                if (chunk == null) continue;
                EnqueueSave(chunk.Coord, chunk.Data);
            }
        }

        public void FlushBlocking()
        {
            StopWorker(flush: true);
            if (asyncWrite) StartWorker();
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
                Name = "ChunkSaveWriter"
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
                    Debug.LogWarning("[ChunkSaveManager] Save worker did not stop in time.");
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

        void WriteRequest(ChunkSaveRequest request)
        {
            try
            {
                string dir = Path.GetDirectoryName(request.Path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                byte[] bytes = ChunkSaveBinary.Serialize(request.Payload, request.Compress, request.CompressionLevel);
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
                Debug.LogError($"[ChunkSave] Write failed: {ex.Message}");
            }
        }

        public bool SnapshotExists(ChunkCoord coord)
        {
            return File.Exists(GetChunkPath(coord));
        }

        public void DeleteSnapshot(ChunkCoord coord)
        {
            string path = GetChunkPath(coord);
            if (File.Exists(path))
                File.Delete(path);
        }

        string GetChunkPath(ChunkCoord coord)
        {
            string worldId = GetWorldId();
            string worldPath = Path.Combine(Application.persistentDataPath, rootFolderName, worldId, "chunks");

            if (useRegionFolders && regionSize > 0)
            {
                int rx = Mathf.FloorToInt((float)coord.X / regionSize);
                int rz = Mathf.FloorToInt((float)coord.Z / regionSize);
                worldPath = Path.Combine(worldPath, $"r.{rx}.{rz}");
            }

            string file = $"c.{coord.X}.{coord.Y}.{coord.Z}.tvx";
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

        static byte[] ToBytes(Unity.Collections.NativeArray<float> values)
        {
            if (!values.IsCreated || values.Length == 0) return null;
            var managed = values.ToArray();
            var bytes = new byte[managed.Length * sizeof(float)];
            Buffer.BlockCopy(managed, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        struct ChunkSaveRequest
        {
            public string Path;
            public ChunkSaveBinary.Payload Payload;
            public bool Compress;
            public IoCompressionLevel CompressionLevel;
        }
    }
}

