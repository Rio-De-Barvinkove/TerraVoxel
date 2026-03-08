/* CPU-only rollback: весь вміст закоментовано, залишено stub. 
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    internal sealed class ChunkAdaptiveLimitsManager
    {
        readonly ChunkManager.Context _ctx;

        internal ChunkAdaptiveLimitsManager(ChunkManager.Context ctx)
        {
            _ctx = ctx;
        }

        internal void InitAdaptiveLimits()
        {
            if (_ctx.AdaptiveInitialized) return;
            if (_ctx.ScaleJobsByProcessorCount)
            {
                int cores = Mathf.Max(1, SystemInfo.processorCount);
                // Heuristic: cores/2 caps parallel jobs; Clamp(2,16) avoids over/under on hyper-threaded or many-core CPUs.
                int perType = Mathf.Clamp(cores / 2, 2, 16);
                _ctx.BaseMaxGenJobsInFlight = perType;
                _ctx.BaseMaxMeshJobsInFlight = perType;
                _ctx.BaseMaxIntegrationsPerFrame = Mathf.Max(_ctx.MaxIntegrationsPerFrame, perType * 2);
            }
            else
            {
                _ctx.BaseMaxGenJobsInFlight = _ctx.MaxGenJobsInFlight;
                _ctx.BaseMaxMeshJobsInFlight = _ctx.MaxMeshJobsInFlight;
                _ctx.BaseMaxIntegrationsPerFrame = _ctx.MaxIntegrationsPerFrame;
            }
            _ctx.BaseMaxPreloadsPerFrame = _ctx.MaxPreloadsPerFrame;
            _ctx.RuntimeMaxGenJobsInFlight = _ctx.BaseMaxGenJobsInFlight;
            _ctx.RuntimeMaxMeshJobsInFlight = _ctx.BaseMaxMeshJobsInFlight;
            _ctx.RuntimeMaxIntegrationsPerFrame = _ctx.BaseMaxIntegrationsPerFrame;
            _ctx.RuntimeMaxPreloadsPerFrame = _ctx.BaseMaxPreloadsPerFrame;
            _ctx.AdaptiveInitialized = true;
        }

        /// <summary>Resets limits to base each frame; reduces them if over gen/mesh/integration/memory/GPU threshold. Limits recover when not throttled (cooldown expires).</summary>
        internal void UpdateAdaptiveLimits()
        {
            if (!_ctx.EnableAdaptiveLimits)
            {
                _ctx.RuntimeMaxGenJobsInFlight = _ctx.MaxGenJobsInFlight;
                _ctx.RuntimeMaxMeshJobsInFlight = _ctx.MaxMeshJobsInFlight;
                _ctx.RuntimeMaxIntegrationsPerFrame = _ctx.MaxIntegrationsPerFrame;
                _ctx.RuntimeMaxPreloadsPerFrame = _ctx.MaxPreloadsPerFrame;
                return;
            }

            InitAdaptiveLimits();
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < _ctx.AdaptiveUntil)
                return;

            _ctx.RuntimeMaxGenJobsInFlight = _ctx.BaseMaxGenJobsInFlight;
            _ctx.RuntimeMaxMeshJobsInFlight = _ctx.BaseMaxMeshJobsInFlight;
            _ctx.RuntimeMaxIntegrationsPerFrame = _ctx.BaseMaxIntegrationsPerFrame;
            _ctx.RuntimeMaxPreloadsPerFrame = _ctx.BaseMaxPreloadsPerFrame;

            bool throttled = false;
            if (_ctx.GenSlowMs > 0 && _ctx.LastGenMs > _ctx.GenSlowMs)
            {
                _ctx.RuntimeMaxGenJobsInFlight = Mathf.Max(1, _ctx.BaseMaxGenJobsInFlight / 2);
                throttled = true;
            }
            if (_ctx.MeshSlowMs > 0 && _ctx.LastMeshMs > _ctx.MeshSlowMs)
            {
                _ctx.RuntimeMaxMeshJobsInFlight = Mathf.Max(1, _ctx.BaseMaxMeshJobsInFlight / 2);
                throttled = true;
            }
            if (_ctx.IntegrationSlowMs > 0 && _ctx.LastIntegrationMs > _ctx.IntegrationSlowMs)
            {
                _ctx.RuntimeMaxIntegrationsPerFrame = Mathf.Max(1, _ctx.BaseMaxIntegrationsPerFrame / 2);
                _ctx.RuntimeMaxPreloadsPerFrame = 0;
                throttled = true;
            }

            if (_ctx.MemoryPressureThresholdMb > 0)
            {
#if UNITY_EDITOR
                long memMb = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024);
                if (memMb > _ctx.MemoryPressureThresholdMb)
                {
                    _ctx.RuntimeMaxGenJobsInFlight = Mathf.Max(1, _ctx.BaseMaxGenJobsInFlight / 2);
                    _ctx.RuntimeMaxMeshJobsInFlight = Mathf.Max(1, _ctx.BaseMaxMeshJobsInFlight / 2);
                    _ctx.RuntimeMaxIntegrationsPerFrame = Mathf.Max(1, _ctx.BaseMaxIntegrationsPerFrame / 2);
                    throttled = true;
                }
#endif
            }

            // SystemInfo.graphicsMemorySize is total VRAM (MB), not used; Unity has no API for used VRAM. Throttle when total < threshold (low-end device).
            if (_ctx.GraphicsMemoryThresholdMb > 0 && SystemInfo.graphicsMemorySize > 0)
            {
                long gpuMb = SystemInfo.graphicsMemorySize;
                if (gpuMb < _ctx.GraphicsMemoryThresholdMb)
                {
                    _ctx.RuntimeMaxMeshJobsInFlight = Mathf.Max(1, _ctx.BaseMaxMeshJobsInFlight / 2);
                    _ctx.RuntimeMaxIntegrationsPerFrame = Mathf.Max(1, _ctx.BaseMaxIntegrationsPerFrame / 2);
                    throttled = true;
                }
            }

            if (throttled && _ctx.AdaptiveCooldown > 0f)
                _ctx.AdaptiveUntil = now + _ctx.AdaptiveCooldown;
            // Limits recover next frame: base values are reapplied at start of UpdateAdaptiveLimits, then reduced only if over threshold.
        }
    }
}

*/