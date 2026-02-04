using UnityEngine;

namespace TerraVoxel.Voxel.GPU
{
    /// <summary>
    /// Optional central reference for GPU pipeline compute shaders. Assign in Inspector; ChunkManager can use this asset instead of four separate ComputeShader fields.
    /// </summary>
    [CreateAssetMenu(menuName = "TerraVoxel/GPU Pipeline Compute Assets", fileName = "GpuPipelineComputeAssets")]
    public class GpuPipelineComputeAssets : ScriptableObject
    {
        [Tooltip("VoxelGeneration.compute")]
        public ComputeShader voxelGeneration;
        [Tooltip("ChunkAnalysis.compute")]
        public ComputeShader chunkAnalysis;
        [Tooltip("ChunkCulling.compute")]
        public ComputeShader chunkCulling;
        [Tooltip("VoxelMeshing.compute")]
        public ComputeShader voxelMeshing;

        public bool HasAll =>
            voxelGeneration != null &&
            chunkAnalysis != null &&
            chunkCulling != null &&
            voxelMeshing != null;
    }
}
