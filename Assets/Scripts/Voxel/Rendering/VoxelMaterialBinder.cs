using UnityEngine;

namespace TerraVoxel.Voxel.Rendering
{
    /// <summary>
    /// Assigns voxel material to a renderer at runtime.
    /// </summary>
    [RequireComponent(typeof(Renderer))]
    public class VoxelMaterialBinder : MonoBehaviour
    {
        [SerializeField] VoxelMaterialLibrary library;

        public VoxelMaterialLibrary Library => library;

        void Awake()
        {
            if (library == null) return;
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


