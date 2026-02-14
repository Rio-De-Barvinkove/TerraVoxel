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
        int _kernelWriteMeshDescriptor;
        int _kernelPadSlot;
        ComputeBuffer _faceBuffer;
        ComputeBuffer _faceCounter;
        int _maxFacesPerChunk;
        const string KernelDetectFaces = "DetectFaces";
        const string KernelGenerateVertices = "GenerateVertices";
        const string KernelWriteMeshDescriptor = "WriteMeshDescriptor";
        const string KernelPadSlot = "PadSlot";

        public bool IsValid => _shader != null && _kernelDetectFaces >= 0 && _kernelGenerateVertices >= 0 && _kernelWriteMeshDescriptor >= 0 && _kernelPadSlot >= 0;

        public void Initialize(ComputeShader shader, int maxFacesPerChunk = 200000)
        {
            _shader = shader;
            _kernelDetectFaces = _shader != null ? _shader.FindKernel(KernelDetectFaces) : -1;
            _kernelGenerateVertices = _shader != null ? _shader.FindKernel(KernelGenerateVertices) : -1;
            _kernelWriteMeshDescriptor = _shader != null ? _shader.FindKernel(KernelWriteMeshDescriptor) : -1;
            _kernelPadSlot = _shader != null ? _shader.FindKernel(KernelPadSlot) : -1;
            _maxFacesPerChunk = Mathf.Max(1, maxFacesPerChunk);
            _faceBuffer?.Release();
            _faceCounter?.Release();
            _faceBuffer = new ComputeBuffer(_maxFacesPerChunk * 2, sizeof(uint));
            _faceCounter = new ComputeBuffer(1, sizeof(uint));
        }

        /// <summary>Mesh one chunk at slot. Updates descriptor on GPU via WriteMeshDescriptor; no sync readback.</summary>
        public void MeshChunk(GpuWorldState state, int slot)
        {
            if (!IsValid || state == null || _faceBuffer == null || _faceCounter == null) return;

            GpuChunkDescriptor desc = state.GetDescriptor(slot);
            if ((desc.Flags & ChunkDescriptorFlags.Empty) != 0) return;

            int voxelOffset = state.GetVoxelOffset(slot);
            int meshVertexOffset = state.GetMeshVertexOffset(slot);
            int chunkSize = state.ChunkSize;
            int maxVerticesPerChunk = state.MaxVerticesPerChunk;
            uint maxFacesForSlot = (uint)(maxVerticesPerChunk / 6);

            _faceCounter.SetData(new uint[] { 0 });

            _shader.SetBuffer(_kernelDetectFaces, "VoxelMaterialBuffer", state.VoxelMaterialBuffer);
            _shader.SetBuffer(_kernelDetectFaces, "FaceBuffer", _faceBuffer);
            _shader.SetBuffer(_kernelDetectFaces, "FaceCounter", _faceCounter);
            _shader.SetInt("ChunkSize_", chunkSize);
            _shader.SetInt("VoxelOffset_", voxelOffset);
            _shader.SetInt("MaxFaces_", _maxFacesPerChunk);

            int groups = Mathf.CeilToInt(chunkSize / 8f);
            _shader.Dispatch(_kernelDetectFaces, groups, groups, groups);

            _shader.SetBuffer(_kernelGenerateVertices, "FaceBuffer", _faceBuffer);
            _shader.SetBuffer(_kernelGenerateVertices, "FaceCounter", _faceCounter);
            _shader.SetBuffer(_kernelGenerateVertices, "MeshVertexBuffer", state.MeshVertexBuffer);
            _shader.SetBuffer(_kernelGenerateVertices, "MeshNormalBuffer", state.MeshNormalBuffer);
            _shader.SetInt("MeshVertexOffset_", meshVertexOffset);
            _shader.SetInt("MaxFaces_", _maxFacesPerChunk);
            _shader.SetInt("SlotIndex_", slot);
            _shader.SetInt("MaxFacesForSlot_", (int)maxFacesForSlot);

            int maxGroupsV = Mathf.CeilToInt(_maxFacesPerChunk / 64f);
            _shader.Dispatch(_kernelGenerateVertices, Mathf.Max(1, maxGroupsV), 1, 1);

            _shader.SetBuffer(_kernelPadSlot, "FaceCounter", _faceCounter);
            _shader.SetBuffer(_kernelPadSlot, "MeshVertexBuffer", state.MeshVertexBuffer);
            _shader.SetBuffer(_kernelPadSlot, "MeshNormalBuffer", state.MeshNormalBuffer);
            _shader.SetInt("PadMeshVertexOffset_", meshVertexOffset);
            _shader.SetInt("PadMaxVerticesPerChunk_", maxVerticesPerChunk);
            _shader.SetInt("PadMaxFacesForSlot_", (int)maxFacesForSlot);
            int groupsPad = Mathf.CeilToInt(maxVerticesPerChunk / 64f);
            _shader.Dispatch(_kernelPadSlot, Mathf.Max(1, groupsPad), 1, 1);

            _shader.SetBuffer(_kernelWriteMeshDescriptor, "ChunkDescriptors", state.ChunkDescriptors);
            _shader.SetBuffer(_kernelWriteMeshDescriptor, "FaceCounter", _faceCounter);
            _shader.SetInt("MeshVertexOffset_", meshVertexOffset);
            _shader.SetInt("MaxFaces_", _maxFacesPerChunk);
            _shader.SetInt("SlotIndex_", slot);
            _shader.SetInt("MaxFacesForSlot_", (int)maxFacesForSlot);
            _shader.Dispatch(_kernelWriteMeshDescriptor, 1, 1, 1);

            // No sync readback. Descriptor updated via GpuColliderReadbackQueue async callback.
        }

        /// <summary>FaceCounter buffer for async readback. Request immediately after MeshChunk before any other MeshChunk.</summary>
        public ComputeBuffer FaceCounter => _faceCounter;

        /// <summary>Readback mesh vertices from GPU and create Mesh for MeshCollider. Vertices are converted to chunk-local space (origin at center). Caller must destroy returned mesh when done.</summary>
        public static Mesh CreateColliderMeshFromGpu(GpuWorldState state, int slot, int chunkSize, float voxelSize)
        {
            if (state == null) return null;
            var desc = state.GetDescriptor(slot);
            if (desc.VertexCount == 0 || desc.MeshOffset == GpuChunkDescriptor.MeshOffsetNone) return null;

            int meshVertexOffset = state.GetMeshVertexOffset(slot);
            int vertexCount = (int)desc.VertexCount;
            if (vertexCount <= 0) return null;

            var vertices = new Vector3[vertexCount];
            state.MeshVertexBuffer.GetData(vertices, 0, meshVertexOffset, vertexCount);

            float half = chunkSize * 0.5f;
            for (int i = 0; i < vertices.Length; i++)
            {
                var v = vertices[i];
                vertices[i] = new Vector3((v.x - half) * voxelSize, (v.y - half) * voxelSize, (v.z - half) * voxelSize);
            }

            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(vertices);
            var indices = new int[vertexCount];
            for (int i = 0; i < vertexCount; i++) indices[i] = i;
            mesh.SetIndices(indices, MeshTopology.Triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
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
