using System;

namespace TerraVoxel.Voxel.Lod
{
    [Serializable]
    public struct ChunkLodLevel
    {
        public int MinDistance;
        public int MaxDistance;
        public int LodStep;
        public int Hysteresis;
        public ChunkLodMode Mode;

        public bool IsValid =>
            MinDistance >= 0 &&
            MaxDistance >= 0 &&
            MaxDistance >= MinDistance &&
            LodStep > 0 &&
            Hysteresis >= 0;
    }

    public enum ChunkLodMode
    {
        Mesh = 0,
        Svo = 1,
        Billboard = 2,
        None = 3
    }
}
