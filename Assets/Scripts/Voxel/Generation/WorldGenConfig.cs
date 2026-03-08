using UnityEngine;

namespace TerraVoxel.Voxel.Generation
{
    /// <summary>
    /// Per-world parameters for world generation. Overrides default VoxelConstants (e.g. ChunkSize) where used.
    /// </summary>
    [CreateAssetMenu(menuName = "TerraVoxel/World Gen Config", fileName = "WorldGenConfig")]
    public class WorldGenConfig : ScriptableObject
    {
        public int Seed = 1;
        public int GeneratorVersion = 1;
        public int ChunkSize = 32;
        public int ColumnChunks = 8;
        [Tooltip("World size of one voxel in Unity units. 0.1 = 10cm, 1 = 1m. Use 1 for quick testing with larger blocks.")]
        [Range(0.01f, 2f)] public float VoxelSize = 0.1f;
        public float BaseHeight = 16f;
        public float HeightScale = 32f;
        public float HorizontalScale = 0.01f;
        public bool EnableRivers = false;
        public int DefaultMaterialIndex = 2;

        void OnValidate()
        {
            ChunkSize = Mathf.Max(1, ChunkSize);
            ColumnChunks = Mathf.Max(1, ColumnChunks);
            VoxelSize = Mathf.Clamp(VoxelSize, 0.01f, 2f);
        }

        [Header("Cave Generation (3D noise)")]
        public bool EnableCaves = true;
        [Tooltip("Scale of 3D cave noise. Smaller = larger caves.")]
        public float CaveScale = 0.04f;
        [Tooltip("Threshold for cave carving. Higher = fewer caves. Range ~0.3-0.7.")]
        [Range(0f, 1f)] public float CaveThreshold = 0.55f;
        [Tooltip("Second octave scale multiplier for cave detail.")]
        public float CaveDetailScale = 0.08f;
        [Tooltip("Weight of second cave octave.")]
        [Range(0f, 1f)] public float CaveDetailWeight = 0.3f;

        [Header("Safe Spawn Platform")]
        public bool EnableSafeSpawn = true;
        public float SafeSpawnSizeChunks = 2f;
        public int SafeSpawnThickness = 10;
        public int SafeSpawnMaterialIndex = 200;
        public bool SnapPlayerToSafeSpawn = true;
        public bool SafeSpawnRevalidate = true;
    }
}


