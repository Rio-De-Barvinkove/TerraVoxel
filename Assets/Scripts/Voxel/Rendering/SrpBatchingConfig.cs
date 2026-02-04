using TerraVoxel.Voxel.Core;
using UnityEngine;

namespace TerraVoxel.Voxel.Rendering
{
    /// <summary>
    /// SRP Batcher compatibility config: shared material and texture array for CPU-rendered voxel chunks.
    /// Attach to same GameObject as ChunkManager; assign to ChunkManager.srpBatchingConfig.
    /// Used only for mesh-on-Chunk CPU render; when useGpuPipeline is true, main drawing is done by
    /// GpuDrivenRenderer with its own instanced material. Ensures single shader, no MaterialPropertyBlock,
    /// Texture2DArray - required for SRP Batching.
    /// </summary>
    public class SrpBatchingConfig : MonoBehaviour
    {
        [Tooltip("Shared material (VoxelTriplanarURP). All chunks use this for SRP Batcher batching.")]
        [SerializeField] Material voxelMaterial;
        [Tooltip("Configures voxelMaterial at startup. Texture array and params applied before chunks spawn.")]
        [SerializeField] VoxelMaterialLibrary voxelMaterialLibrary;

        bool _configured;

        /// <summary>Configure material once at startup. Called from ChunkManager during Awake (before any SpawnChunk).</summary>
        public void Configure()
        {
            if (_configured || voxelMaterial == null || voxelMaterialLibrary == null) return;
            voxelMaterial.SetTexture("_MainTexArr", voxelMaterialLibrary.TextureArray);
            voxelMaterial.SetFloat("_TriplanarScale", voxelMaterialLibrary.TriplanarScale);
            voxelMaterial.SetFloat("_NormalStrength", voxelMaterialLibrary.NormalStrength);
            voxelMaterial.SetInt("_LayerIndex", voxelMaterialLibrary.DefaultLayerIndex);
            _configured = true;
        }

        /// <summary>Apply shared material to chunk. Call from ChunkManager.SpawnChunk.</summary>
        public void ApplyToChunk(Chunk chunk)
        {
            if (chunk == null || voxelMaterial == null) return;
            chunk.SetSharedMaterial(voxelMaterial);
        }

        public Material Material => voxelMaterial;
    }
}
