using System;
using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace TerraVoxel.Voxel.GPU
{
    /// <summary>
    /// Manages async GPU readback for save/debug. Only aggregates and metadata; never full buffers every frame.
    /// </summary>
    public sealed class GpuReadbackManager
    {
        readonly Queue<AsyncGPUReadbackRequest> _pending = new Queue<AsyncGPUReadbackRequest>();

        /// <summary>Request visible chunk count (1 uint). Callback receives the count.</summary>
        public void RequestVisibleCount(ComputeBuffer visibleCountBuffer, Action<int> callback)
        {
            if (visibleCountBuffer == null || callback == null) return;
            var request = AsyncGPUReadback.Request(visibleCountBuffer, (req) =>
            {
                if (req.hasError) return;
                var data = req.GetData<uint>();
                if (data.Length > 0)
                    callback((int)data[0]);
            });
            _pending.Enqueue(request);
        }

        /// <summary>Request chunk voxel data for one slot (for save). Callback receives ushort[] of size voxelsPerChunk; GPU buffer is uint so we cast low 16 bits.</summary>
        public void RequestChunkVoxels(GpuWorldState state, int slot, Action<ushort[]> callback)
        {
            if (state == null || callback == null || slot < 0 || slot >= state.MaxChunks) return;
            int voxelOffset = state.GetVoxelOffset(slot);
            int count = state.VoxelsPerChunk;
            ComputeBuffer voxelBuffer = state.VoxelMaterialBuffer;
            var request = AsyncGPUReadback.Request(voxelBuffer, count * sizeof(uint), voxelOffset * sizeof(uint), (req) =>
            {
                if (req.hasError) return;
                var data = req.GetData<uint>();
                var materials = new ushort[count];
                for (int i = 0; i < count && i < data.Length; i++)
                    materials[i] = (ushort)(data[i] & 0xFFFF);
                callback(materials);
            });
            _pending.Enqueue(request);
        }

        /// <summary>Request single chunk descriptor flags (from CPU staging; use RequestAllDescriptorFlags for GPU-updated flags).</summary>
        public void RequestChunkFlags(GpuWorldState state, int slot, Action<uint> callback)
        {
            if (state == null || callback == null || slot < 0 || slot >= state.MaxChunks) return;
            var desc = state.GetDescriptor(slot);
            callback(desc.Flags);
        }

        /// <summary>Request all descriptor flags from GPU (after analysis). Callback receives slot -> flags. Flags at byte 28 per 32-byte descriptor.</summary>
        public void RequestAllDescriptorFlags(GpuWorldState state, Action<Dictionary<int, uint>> callback)
        {
            if (state == null || callback == null || state.ChunkDescriptors == null) return;
            int maxChunks = state.MaxChunks;
            int stride = 32;
            int totalBytes = maxChunks * stride;
            var request = AsyncGPUReadback.Request(state.ChunkDescriptors, totalBytes, 0, (req) =>
            {
                if (req.hasError) return;
                var bytes = req.GetData<byte>();
                var result = new Dictionary<int, uint>(maxChunks);
                for (int i = 0; i < maxChunks; i++)
                {
                    int off = i * stride + 28;
                    if (off + 4 <= bytes.Length)
                    {
                        uint flags = (uint)bytes[off] | ((uint)bytes[off + 1] << 8) | ((uint)bytes[off + 2] << 16) | ((uint)bytes[off + 3] << 24);
                        result[i] = flags;
                    }
                }
                callback(result);
            });
            _pending.Enqueue(request);
        }

        /// <summary>Process completed requests. Call once per frame.</summary>
        public void Update()
        {
            while (_pending.Count > 0 && _pending.Peek().done)
                _pending.Dequeue();
        }
    }
}
