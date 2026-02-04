using UnityEngine;

namespace TerraVoxel.Voxel.Rendering
{
    /// <summary>
    /// Holds texture array and triplanar parameters for voxel materials.
    /// Used for CPU-rendered chunks (SrpBatchingConfig.Configure, VoxelMaterialBinder) and for
    /// fallback material index in mesh jobs (ChunkManager.Jobs). For GPU pipeline, the instanced
    /// material is configured separately on GpuDrivenRenderer.
    /// </summary>
    [CreateAssetMenu(menuName = "TerraVoxel/Voxel Material Library", fileName = "VoxelMaterialLibrary")]
    public class VoxelMaterialLibrary : ScriptableObject
    {
        public Texture2DArray TextureArray;
        [Range(0.01f, 1f)] public float TriplanarScale = 0.1f;
        [Range(0f, 1f)] public float NormalStrength = 1f;
        [Tooltip("Default texture layer index; valid range 0 to Texture2DArray.depth - 1. Clamped at runtime in mesh jobs.")]
        [Range(0, 15)] public int DefaultLayerIndex = 0;

        void OnValidate()
        {
            if (TextureArray == null)
                Debug.LogWarning("[VoxelMaterialLibrary] TextureArray is not set; voxel materials may render pink at runtime.", this);
        }
    }
}


