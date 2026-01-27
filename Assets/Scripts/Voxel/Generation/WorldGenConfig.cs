using UnityEngine;

namespace TerraVoxel.Voxel.Generation
{
    /// <summary>
    /// Global parameters for world generation.
    /// </summary>
    [CreateAssetMenu(menuName = "TerraVoxel/World Gen Config", fileName = "WorldGenConfig")]
    public class WorldGenConfig : ScriptableObject
    {
        public int Seed = 1;
        public int GeneratorVersion = 1;
        public int ChunkSize = 32;
        public int ColumnChunks = 8;
        public float BaseHeight = 16f;
        public float HeightScale = 32f;
        public float HorizontalScale = 0.01f;
        public bool EnableRivers = false;
        public int DefaultMaterialIndex = 2;

        [Header("Safe Spawn Platform")]
        public bool EnableSafeSpawn = true;
        public float SafeSpawnSizeChunks = 2f;
        public int SafeSpawnThickness = 10;
        public int SafeSpawnMaterialIndex = 200;
        public bool SnapPlayerToSafeSpawn = true;
        public bool SafeSpawnRevalidate = true;
    }
}


