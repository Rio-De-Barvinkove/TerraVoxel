/*
using UnityEngine;

namespace TerraVoxel.Voxel.GPU
{
    /// <summary>
    /// Dispatches GPU chunk analysis (empty/solid/mixed flags) via ChunkAnalysis.compute.
    /// Full chunk coverage: CountVoxels + ResolveFlags (global atomics).
    /// </summary>
    public sealed class GpuChunkAnalyzer
    {
        ComputeShader _shader;
        int _kernelClearCounts;
        int _kernelAnalyzeChunkCount;
        int _kernelAnalyzeChunkFlags;
        ComputeBuffer _solidCount;
        ComputeBuffer _airCount;
        const string KernelClearCounts = "ClearCounts";
        const string KernelAnalyzeChunkCount = "AnalyzeChunkCount";
        const string KernelAnalyzeChunkFlags = "AnalyzeChunkFlags";
        const int ThreadsPerGroup = 512;
        const int MaxGroupsPerDispatch = 65535;

        public bool IsValid => _shader != null && _kernelClearCounts >= 0 && _kernelAnalyzeChunkCount >= 0 && _kernelAnalyzeChunkFlags >= 0;

        public void Initialize(ComputeShader shader)
        {
            _shader = shader;
            if (_shader == null) return;
            _kernelClearCounts = _shader.FindKernel(KernelClearCounts);
            _kernelAnalyzeChunkCount = _shader.FindKernel(KernelAnalyzeChunkCount);
            _kernelAnalyzeChunkFlags = _shader.FindKernel(KernelAnalyzeChunkFlags);
            if (_kernelClearCounts < 0 || _kernelAnalyzeChunkCount < 0 || _kernelAnalyzeChunkFlags < 0)
                Debug.LogWarning("[GpuChunkAnalyzer] ChunkAnalysis.compute kernel not found. ClearCounts=" + _kernelClearCounts + " AnalyzeChunkCount=" + _kernelAnalyzeChunkCount + " AnalyzeChunkFlags=" + _kernelAnalyzeChunkFlags + ". Check that the compute shader compiles and has these kernels.");
        }

        void EnsureCountBuffers(int maxChunks)
        {
            if (_solidCount != null && _solidCount.count >= maxChunks) return;
            _solidCount?.Release();
            _airCount?.Release();
            _solidCount = new ComputeBuffer(Mathf.Max(1, maxChunks), sizeof(uint));
            _airCount = new ComputeBuffer(Mathf.Max(1, maxChunks), sizeof(uint));
        }

        /// <summary>Run analysis for active slots only. Updates ChunkDescriptors.Flags (empty/solid/mixed). Call state.UpdateActiveSlotIndicesBuffer() before this.</summary>
        public void ScheduleAnalysis(GpuWorldState state)
        {
            if (!IsValid || state == null) return;
            int activeCount = state.ChunkCount;
            if (activeCount <= 0) return;
            if (state.ActiveSlotIndicesBuffer == null) return;

            state.UpdateActiveSlotIndicesBuffer();

            int chunkSize = state.ChunkSize;
            int voxelsPerChunk = state.VoxelsPerChunk;
            int maxChunks = state.MaxChunks;

            EnsureCountBuffers(maxChunks);

            _shader.SetBuffer(_kernelClearCounts, "SolidCount", _solidCount);
            _shader.SetBuffer(_kernelClearCounts, "AirCount", _airCount);
            _shader.SetBuffer(_kernelClearCounts, "ActiveSlotIndices", state.ActiveSlotIndicesBuffer);
            _shader.SetInt("ActiveCount_", activeCount);
            int groupsClear = Mathf.CeilToInt(activeCount / 64f);
            _shader.Dispatch(_kernelClearCounts, Mathf.Max(1, groupsClear), 1, 1);

            _shader.SetBuffer(_kernelAnalyzeChunkCount, "VoxelMaterialBuffer", state.VoxelMaterialBuffer);
            _shader.SetBuffer(_kernelAnalyzeChunkCount, "SolidCount", _solidCount);
            _shader.SetBuffer(_kernelAnalyzeChunkCount, "AirCount", _airCount);
            _shader.SetBuffer(_kernelAnalyzeChunkCount, "ActiveSlotIndices", state.ActiveSlotIndicesBuffer);
            _shader.SetInt("ChunkSize_", chunkSize);
            _shader.SetInt("VoxelsPerChunk_", voxelsPerChunk);
            _shader.SetInt("ActiveCount_", activeCount);

            int totalVoxels = activeCount * voxelsPerChunk;
            for (int voxelStart = 0; voxelStart < totalVoxels; voxelStart += MaxGroupsPerDispatch * ThreadsPerGroup)
            {
                int remaining = totalVoxels - voxelStart;
                int groupsThisBatch = Mathf.Min(MaxGroupsPerDispatch, Mathf.CeilToInt((float)remaining / ThreadsPerGroup));
                if (groupsThisBatch <= 0) break;
                int vStart = voxelStart;
                _shader.SetInt("VoxelStart_", vStart);
                _shader.Dispatch(_kernelAnalyzeChunkCount, groupsThisBatch, 1, 1);
            }

            _shader.SetBuffer(_kernelAnalyzeChunkFlags, "ChunkDescriptors", state.ChunkDescriptors);
            _shader.SetBuffer(_kernelAnalyzeChunkFlags, "SolidCount", _solidCount);
            _shader.SetBuffer(_kernelAnalyzeChunkFlags, "AirCount", _airCount);
            _shader.SetBuffer(_kernelAnalyzeChunkFlags, "ActiveSlotIndices", state.ActiveSlotIndicesBuffer);
            if (state.ExpectedGenerationBuffer != null)
                _shader.SetBuffer(_kernelAnalyzeChunkFlags, "ExpectedGeneration", state.ExpectedGenerationBuffer);
            _shader.SetInt("ActiveCount_", activeCount);

            int groupsFlags = Mathf.CeilToInt(activeCount / 64f);
            _shader.Dispatch(_kernelAnalyzeChunkFlags, Mathf.Max(1, groupsFlags), 1, 1);
        }

        public void Dispose()
        {
            _solidCount?.Release();
            _solidCount = null;
            _airCount?.Release();
            _airCount = null;
        }
    }
}
*/

using UnityEngine;

namespace TerraVoxel.Voxel.GPU
{
    public sealed class GpuChunkAnalyzer
    {
        public bool IsValid => false;
        public void Initialize(UnityEngine.ComputeShader shader) { }
        public void ScheduleAnalysis(GpuWorldState state) { }
        public void Dispose() { }
    }
}
