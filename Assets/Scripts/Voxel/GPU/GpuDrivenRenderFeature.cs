using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace TerraVoxel.Voxel.GPU
{
    /// <summary>
    /// URP Renderer Feature: draws GPU-driven voxel chunks via CommandBuffer in SRP context.
    /// Add to your URP Renderer (e.g. Universal Renderer). Assign GpuDrivenRenderer reference.
    /// On GpuDrivenRenderer, enable "Draw Via Render Feature" so the draw runs here (fixes DX12 SRV binding).
    /// </summary>
    public class GpuDrivenRenderFeature : ScriptableRendererFeature
    {
        [SerializeField] GpuDrivenRenderer gpuDrivenRenderer;

        GpuDrivenRenderPass _pass;

        public override void Create()
        {
            if (gpuDrivenRenderer != null)
                _pass = new GpuDrivenRenderPass(gpuDrivenRenderer);
            else
                _pass = null;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (gpuDrivenRenderer == null || _pass == null) return;
            renderer.EnqueuePass(_pass);
        }
    }
}
