using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace TerraVoxel.Voxel.GPU
{
    /// <summary>
    /// URP pass that records GpuDrivenRenderer draw into the SRP command buffer.
    /// Fixes DX12 "buffer not provided" by drawing in SRP context.
    /// </summary>
    public class GpuDrivenRenderPass : ScriptableRenderPass
    {
        const string ProfilerTag = "GpuDrivenVoxels";

        readonly GpuDrivenRenderer _renderer;

        public GpuDrivenRenderPass(GpuDrivenRenderer renderer)
        {
            _renderer = renderer;
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
        }

#pragma warning disable 0618 // Obsolete Execute; migration to RenderGraph planned
        [Obsolete("Overrides obsolete ScriptableRenderPass.Execute; use RenderGraph API when migrating.")]
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (_renderer == null) return;

            CommandBuffer cmd = CommandBufferPool.Get(ProfilerTag);
            if (_renderer.RecordDrawToCommandBuffer(cmd))
                context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
#pragma warning restore 0618

        public override void OnCameraCleanup(CommandBuffer cmd) { }
    }
}
