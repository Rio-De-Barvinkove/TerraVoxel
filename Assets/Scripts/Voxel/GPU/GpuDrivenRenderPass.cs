using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace TerraVoxel.Voxel.GPU
{
    /// <summary>
    /// URP pass that records GpuDrivenRenderer draw into the SRP command buffer.
    /// Fixes DX12 "buffer not provided" by drawing in SRP context.
    /// Implements RecordRenderGraph so the pass runs when RenderGraph is enabled.
    /// </summary>
    public class GpuDrivenRenderPass : ScriptableRenderPass
    {
        const string ProfilerTag = "GpuDrivenVoxels";

        readonly GpuDrivenRenderer _renderer;

        class PassData
        {
            internal GpuDrivenRenderer renderer;
        }

        public GpuDrivenRenderPass(GpuDrivenRenderer renderer)
        {
            _renderer = renderer;
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_renderer == null) return;

            UniversalResourceData resourcesData = frameData.Get<UniversalResourceData>();
            if (!resourcesData.activeColorTexture.IsValid())
                return;

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(ProfilerTag, out var passData))
            {
                passData.renderer = _renderer;
                builder.SetRenderAttachment(resourcesData.activeColorTexture, 0, AccessFlags.Write);
                if (resourcesData.activeDepthTexture.IsValid())
                    builder.SetRenderAttachmentDepth(resourcesData.activeDepthTexture, AccessFlags.ReadWrite);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    if (data.renderer != null)
                        data.renderer.RecordDrawToCommandBuffer(context.cmd);
                });
            }
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
