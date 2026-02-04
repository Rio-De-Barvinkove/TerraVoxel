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

        public override void Create()
        {
            _pass = null;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (gpuDrivenRenderer == null)
                gpuDrivenRenderer = Object.FindAnyObjectByType<GpuDrivenRenderer>();
            if (gpuDrivenRenderer == null)
                return;
            if (_pass == null)
                _pass = new GpuDrivenRenderPass(gpuDrivenRenderer);
            renderer.EnqueuePass(_pass);
        }
    }
}
