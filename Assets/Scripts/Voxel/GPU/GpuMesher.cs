using UnityEngine;

namespace TerraVoxel.Voxel.GPU
{
    /// <summary>
    /// Dispatches GPU face extraction and vertex generation via VoxelMeshing.compute (no greedy).
    /// Mesh only visible chunks; caller checks VisibilityFlags or only calls for visible slots.
    /// </summary>
    public sealed class GpuMesher
    {
        ComputeShader _shader;
        int _kernelDetectFaces;
        int _kernelGenerateVertices;
        int _kernelPadSlot;
        ComputeBuffer _faceBuffer;
        ComputeBuffer _faceCounter;
        readonly uint[] _faceCountReadback = new uint[1];
        int _maxFacesPerChunk;
        const string KernelDetectFaces = "DetectFaces";
        const string KernelGenerateVertices = "GenerateVertices";
        const string KernelPadSlot = "PadSlot";

        public bool IsValid => _shader != null && _kernelDetectFaces >= 0 && _kernelGenerateVertices >= 0 && _kernelPadSlot >= 0;

        public void Initialize(ComputeShader shader, int maxFacesPerChunk = 200000)
        {
            _shader = shader;
            _kernelDetectFaces = _shader != null ? _shader.FindKernel(KernelDetectFaces) : -1;
            _kernelGenerateVertices = _shader != null ? _shader.FindKernel(KernelGenerateVertices) : -1;
            _kernelPadSlot = _shader != null ? _shader.FindKernel(KernelPadSlot) : -1;
            _maxFacesPerChunk = Mathf.Max(1, maxFacesPerChunk);
            _faceBuffer?.Release();
            _faceCounter?.Release();
            _faceBuffer = new ComputeBuffer(_maxFacesPerChunk * 2, sizeof(uint));
            _faceCounter = new ComputeBuffer(1, sizeof(uint));
        }

        /// <summary>Mesh one chunk at slot. Updates descriptor with mesh offset and vertex/index count. Sync readback for face count.</summary>
        public void MeshChunk(GpuWorldState state, int slot)
        {
            if (!IsValid || state == null || _faceBuffer == null || _faceCounter == null) return;

            GpuChunkDescriptor desc = state.GetDescriptor(slot);
            if ((desc.Flags & ChunkDescriptorFlags.Empty) != 0) return;

            int voxelOffset = state.GetVoxelOffset(slot);
            int meshVertexOffset = state.GetMeshVertexOffset(slot);
            int chunkSize = state.ChunkSize;
            int maxVerticesPerChunk = state.MaxVerticesPerChunk;

            _faceCountReadback[0] = 0;
            _faceCounter.SetData(_faceCountReadback);

            _shader.SetBuffer(_kernelDetectFaces, "VoxelMaterialBuffer", state.VoxelMaterialBuffer);
            _shader.SetBuffer(_kernelDetectFaces, "FaceBuffer", _faceBuffer);
            _shader.SetBuffer(_kernelDetectFaces, "FaceCounter", _faceCounter);
            _shader.SetInt("ChunkSize_", chunkSize);
            _shader.SetInt("VoxelOffset_", voxelOffset);
            _shader.SetInt("MaxFaces_", _maxFacesPerChunk);

            int groups = Mathf.CeilToInt(chunkSize / 8f);
            _shader.Dispatch(_kernelDetectFaces, groups, groups, groups);

            _faceCounter.GetData(_faceCountReadback);
            uint faceCount = _faceCountReadback[0];
            if (faceCount == 0 || faceCount > (uint)_maxFacesPerChunk)
            {
                state.UpdateDescriptor(slot, GpuChunkDescriptor.MeshOffsetNone, 0, desc.Flags);
                return;
            }

            // DrawProceduralIndirect uses triangle list (no index buffer): 6 vertices per quad.
            uint maxFacesForSlot = (uint)(maxVerticesPerChunk / 6);
            if (faceCount > maxFacesForSlot)
                faceCount = maxFacesForSlot;
            uint vertexCount = faceCount * 6;

            _shader.SetBuffer(_kernelGenerateVertices, "FaceBuffer", _faceBuffer);
            _shader.SetBuffer(_kernelGenerateVertices, "MeshVertexBuffer", state.MeshVertexBuffer);
            _shader.SetBuffer(_kernelGenerateVertices, "MeshNormalBuffer", state.MeshNormalBuffer);
            _shader.SetInt("MeshVertexOffset_", meshVertexOffset);
            _shader.SetInt("FaceCount_", (int)faceCount);

            int groupsV = Mathf.CeilToInt((int)faceCount / 64f);
            _shader.Dispatch(_kernelGenerateVertices, Mathf.Max(1, groupsV), 1, 1);

            int padCount = maxVerticesPerChunk - (int)vertexCount;
            if (padCount > 0)
            {
                int padStart = meshVertexOffset + (int)vertexCount;
                _shader.SetBuffer(_kernelPadSlot, "MeshVertexBuffer", state.MeshVertexBuffer);
                _shader.SetBuffer(_kernelPadSlot, "MeshNormalBuffer", state.MeshNormalBuffer);
                _shader.SetInt("PadStart_", padStart);
                _shader.SetInt("PadCount_", padCount);
                int groupsPad = Mathf.CeilToInt(padCount / 64f);
                _shader.Dispatch(_kernelPadSlot, Mathf.Max(1, groupsPad), 1, 1);
            }

            state.UpdateDescriptor(slot, (uint)meshVertexOffset, vertexCount, desc.Flags);
        }

        public void Dispose()
        {
            _faceBuffer?.Release();
            _faceBuffer = null;
            _faceCounter?.Release();
            _faceCounter = null;
        }
    }
}
