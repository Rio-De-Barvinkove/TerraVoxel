using UnityEngine;

namespace TerraVoxel.Voxel.GPU
{
    /// <summary>
    /// Dispatches GPU frustum culling and build draw commands via ChunkCulling.compute.
    /// </summary>
    public sealed class GpuCuller
    {
        ComputeShader _shader;
        int _kernelFrustumCull;
        int _kernelOcclusionCull;
        int _kernelBuildDrawCommands;
        int _kernelWriteDrawArgs;
        int _kernelGenerateHiZ;
        readonly uint[] _clearCount = new uint[] { 0 };
        static readonly uint[] _syncVisibleCount = new uint[1];
        const string KernelFrustumCull = "FrustumCull";
        const string KernelOcclusionCull = "OcclusionCull";
        const string KernelBuildDrawCommands = "BuildDrawCommands";
        const string KernelWriteDrawArgs = "WriteDrawArgs";
        const string KernelGenerateHiZ = "GenerateHiZ";

        public bool IsValid => _shader != null && _kernelFrustumCull >= 0 && _kernelBuildDrawCommands >= 0 && _kernelWriteDrawArgs >= 0;

        public void Initialize(ComputeShader shader)
        {
            _shader = shader;
            _kernelFrustumCull = _shader != null ? _shader.FindKernel(KernelFrustumCull) : -1;
            _kernelOcclusionCull = _shader != null ? _shader.FindKernel(KernelOcclusionCull) : -1;
            _kernelBuildDrawCommands = _shader != null ? _shader.FindKernel(KernelBuildDrawCommands) : -1;
            _kernelWriteDrawArgs = _shader != null ? _shader.FindKernel(KernelWriteDrawArgs) : -1;
            _kernelGenerateHiZ = _shader != null ? _shader.FindKernel(KernelGenerateHiZ) : -1;
        }

        /// <summary>Run frustum cull, optional Hi-Z occlusion, build draw commands. Pass chunkWorldSize in world units. If depthTexture and hiZMipTarget are set, runs GenerateHiZ then OcclusionCull. frustumMarginOverride: when set, use this instead of chunkWorldSize*6 (use huge value to effectively disable frustum culling).</summary>
        public void Cull(GpuWorldState state, Camera camera, float chunkWorldSize, RenderTexture depthTexture = null, RenderTexture hiZMipTarget = null, float occlusionBias = 0.01f, float? frustumMarginOverride = null, float frustumMarginScale = 6f)
        {
            if (!IsValid || state == null || camera == null) return;

            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(camera);
            Vector4[] planeVec = new Vector4[6];
            for (int i = 0; i < 6; i++)
                planeVec[i] = new Vector4(planes[i].normal.x, planes[i].normal.y, planes[i].normal.z, planes[i].distance);

            Matrix4x4 viewProj = camera.projectionMatrix * camera.worldToCameraMatrix;

            _shader.SetBuffer(_kernelFrustumCull, "CullingChunkBuf", state.ChunkDescriptors);
            _shader.SetBuffer(_kernelFrustumCull, "VisibilityFlags", state.VisibilityFlags);
            if (state.ExpectedGenerationBuffer != null)
                _shader.SetBuffer(_kernelFrustumCull, "ExpectedGeneration", state.ExpectedGenerationBuffer);
            _shader.SetVectorArray("FrustumPlanes", planeVec);
            _shader.SetMatrix("ViewProjection", viewProj);
            _shader.SetFloat("ChunkWorldSize_", chunkWorldSize);
            float frustumMargin = frustumMarginOverride ?? (chunkWorldSize * Mathf.Max(0f, frustumMarginScale));
            _shader.SetFloat("FrustumMargin_", frustumMargin);
            _shader.SetInt("ChunkCount_", state.MaxChunks);

            int groupsFrustum = Mathf.CeilToInt(state.MaxChunks / 64f);
            _shader.Dispatch(_kernelFrustumCull, Mathf.Max(1, groupsFrustum), 1, 1);

            if (_kernelOcclusionCull >= 0 && _kernelGenerateHiZ >= 0 && depthTexture != null && hiZMipTarget != null)
            {
                int w = depthTexture.width / 2;
                int h = depthTexture.height / 2;
                if (w > 0 && h > 0)
                {
                    _shader.SetTexture(_kernelGenerateHiZ, "DepthBuffer_", depthTexture);
                    _shader.SetTexture(_kernelGenerateHiZ, "HiZMip0_", hiZMipTarget);
                    _shader.SetInts("HiZSize_", w, h);
                    _shader.Dispatch(_kernelGenerateHiZ, (w + 7) / 8, (h + 7) / 8, 1);
                    _shader.SetBuffer(_kernelOcclusionCull, "CullingChunkBuf", state.ChunkDescriptors);
                    _shader.SetBuffer(_kernelOcclusionCull, "VisibilityFlags", state.VisibilityFlags);
                    if (state.ExpectedGenerationBuffer != null)
                        _shader.SetBuffer(_kernelOcclusionCull, "ExpectedGeneration", state.ExpectedGenerationBuffer);
                    _shader.SetTexture(_kernelOcclusionCull, "HiZBuffer_", hiZMipTarget);
                    _shader.SetMatrix("ViewProjection", viewProj);
                    _shader.SetFloat("ChunkWorldSize_", chunkWorldSize);
                    _shader.SetFloat("OcclusionBias_", occlusionBias);
                    _shader.SetInt("ChunkCount_", state.MaxChunks);
                    _shader.Dispatch(_kernelOcclusionCull, Mathf.Max(1, groupsFrustum), 1, 1);
                }
            }

            state.VisibleCountBuffer.SetData(_clearCount);
            _shader.SetBuffer(_kernelBuildDrawCommands, "CullingChunkBuf", state.ChunkDescriptors);
            _shader.SetBuffer(_kernelBuildDrawCommands, "VisibilityFlags", state.VisibilityFlags);
            if (state.ExpectedGenerationBuffer != null)
                _shader.SetBuffer(_kernelBuildDrawCommands, "ExpectedGeneration", state.ExpectedGenerationBuffer);
            _shader.SetBuffer(_kernelBuildDrawCommands, "VisibleInstanceMatrices", state.InstanceMatrices);
            _shader.SetBuffer(_kernelBuildDrawCommands, "VisibleChunkIndices", state.VisibleChunkIndices);
            _shader.SetBuffer(_kernelBuildDrawCommands, "VisibleCount", state.VisibleCountBuffer);
            _shader.SetFloat("ChunkWorldSize_", chunkWorldSize);
            _shader.SetInt("ChunkSize_", state.ChunkSize);
            _shader.SetInt("ChunkCount_", state.MaxChunks);

            _shader.Dispatch(_kernelBuildDrawCommands, Mathf.Max(1, groupsFrustum), 1, 1);

            _shader.SetBuffer(_kernelWriteDrawArgs, "DrawArgs", state.DrawArgsBuffer);
            _shader.SetBuffer(_kernelWriteDrawArgs, "VisibleCount", state.VisibleCountBuffer);
            _shader.SetInt("MaxVerticesPerInstance_", state.MaxVerticesPerChunk);
            _shader.Dispatch(_kernelWriteDrawArgs, 1, 1, 1);

            // Sync so Render() / RecordDrawToCommandBuffer read correct VisibleCount (GPU may not have finished otherwise).
            state.VisibleCountBuffer.GetData(_syncVisibleCount, 0, 0, 1);
        }
    }
}
