using TerraVoxel.Voxel.Streaming;
using UnityEngine;

namespace TerraVoxel.Voxel.Rendering
{
    /// <summary>
    /// Applies VoxelMaterialLibrary to the renderer at runtime. Use when ChunkManager has no SrpBatchingConfig
    /// (legacy path); when SrpBatchingConfig is set, ChunkManager applies the shared material and binder is skipped.
    /// When UseGpuPipeline is true, skips binding (chunks are drawn by GpuDrivenRenderer; configure its
    /// instanced material and textures in Inspector or separately).
    /// </summary>
    [RequireComponent(typeof(Renderer))]
    public class VoxelMaterialBinder : MonoBehaviour
    {
        [SerializeField] VoxelMaterialLibrary library;

        public VoxelMaterialLibrary Library => library;

        void Awake()
        {
            if (library == null) return;
            var chunkManager = GetComponentInParent<ChunkManager>();
            if (chunkManager != null && chunkManager.UseGpuPipeline)
                return;
            if (chunkManager != null && chunkManager.SrpBatchingConfig != null)
                return;
            var renderer = GetComponent<Renderer>();
            if (renderer != null && renderer.sharedMaterial != null)
            {
                renderer.sharedMaterial.SetTexture("_MainTexArr", library.TextureArray);
                renderer.sharedMaterial.SetFloat("_TriplanarScale", library.TriplanarScale);
                renderer.sharedMaterial.SetFloat("_NormalStrength", library.NormalStrength);
                renderer.sharedMaterial.SetInt("_LayerIndex", library.DefaultLayerIndex);
            }
        }
    }
}


