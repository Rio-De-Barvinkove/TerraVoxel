/*
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
        public GpuDrivenRenderer Renderer => _renderer;

        class PassData
        {
            internal GpuDrivenRenderer renderer;
            internal Camera camera;
        }

        public GpuDrivenRenderPass(GpuDrivenRenderer renderer)
        {
            _renderer = renderer;
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_renderer == null || !_renderer.HasDrawData) return;

            UniversalResourceData resourcesData = frameData.Get<UniversalResourceData>();
            if (!resourcesData.activeColorTexture.IsValid())
                return;

            var cameraData = frameData.Get<UniversalCameraData>();
            var cam = cameraData?.camera;

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(ProfilerTag, out var passData))
            {
                passData.renderer = _renderer;
                passData.camera = cam;
                builder.SetRenderAttachment(resourcesData.activeColorTexture, 0, AccessFlags.Write);
                if (resourcesData.activeDepthTexture.IsValid())
                    builder.SetRenderAttachmentDepth(resourcesData.activeDepthTexture, AccessFlags.ReadWrite);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    if (data.renderer == null) return;
                    if (data.camera != null)
                        data.renderer.SetBoundsFromCamera(data.camera);
                    data.renderer.RecordDrawToCommandBuffer(context.cmd);
                });
            }
        }

#pragma warning disable 0618 // Obsolete Execute; migration to RenderGraph planned
        [Obsolete("Overrides obsolete ScriptableRenderPass.Execute; use RenderGraph API when migrating.")]
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (_renderer == null || !_renderer.HasDrawData) return;

            var cam = renderingData.cameraData.camera;
            if (cam != null)
                _renderer.SetBoundsFromCamera(cam);

            CommandBuffer cmd = CommandBufferPool.Get(ProfilerTag);
            if (_renderer.RecordDrawToCommandBuffer(cmd))
                context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
#pragma warning restore 0618

        public override void OnCameraCleanup(CommandBuffer cmd) { }
    }
}
*/

using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace TerraVoxel.Voxel.GPU
{
    public class GpuDrivenRenderPass : ScriptableRenderPass
    {
        readonly GpuDrivenRenderer _renderer;
        public GpuDrivenRenderer Renderer => _renderer;

        public GpuDrivenRenderPass(GpuDrivenRenderer renderer)
        {
            _renderer = renderer;
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_renderer == null || !_renderer.HasDrawData) return;
            var resourcesData = frameData.Get<UniversalResourceData>();
            if (!resourcesData.activeColorTexture.IsValid()) return;
            var cameraData = frameData.Get<UniversalCameraData>();
            var cam = cameraData?.camera;
            const string ProfilerTag = "GpuDrivenVoxels";
            using (var builder = renderGraph.AddRasterRenderPass<object>(ProfilerTag, out _))
            {
                builder.SetRenderAttachment(resourcesData.activeColorTexture, 0, AccessFlags.Write);
                if (resourcesData.activeDepthTexture.IsValid())
                    builder.SetRenderAttachmentDepth(resourcesData.activeDepthTexture, AccessFlags.ReadWrite);
                builder.SetRenderFunc((object _, RasterGraphContext ctx) =>
                {
                    if (_renderer != null && cam != null)
                    {
                        _renderer.SetBoundsFromCamera(cam);
                        _renderer.RecordDrawToCommandBuffer(ctx.cmd);
                    }
                });
            }
        }

#pragma warning disable 0618
        [Obsolete("Overrides obsolete ScriptableRenderPass.Execute")]
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (_renderer == null || !_renderer.HasDrawData) return;
            var cam = renderingData.cameraData.camera;
            if (cam != null) _renderer.SetBoundsFromCamera(cam);
            var cmd = CommandBufferPool.Get("GpuDrivenVoxels");
            if (_renderer.RecordDrawToCommandBuffer(cmd))
                context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
#pragma warning restore 0618

        public override void OnCameraCleanup(CommandBuffer cmd) { }
    }
}