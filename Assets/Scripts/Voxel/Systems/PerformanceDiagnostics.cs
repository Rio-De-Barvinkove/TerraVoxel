using System.Text;
using UnityEngine;

namespace TerraVoxel.Voxel.Systems
{
    /// <summary>
    /// Per-frame performance breakdown for streaming. Populated by ChunkManager when enabled.
    /// Use with Unity Profiler markers for hierarchy; this provides numeric values for HUD/logging.
    /// </summary>
    public static class PerformanceDiagnostics
    {
        public struct FrameBreakdown
        {
            public int ActiveChunks;
            public int PendingCount;
            public int GpuSlots;
            public int ColliderQueueSize;
            public long MaintainRadiusMs;
            public long GenMs;
            public long MeshMs;
            public long LodMs;
            public long OcclusionMs;
            public long GpuCullMs;
            public float CpuFrameMs;
            public float GpuFrameMs;
            public long VramMb;
            public long RamMb;
        }

        static FrameBreakdown _last;

        public static FrameBreakdown Last => _last;

        public static void Record(int activeChunks, int pendingCount, int gpuSlots, int colliderQueueSize,
            long maintainRadiusMs, long genMs, long meshMs, long lodMs, long occlusionMs, long gpuCullMs,
            float cpuFrameMs, float gpuFrameMs, long vramMb, long ramMb)
        {
            _last = new FrameBreakdown
            {
                ActiveChunks = activeChunks,
                PendingCount = pendingCount,
                GpuSlots = gpuSlots,
                ColliderQueueSize = colliderQueueSize,
                MaintainRadiusMs = maintainRadiusMs,
                GenMs = genMs,
                MeshMs = meshMs,
                LodMs = lodMs,
                OcclusionMs = occlusionMs,
                GpuCullMs = gpuCullMs,
                CpuFrameMs = cpuFrameMs,
                GpuFrameMs = gpuFrameMs,
                VramMb = vramMb,
                RamMb = ramMb
            };
        }

        public static string GetBreakdownString()
        {
            var b = _last;
            var sb = new StringBuilder();
            sb.AppendLine($"Active:{b.ActiveChunks} Pending:{b.PendingCount} GpuSlots:{b.GpuSlots} ColliderQ:{b.ColliderQueueSize}");
            sb.AppendLine($"MaintainRadius:{b.MaintainRadiusMs}ms Gen:{b.GenMs}ms Mesh:{b.MeshMs}ms LOD:{b.LodMs}ms Occlusion:{b.OcclusionMs}ms GpuCull:{b.GpuCullMs}ms");
            sb.AppendLine($"CPU:{b.CpuFrameMs:F1}ms GPU:{b.GpuFrameMs:F1}ms VRAM:{b.VramMb}MB RAM:{b.RamMb}MB");
            return sb.ToString();
        }
    }
}
