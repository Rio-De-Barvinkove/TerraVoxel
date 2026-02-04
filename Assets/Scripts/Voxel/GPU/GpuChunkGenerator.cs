using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Generation;
using UnityEngine;

namespace TerraVoxel.Voxel.GPU
{
    /// <summary>
    /// Dispatches GPU chunk generation via VoxelGeneration.compute. CPU only schedules; no voxel data on CPU.
    /// </summary>
    public sealed class GpuChunkGenerator
    {
        ComputeShader _shader;
        int _kernelGenerate;
        const string ShaderName = "VoxelGeneration";
        const string KernelGenerate = "GenerateChunk";

        public bool IsValid => _shader != null && _kernelGenerate >= 0;

        /// <summary>Initialize with compute shader. Pass VoxelGeneration.compute.</summary>
        public void Initialize(ComputeShader shader)
        {
            _shader = shader;
            _kernelGenerate = _shader != null ? _shader.FindKernel(KernelGenerate) : -1;
        }

        /// <summary>Schedule generation for one chunk. Writes to state.VoxelMaterialBuffer at slot's voxel offset.</summary>
        public void ScheduleGeneration(GpuWorldState state, ChunkCoord coord, int slot, WorldGenConfig config, NoiseStack noiseStack)
        {
            if (!IsValid || state == null) return;

            int voxelOffset = state.GetVoxelOffset(slot);
            int chunkSize = state.ChunkSize;
            int columnChunks = config != null ? config.ColumnChunks : 8;
            float baseHeight = config != null ? config.BaseHeight : 16f;
            float heightScale = config != null ? config.HeightScale : 32f;
            float horizontalScale = config != null ? config.HorizontalScale : 0.01f;
            int seed = config != null ? config.Seed : 1;
            ushort materialIndex = config != null && config.DefaultMaterialIndex > 0
                ? (ushort)Mathf.Clamp(config.DefaultMaterialIndex, 1, ushort.MaxValue)
                : (ushort)2;

            _shader.SetBuffer(_kernelGenerate, "VoxelMaterialBuffer", state.VoxelMaterialBuffer);
            _shader.SetInts("ChunkCoord_", coord.X, coord.Y, coord.Z);
            _shader.SetInt("ChunkSize_", chunkSize);
            _shader.SetInt("VoxelOffset_", voxelOffset);
            _shader.SetFloat("BaseHeight_", baseHeight);
            _shader.SetFloat("HeightScale_", heightScale);
            _shader.SetFloat("HorizontalScale_", horizontalScale);
            _shader.SetInt("ColumnChunks_", columnChunks);
            _shader.SetInt("Seed_", seed);
            _shader.SetInt("MaterialIndex_", materialIndex);

            int groups = Mathf.CeilToInt(chunkSize / 8f);
            _shader.Dispatch(_kernelGenerate, groups, groups, groups);
        }
    }
}
