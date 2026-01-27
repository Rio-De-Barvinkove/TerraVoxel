using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Threading;
using TerraVoxel.Voxel.Streaming;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

namespace TerraVoxel.Voxel.Systems
{
    public class VoxelDebugHUD : MonoBehaviour
    {
        [SerializeField] ChunkManager chunkManager;
        [SerializeField] KeyCode toggleKey = KeyCode.F3;
        [SerializeField] KeyCode saveCsvKey = KeyCode.F6;
        [SerializeField] Vector2 offset = new Vector2(12f, 12f);
        [SerializeField] int fontSize = 14;
        [SerializeField] Color textColor = Color.white;
        [SerializeField] float sampleInterval = 0.2f;
        [SerializeField] float summaryIntervalSeconds = 60f;
        [SerializeField] bool logSummaryToConsole = true;
        [SerializeField] bool writeSummaryToFile = true;
        [SerializeField] string summaryFileName = "WorldLogger.performance.csv";
        [SerializeField] int graphSamples = 300;

        enum HudMode { Off, Compact, Extended }
        HudMode _mode = HudMode.Compact;

        float _fps;
        float _fpsTimer;
        int _fpsFrames;
        GUIStyle _style;
        Texture2D _lineTex;

        float _sampleTimer;
        float _summaryTimer;
        float _sumCpuMs;
        float _sumGpuMs;
        float _sumVramMb;
        float _sumRamMb;
        int _sumCount;
        int _sumActive;
        int _sumPending;
        int _sumSpawned;
        string _summaryPath;
        volatile bool _summaryHeaderWritten;

        readonly ConcurrentQueue<SummaryWriteRequest> _summaryQueue = new ConcurrentQueue<SummaryWriteRequest>();
        AutoResetEvent _summarySignal;
        Thread _summaryWorker;
        volatile bool _summaryStop;
        volatile bool _summaryAccepting = true;

        int _sampleIndex;
        int _sampleCount;
        float[] _times;
        float[] _cpuMs;
        float[] _gpuMs;
        float[] _vramMb;
        float[] _ramMb;
        int[] _active;
        int[] _pending;
        int[] _spawned;

        ProfilerRecorder _mainThreadRecorder;
        ProfilerRecorder _gpuRecorder;

        void OnEnable()
        {
            _times = new float[graphSamples];
            _cpuMs = new float[graphSamples];
            _gpuMs = new float[graphSamples];
            _vramMb = new float[graphSamples];
            _ramMb = new float[graphSamples];
            _active = new int[graphSamples];
            _pending = new int[graphSamples];
            _spawned = new int[graphSamples];

            _mainThreadRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 1);
            _gpuRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GPU Frame Time", 1);

            if (writeSummaryToFile)
                StartSummaryWorker();
        }

        void OnDisable()
        {
            StopSummaryWorker(flush: true);
            if (_mainThreadRecorder.Valid) _mainThreadRecorder.Dispose();
            if (_gpuRecorder.Valid) _gpuRecorder.Dispose();
        }

        void Update()
        {
            if (Input.GetKeyDown(toggleKey))
            {
                _mode = _mode == HudMode.Off ? HudMode.Compact : (_mode == HudMode.Compact ? HudMode.Extended : HudMode.Off);
            }
            if (Input.GetKeyDown(saveCsvKey))
                SaveCsv();

            _fpsFrames++;
            _fpsTimer += Time.unscaledDeltaTime;
            if (_fpsTimer >= 0.5f)
            {
                _fps = _fpsFrames / _fpsTimer;
                _fpsFrames = 0;
                _fpsTimer = 0f;
            }

            if (chunkManager == null)
                chunkManager = FindObjectOfType<ChunkManager>();

            _sampleTimer += Time.unscaledDeltaTime;
            _summaryTimer += Time.unscaledDeltaTime;

            if (_sampleTimer >= sampleInterval)
            {
                _sampleTimer = 0f;
                SampleMetrics();
            }

            if (logSummaryToConsole && _summaryTimer >= summaryIntervalSeconds)
            {
                _summaryTimer = 0f;
                LogSummary();
            }
        }

        void SampleMetrics()
        {
            float cpuMs = GetMainThreadMs();
            float gpuMs = GetGpuMs();
            float vramMb = Profiler.GetAllocatedMemoryForGraphicsDriver() / (1024f * 1024f);
            float ramMb = Profiler.GetTotalAllocatedMemoryLong() / (1024f * 1024f);

            _times[_sampleIndex] = Time.unscaledTime;
            _cpuMs[_sampleIndex] = cpuMs;
            _gpuMs[_sampleIndex] = gpuMs;
            _vramMb[_sampleIndex] = vramMb;
            _ramMb[_sampleIndex] = ramMb;
            _active[_sampleIndex] = chunkManager != null ? chunkManager.ActiveCount : 0;
            _pending[_sampleIndex] = chunkManager != null ? chunkManager.PendingCount : 0;
            _spawned[_sampleIndex] = chunkManager != null ? chunkManager.SpawnedLastFrame : 0;
            _sampleIndex = (_sampleIndex + 1) % graphSamples;
            if (_sampleCount < graphSamples) _sampleCount++;

            if (cpuMs >= 0) _sumCpuMs += cpuMs;
            if (gpuMs >= 0) _sumGpuMs += gpuMs;
            _sumVramMb += vramMb;
            _sumRamMb += ramMb;
            _sumActive += _active[_sampleIndex];
            _sumPending += _pending[_sampleIndex];
            _sumSpawned += _spawned[_sampleIndex];
            _sumCount++;
        }

        void LogSummary()
        {
            if (_sumCount == 0) return;
            float avgCpu = _sumCpuMs / _sumCount;
            float avgGpu = _sumGpuMs > 0 ? _sumGpuMs / _sumCount : -1f;
            float avgVram = _sumVramMb / _sumCount;
            float avgRam = _sumRamMb / _sumCount;

            float avgActive = _sumActive / (float)_sumCount;
            float avgPending = _sumPending / (float)_sumCount;
            float avgSpawned = _sumSpawned / (float)_sumCount;

            if (logSummaryToConsole)
            {
                string gpuText = avgGpu >= 0 ? $"{avgGpu:0.0}ms" : "n/a";
                string text =
                    $"[VoxelHUD] FPS:{_fps:0.0} CPU:{avgCpu:0.0}ms GPU:{gpuText} VRAM:{avgVram:0.0}MB RAM:{avgRam:0.0}MB " +
                    $"Active:{avgActive:0} Pending:{avgPending:0} Spawned:{avgSpawned:0} " +
                    $"Gen:{chunkManager?.LastGenMs ?? 0}ms Mesh:{chunkManager?.LastMeshMs ?? 0}ms Total:{chunkManager?.LastTotalMs ?? 0}ms";
                Debug.Log(text);
            }

            if (writeSummaryToFile)
                WriteSummaryToFile(avgCpu, avgGpu, avgVram, avgRam, avgActive, avgPending, avgSpawned);

            _sumCpuMs = 0;
            _sumGpuMs = 0;
            _sumVramMb = 0;
            _sumRamMb = 0;
            _sumCount = 0;
            _sumActive = 0;
            _sumPending = 0;
            _sumSpawned = 0;
        }

        float GetMainThreadMs()
        {
            if (_mainThreadRecorder.Valid && _mainThreadRecorder.Count > 0)
            {
                // ProfilerRecorder values are in nanoseconds for these counters.
                return _mainThreadRecorder.LastValue / 1_000_000f;
            }
            return Time.unscaledDeltaTime * 1000f;
        }

        float GetGpuMs()
        {
            if (_gpuRecorder.Valid && _gpuRecorder.Count > 0)
            {
                return _gpuRecorder.LastValue / 1_000_000f;
            }
            return -1f;
        }

        void OnGUI()
        {
            if (_mode == HudMode.Off || chunkManager == null) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = fontSize,
                    normal = { textColor = textColor }
                };
            }
            if (_lineTex == null)
            {
                _lineTex = new Texture2D(1, 1);
                _lineTex.SetPixel(0, 0, Color.white);
                _lineTex.Apply();
            }

            string baseText =
                $"FPS: {_fps:0.0}\n" +
                $"Active: {chunkManager.ActiveCount}  Pending: {chunkManager.PendingCount}  Spawned: {chunkManager.SpawnedLastFrame}\n" +
                $"IntegrationQ: {chunkManager.IntegrationQueueCount}  Integrated: {chunkManager.IntegrationsLastFrame}\n" +
                $"ChunkSize: {chunkManager.ChunkSize}  Columns: {chunkManager.ColumnChunks}  Radius: {chunkManager.LoadRadius}\n" +
                $"Gen: {chunkManager.LastGenMs} ms  Mesh: {chunkManager.LastMeshMs} ms  Total: {chunkManager.LastTotalMs} ms\n" +
                $"Last: {chunkManager.LastSpawnCoord}";

            GUI.Label(new Rect(offset.x, offset.y, 520f, 140f), baseText, _style);

            if (_mode == HudMode.Compact) return;

            float startY = offset.y + 140f;
            DrawGraph(new Rect(offset.x, startY, 520f, 80f), _cpuMs, "CPU ms", Color.green);
            DrawGraph(new Rect(offset.x, startY + 90f, 520f, 80f), _gpuMs, "GPU ms", Color.cyan, allowNA: true);
            DrawGraph(new Rect(offset.x, startY + 180f, 520f, 80f), _vramMb, "VRAM MB", Color.yellow);
            DrawGraph(new Rect(offset.x, startY + 270f, 520f, 80f), _ramMb, "RAM MB", Color.magenta);

            if (GUI.Button(new Rect(offset.x, startY + 360f, 140f, 24f), "Save CSV"))
            {
                SaveCsv();
            }
        }

        void DrawGraph(Rect rect, float[] data, string label, Color color, bool allowNA = false)
        {
            GUI.Label(new Rect(rect.x, rect.y - 18f, rect.width, 18f), label, _style);
            GUI.Box(rect, GUIContent.none);

            float max = 0.1f;
            for (int i = 0; i < data.Length; i++)
            {
                float v = data[i];
                if (allowNA && v < 0) continue;
                if (v > max) max = v;
            }

            float barW = rect.width / data.Length;
            for (int i = 0; i < data.Length; i++)
            {
                float v = data[(i + _sampleIndex) % data.Length];
                if (allowNA && v < 0) continue;
                float h = Mathf.Clamp01(v / max) * rect.height;
                Rect bar = new Rect(rect.x + i * barW, rect.y + rect.height - h, barW, h);
                GUI.color = color;
                GUI.DrawTexture(bar, _lineTex);
            }
            GUI.color = Color.white;
        }

        void SaveCsv()
        {
            string path = Path.Combine(Application.persistentDataPath, $"voxel_profiler_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            using var sw = new StreamWriter(path);
            sw.WriteLine("time,cpu_ms,gpu_ms,vram_mb,ram_mb,active,pending,spawned");
            int count = Mathf.Min(_sampleCount, graphSamples);
            int start = (_sampleIndex - count + graphSamples) % graphSamples;
            for (int i = 0; i < count; i++)
            {
                int idx = (start + i) % graphSamples;
                sw.WriteLine(
                    $"{Fmt(_times[idx])}," +
                    $"{Fmt(_cpuMs[idx])}," +
                    $"{Fmt(_gpuMs[idx])}," +
                    $"{Fmt(_vramMb[idx])}," +
                    $"{Fmt(_ramMb[idx])}," +
                    $"{_active[idx]}," +
                    $"{_pending[idx]}," +
                    $"{_spawned[idx]}");
            }
            Debug.Log($"[VoxelHUD] Saved CSV: {path}");
        }

        static string Fmt(float v) => v.ToString("0.000", CultureInfo.InvariantCulture);

        void WriteSummaryToFile(float avgCpu, float avgGpu, float avgVram, float avgRam, float avgActive, float avgPending, float avgSpawned)
        {
            if (string.IsNullOrWhiteSpace(summaryFileName)) return;
            if (!_summaryAccepting) return;

            if (string.IsNullOrEmpty(_summaryPath))
            {
                string dir = Path.Combine(Application.persistentDataPath, "Logs");
                _summaryPath = Path.Combine(dir, summaryFileName);
            }

            long gen = chunkManager != null ? chunkManager.LastGenMs : 0;
            long mesh = chunkManager != null ? chunkManager.LastMeshMs : 0;
            long total = chunkManager != null ? chunkManager.LastTotalMs : 0;

            string line =
                $"{DateTime.Now:O}," +
                $"{Fmt(avgCpu)}," +
                $"{Fmt(avgGpu)}," +
                $"{Fmt(avgVram)}," +
                $"{Fmt(avgRam)}," +
                $"{avgActive:0}," +
                $"{avgPending:0}," +
                $"{avgSpawned:0}," +
                $"{gen}," +
                $"{mesh}," +
                $"{total}\n";

            EnsureSummaryWorker();
            var request = new SummaryWriteRequest
            {
                Path = _summaryPath,
                Line = line,
                EnsureHeader = !_summaryHeaderWritten
            };

            if (_summaryWorker != null)
            {
                _summaryQueue.Enqueue(request);
                _summarySignal.Set();
            }
            else
            {
                WriteSummaryRequest(request);
            }
        }

        void EnsureSummaryWorker()
        {
            if (_summaryWorker != null || !writeSummaryToFile) return;
            StartSummaryWorker();
        }

        void StartSummaryWorker()
        {
            if (_summaryWorker != null) return;
            _summaryAccepting = true;
            _summaryStop = false;
            _summarySignal = new AutoResetEvent(false);
            _summaryWorker = new Thread(SummaryWorkerLoop)
            {
                IsBackground = true,
                Name = "VoxelSummaryWriter"
            };
            _summaryWorker.Start();
        }

        void StopSummaryWorker(bool flush)
        {
            _summaryAccepting = false;
            _summaryStop = true;
            _summarySignal?.Set();
            if (_summaryWorker != null)
            {
                _summaryWorker.Join();
                _summaryWorker = null;
            }
            _summarySignal?.Dispose();
            _summarySignal = null;

            if (flush)
                FlushSummaryOnMainThread();
        }

        void SummaryWorkerLoop()
        {
            while (true)
            {
                _summarySignal.WaitOne();
                while (_summaryQueue.TryDequeue(out var request))
                    WriteSummaryRequest(request);

                if (_summaryStop && _summaryQueue.IsEmpty)
                    return;
            }
        }

        void FlushSummaryOnMainThread()
        {
            while (_summaryQueue.TryDequeue(out var request))
                WriteSummaryRequest(request);
        }

        void WriteSummaryRequest(SummaryWriteRequest request)
        {
            try
            {
                string dir = Path.GetDirectoryName(request.Path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                if (request.EnsureHeader && !_summaryHeaderWritten && !File.Exists(request.Path))
                {
                    File.AppendAllText(request.Path, "time,cpu_ms,gpu_ms,vram_mb,ram_mb,active,pending,spawned,gen_ms,mesh_ms,total_ms\n");
                    _summaryHeaderWritten = true;
                }
                else if (request.EnsureHeader && !_summaryHeaderWritten)
                {
                    _summaryHeaderWritten = true;
                }

                File.AppendAllText(request.Path, request.Line);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VoxelHUD] Summary write failed: {ex.Message}");
            }
        }

        struct SummaryWriteRequest
        {
            public string Path;
            public string Line;
            public bool EnsureHeader;
        }
    }
}

