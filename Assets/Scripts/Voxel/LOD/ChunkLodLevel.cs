using System;

namespace TerraVoxel.Voxel.Lod
{
    /// <summary>
    /// One LOD level: distance range [MinDistance, MaxDistance], LodStep, Hysteresis and Mode.
    /// MinDistance / MaxDistance in chunk units; range must be non-empty (MaxDistance &gt; MinDistance).
    /// Hysteresis limits LOD flip-flop at boundaries; capped by MaxHysteresis.
    /// </summary>
    [Serializable]
    public struct ChunkLodLevel
    {
        /// <summary>Min distance (chunk units) for this level; inclusive.</summary>
        public int MinDistance;
        /// <summary>Max distance (chunk units) for this level; must be &gt; MinDistance for valid non-empty range.</summary>
        public int MaxDistance;
        /// <summary>LOD step (e.g. 1 = full detail, 2 = half resolution). Must be &gt; 0.</summary>
        public int LodStep;
        /// <summary>Hysteresis in chunk units; 0 &lt;= value &lt;= MaxHysteresis to avoid unexpected behaviour.</summary>
        public int Hysteresis;
        /// <summary>Rendering mode for this level (Mesh, Svo, Billboard, None).</summary>
        public ChunkLodMode Mode;

        /// <summary>Max allowed Hysteresis to avoid unexpected behaviour with large values.</summary>
        public const int MaxHysteresis = 256;

        /// <summary>True if distances non-negative, MaxDistance &gt; MinDistance (non-empty range), LodStep &gt; 0, and 0 &lt;= Hysteresis &lt;= MaxHysteresis. MaxDistance may be int.MaxValue for unbounded far; callers must avoid overflow (e.g. MaxDistance + Hysteresis).</summary>
        public bool IsValid =>
            MinDistance >= 0 &&
            MaxDistance >= 0 &&
            MaxDistance > MinDistance &&
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
