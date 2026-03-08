/*
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace TerraVoxel.Voxel.GPU
{
    /// <summary>
    /// URP Renderer Feature: draws GPU-driven voxel chunks via CommandBuffer in SRP context.
    /// Assign GpuDrivenRenderer in Inspector if the field accepts scene references; otherwise the feature finds GpuDrivenRenderer in the scene automatically.
    /// On GpuDrivenRenderer, enable "Draw Via Render Feature" so the draw runs here (fixes DX12 SRV binding).
    /// </summary>
    public class GpuDrivenRenderFeature : ScriptableRendererFeature
    {
        [Tooltip("Assign if Inspector allows (drag ChunkManager from Hierarchy). If empty, the feature finds GpuDrivenRenderer in the scene at runtime.")]
        [SerializeField] GpuDrivenRenderer gpuDrivenRenderer;

        GpuDrivenRenderPass _pass;
        GpuDrivenRenderer _cachedRenderer;

        public override void Create()
        {
            _pass = null;
            _cachedRenderer = null;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (gpuDrivenRenderer != null)
                _cachedRenderer = gpuDrivenRenderer;
            if (_cachedRenderer == null)
            {
#if UNITY_2023_1_OR_NEWER
                _cachedRenderer = Object.FindAnyObjectByType<GpuDrivenRenderer>();
#else
                _cachedRenderer = Object.FindObjectOfType<GpuDrivenRenderer>();
#endif
            }
            if (_cachedRenderer == null)
                return;
            if (_pass == null || _pass.Renderer != _cachedRenderer)
                _pass = new GpuDrivenRenderPass(_cachedRenderer);
            renderer.EnqueuePass(_pass);
        }
    }
}
*/

using TerraVoxel.Voxel.Streaming;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace TerraVoxel.Voxel.GPU
{
    public class GpuDrivenRenderFeature : ScriptableRendererFeature
    {
        [SerializeField] GpuDrivenRenderer gpuDrivenRenderer;
        GpuDrivenRenderPass _pass;
        GpuDrivenRenderer _cachedRenderer;

        public override void Create()
        {
            _pass = null;
            _cachedRenderer = null;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
#if UNITY_2023_1_OR_NEWER
            var chunkManager = Object.FindAnyObjectByType<ChunkManager>();
#else
            var chunkManager = Object.FindObjectOfType<ChunkManager>();
#endif
            if (chunkManager != null && !chunkManager.UseGpuPipeline)
                return;
            if (gpuDrivenRenderer != null) _cachedRenderer = gpuDrivenRenderer;
            if (_cachedRenderer == null)
            {
#if UNITY_2023_1_OR_NEWER
                _cachedRenderer = Object.FindAnyObjectByType<GpuDrivenRenderer>();
#else
                _cachedRenderer = Object.FindObjectOfType<GpuDrivenRenderer>();
#endif
            }
            if (_cachedRenderer == null) return;
            if (_pass == null || _pass.Renderer != _cachedRenderer)
                _pass = new GpuDrivenRenderPass(_cachedRenderer);
            renderer.EnqueuePass(_pass);
        }
    }
}