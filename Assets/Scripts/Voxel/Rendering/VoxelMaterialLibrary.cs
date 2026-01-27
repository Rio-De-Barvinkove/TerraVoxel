using UnityEngine;

namespace TerraVoxel.Voxel.Rendering
{
    /// <summary>
    /// Holds texture array + triplanar parameters for voxel materials.
    /// </summary>
    [CreateAssetMenu(menuName = "TerraVoxel/Voxel Material Library", fileName = "VoxelMaterialLibrary")]
    public class VoxelMaterialLibrary : ScriptableObject
    {
        public Texture2DArray TextureArray;
        [Range(0.01f, 1f)] public float TriplanarScale = 0.1f;
        [Range(0f, 1f)] public float NormalStrength = 1f;
        [Range(0, 15)] public int DefaultLayerIndex = 0;
    }
}


