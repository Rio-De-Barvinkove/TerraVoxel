using System;

namespace TerraVoxel.Voxel.Lod
{
    /// <summary>
    /// One LOD level: distance range [MinDistance, MaxDistance], LodStep, Hysteresis and Mode.
    /// MinDistance=0 / MaxDistance=0 are valid (e.g. for closest chunks at camera).
    /// </summary>
    [Serializable]
    public struct ChunkLodLevel
    {
        public int MinDistance;
        public int MaxDistance;
        public int LodStep;
        public int Hysteresis;
        public ChunkLodMode Mode;

        /// <summary>Max allowed Hysteresis to avoid unexpected behaviour with large values.</summary>
        public const int MaxHysteresis = 256;

        /// <summary>True if distances are non-negative, MaxDistance >= MinDistance, LodStep > 0, and 0 <= Hysteresis <= MaxHysteresis.</summary>
        public bool IsValid =>
            MinDistance >= 0 &&
            MaxDistance >= 0 &&
            MaxDistance >= MinDistance &&
            LodStep > 0 &&
            Hysteresis >= 0 &&
            Hysteresis <= MaxHysteresis;
    }

    public enum ChunkLodMode
    {
        Mesh = 0,
        Svo = 1,
        Billboard = 2,
        None = 3
    }
}
