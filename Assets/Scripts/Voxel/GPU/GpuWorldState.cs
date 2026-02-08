using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using UnityEngine;

namespace TerraVoxel.Voxel.GPU
{
    /// <summary>
    /// GPU World State: central buffers for voxels, chunk descriptors, mesh data, visibility, and draw args.
    /// CPU knows only offsets and slot indices; geometry lives on GPU. Use GpuSlotAllocator for allocate/free.
    /// </summary>
    public sealed class GpuWorldState
    {
        readonly int _maxChunks;
        readonly int _chunkSize;
        readonly int _voxelsPerChunk;
        readonly int _maxVerticesPerChunk;
        readonly int _maxIndicesPerChunk;

        GpuSlotAllocator _allocator;
        readonly Dictionary<ChunkCoord, int> _coordToSlot = new Dictionary<ChunkCoord, int>();
        readonly Dictionary<int, ChunkCoord> _slotToCoord = new Dictionary<int, ChunkCoord>();
        readonly GpuChunkDescriptor[] _descriptorStaging;

        public ComputeBuffer VoxelMaterialBuffer { get; private set; }
        public ComputeBuffer ChunkDescriptors { get; private set; }
        public ComputeBuffer MeshVertexBuffer { get; private set; }
        public ComputeBuffer MeshNormalBuffer { get; private set; }
        public ComputeBuffer MeshIndexBuffer { get; private set; }
        public ComputeBuffer InstanceMatrices { get; private set; }
        public ComputeBuffer VisibilityFlags { get; private set; }
        public ComputeBuffer VisibleChunkIndices { get; private set; }
        public ComputeBuffer VisibleCountBuffer { get; private set; }
        public ComputeBuffer DrawArgsBuffer { get; private set; }
        /// <summary>Current generation per slot for use-after-free check in compute. Updated on Allocate/Free.</summary>
        public ComputeBuffer ExpectedGenerationBuffer { get; private set; }

        public GpuSlotAllocator Allocator => _allocator;
        public int MaxChunks => _maxChunks;
        public int ChunkSize => _chunkSize;
        public int VoxelsPerChunk => _voxelsPerChunk;
        public int MaxVerticesPerChunk => _maxVerticesPerChunk;
        public int MaxIndicesPerChunk => _maxIndicesPerChunk;
        public int ChunkCount => _coordToSlot.Count;

        /// <summary>Create GPU World State. Buffers are allocated; use AllocateChunk/FreeChunk for slots.</summary>
        /// <param name="maxChunks">Max chunk slots (e.g. 4096).</param>
        /// <param name="chunkSize">Voxels per axis (e.g. 32).</param>
        /// <param name="maxVerticesPerChunk">Max vertices per chunk mesh (e.g. 50000).</param>
        /// <param name="maxIndicesPerChunk">Max indices per chunk (e.g. 75000).</param>
        public GpuWorldState(int maxChunks, int chunkSize, int maxVerticesPerChunk = 50000, int maxIndicesPerChunk = 75000)
        {
            _maxChunks = Mathf.Max(1, maxChunks);
            _chunkSize = Mathf.Max(1, chunkSize);
            _voxelsPerChunk = _chunkSize * _chunkSize * _chunkSize;
            _maxVerticesPerChunk = Mathf.Max(1, maxVerticesPerChunk);
            _maxIndicesPerChunk = Mathf.Max(1, maxIndicesPerChunk);

            _allocator = new GpuSlotAllocator(_maxChunks);
            _descriptorStaging = new GpuChunkDescriptor[_maxChunks];

            int totalVoxels = _maxChunks * _voxelsPerChunk;
            int totalVertices = _maxChunks * _maxVerticesPerChunk;
            int totalIndices = _maxChunks * _maxIndicesPerChunk;

            // uint per voxel (HLSL RWStructuredBuffer<uint>); material in low 16 bits
            VoxelMaterialBuffer = new ComputeBuffer(totalVoxels, sizeof(uint));
            ChunkDescriptors = new ComputeBuffer(_maxChunks, GpuChunkDescriptor.StrideBytes);
            MeshVertexBuffer = new ComputeBuffer(totalVertices, 12); // float3
            MeshNormalBuffer = new ComputeBuffer(totalVertices, 12);
            MeshIndexBuffer = new ComputeBuffer(totalIndices, sizeof(uint));
            InstanceMatrices = new ComputeBuffer(_maxChunks, 64); // float4x4
            VisibilityFlags = new ComputeBuffer(_maxChunks, sizeof(uint));
            VisibleChunkIndices = new ComputeBuffer(_maxChunks, sizeof(uint));
            VisibleCountBuffer = new ComputeBuffer(1, sizeof(uint));
            DrawArgsBuffer = new ComputeBuffer(5, sizeof(uint), ComputeBufferType.IndirectArguments);
            ExpectedGenerationBuffer = new ComputeBuffer(_maxChunks, sizeof(uint));
            var genIds = new uint[_maxChunks];
            for (int i = 0; i < _maxChunks; i++)
                genIds[i] = _allocator.GetGeneration(i);
            ExpectedGenerationBuffer.SetData(genIds);

            ClearDescriptorStaging();
            ChunkDescriptors.SetData(_descriptorStaging);
        }

        void ClearDescriptorStaging()
        {
            for (int i = 0; i < _maxChunks; i++)
            {
                _descriptorStaging[i] = new GpuChunkDescriptor
                {
                    MeshOffset = GpuChunkDescriptor.MeshOffsetNone,
                    VertexCount = 0,
                    Flags = ChunkDescriptorFlags.Empty
                };
            }
        }

        /// <summary>Allocate a slot for chunk at coord. Returns slot index. Throws if full or coord already allocated.</summary>
        public int AllocateChunk(ChunkCoord coord)
        {
            if (_coordToSlot.ContainsKey(coord))
                throw new System.InvalidOperationException($"[GpuWorldState] Chunk {coord} already allocated");
            var (slot, generation) = _allocator.Allocate();
            _coordToSlot[coord] = slot;
            _slotToCoord[slot] = coord;

            uint voxelOffset = (uint)(slot * _voxelsPerChunk);
            uint meshOffset = GpuChunkDescriptor.MeshOffsetNone;
            uint vertexStride = (uint)_maxVerticesPerChunk;
            uint meshVertexOffset = (uint)(slot * _maxVerticesPerChunk);

            _descriptorStaging[slot] = new GpuChunkDescriptor
            {
                Coord = coord,
                SlotGeneration = generation,
                VoxelOffset = voxelOffset,
                MeshOffset = meshOffset,
                VertexCount = 0,
                Flags = 0
            };
            ChunkDescriptors.SetData(_descriptorStaging, slot, slot, 1);
            ExpectedGenerationBuffer.SetData(new[] { generation }, 0, slot, 1);

            return slot;
        }

        /// <summary>Free slot for chunk. Call after readback for save if needed. Increments generation.</summary>
        public void FreeChunk(ChunkCoord coord)
        {
            if (!_coordToSlot.TryGetValue(coord, out int slot))
                return;
            _coordToSlot.Remove(coord);
            _slotToCoord.Remove(slot);

            _descriptorStaging[slot] = new GpuChunkDescriptor
            {
                MeshOffset = GpuChunkDescriptor.MeshOffsetNone,
                VertexCount = 0,
                Flags = ChunkDescriptorFlags.Empty
            };
            ChunkDescriptors.SetData(_descriptorStaging, slot, slot, 1);
            _allocator.Free(slot);
            ExpectedGenerationBuffer.SetData(new[] { _allocator.GetGeneration(slot) }, 0, slot, 1);
        }

        public bool TryGetSlot(ChunkCoord coord, out int slot)
        {
            return _coordToSlot.TryGetValue(coord, out slot);
        }

        public bool TryGetCoord(int slot, out ChunkCoord coord)
        {
            return _slotToCoord.TryGetValue(slot, out coord);
        }

        /// <summary>Get descriptor for slot (from staging; for CPU read).</summary>
        public GpuChunkDescriptor GetDescriptor(int slot)
        {
            if (slot < 0 || slot >= _maxChunks) return default;
            return _descriptorStaging[slot];
        }

        /// <summary>Update descriptor staging and upload single slot to GPU. Only MeshOffset, VertexCount, Flags are written; Coord and SlotGeneration are preserved from AllocateChunk.</summary>
        public void UpdateDescriptor(int slot, uint meshOffset, uint vertexCount, uint flags)
        {
            if (slot < 0 || slot >= _maxChunks) return;
            ref var d = ref _descriptorStaging[slot];
            d.MeshOffset = meshOffset;
            d.VertexCount = vertexCount;
            d.Flags = flags;
#if UNITY_EDITOR
            ValidateDescriptor(slot, ref d);
#endif
            ChunkDescriptors.SetData(_descriptorStaging, slot, slot, 1);
        }

#if UNITY_EDITOR
        static void ValidateDescriptor(int slot, ref GpuChunkDescriptor d)
        {
            if (d.VertexCount == 0) return;
            // SlotGeneration 0 is valid: allocator initializes all slots to 0; generation increments only on Free.
            // Chunk at world origin (0,0,0) can be in any slot — no warning for coord (0,0,0).
        }
#endif

        /// <summary>Set descriptor flags only (e.g. after analysis).</summary>
        public void SetDescriptorFlags(int slot, uint flags)
        {
            if (slot < 0 || slot >= _maxChunks) return;
            ref var d = ref _descriptorStaging[slot];
            d.Flags = flags;
            ChunkDescriptors.SetData(_descriptorStaging, slot, slot, 1);
        }

        /// <summary>Voxel offset in VoxelMaterialBuffer for slot (element index).</summary>
        public int GetVoxelOffset(int slot) => slot * _voxelsPerChunk;

        /// <summary>Mesh vertex offset in MeshVertexBuffer for slot (vertex index).</summary>
        public int GetMeshVertexOffset(int slot) => slot * _maxVerticesPerChunk;

        /// <summary>Mesh index offset in MeshIndexBuffer for slot (index count).</summary>
        public int GetMeshIndexOffset(int slot) => slot * _maxIndicesPerChunk;

        /// <summary>Upload CPU voxel data to GPU at slot (for load). Materials are stored as uint (low 16 bits).</summary>
        public void SetVoxels(int slot, ushort[] materials)
        {
            if (materials == null || materials.Length != _voxelsPerChunk || slot < 0 || slot >= _maxChunks) return;
            uint[] u = new uint[_voxelsPerChunk];
            for (int i = 0; i < _voxelsPerChunk; i++)
                u[i] = materials[i];
            int offset = slot * _voxelsPerChunk;
            VoxelMaterialBuffer.SetData(u, 0, offset, _voxelsPerChunk);
        }

        /// <summary>Block until GPU has finished writing to this slot's voxel region (e.g. after ScheduleGeneration). Call before MeshChunk when voxels were generated on GPU.</summary>
        public void SyncVoxelSlot(int slot)
        {
            if (slot < 0 || slot >= _maxChunks || VoxelMaterialBuffer == null) return;
            int offset = slot * _voxelsPerChunk;
            VoxelMaterialBuffer.GetData(_syncVoxelOne, 0, offset, 1);
        }
        static readonly uint[] _syncVoxelOne = new uint[1];

        /// <summary>Upload a single voxel at slot (for mods).</summary>
        public void SetVoxel(int slot, int localIndex, ushort material)
        {
            if (slot < 0 || slot >= _maxChunks || localIndex < 0 || localIndex >= _voxelsPerChunk) return;
            uint v = material;
            int elementOffset = slot * _voxelsPerChunk + localIndex;
            VoxelMaterialBuffer.SetData(new[] { v }, 0, elementOffset, 1);
        }

        public void Dispose()
        {
            VoxelMaterialBuffer?.Release();
            VoxelMaterialBuffer = null;
            ChunkDescriptors?.Release();
            ChunkDescriptors = null;
            MeshVertexBuffer?.Release();
            MeshVertexBuffer = null;
            MeshNormalBuffer?.Release();
            MeshNormalBuffer = null;
            MeshIndexBuffer?.Release();
            MeshIndexBuffer = null;
            InstanceMatrices?.Release();
            InstanceMatrices = null;
            VisibilityFlags?.Release();
            VisibilityFlags = null;
            VisibleChunkIndices?.Release();
            VisibleChunkIndices = null;
            VisibleCountBuffer?.Release();
            VisibleCountBuffer = null;
            DrawArgsBuffer?.Release();
            DrawArgsBuffer = null;
            ExpectedGenerationBuffer?.Release();
            ExpectedGenerationBuffer = null;
            _coordToSlot.Clear();
            _slotToCoord.Clear();
        }
    }
}
