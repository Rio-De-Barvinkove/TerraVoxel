using System;

namespace TerraVoxel.Voxel.Core
{
    /// <summary>
    /// Integer chunk coordinate (x,z) with vertical layer index.
    /// </summary>
    [Serializable]
    public struct ChunkCoord : IEquatable<ChunkCoord>
    {
        public int X;
        public int Y;
        public int Z;

        public ChunkCoord(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public bool Equals(ChunkCoord other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object obj) => obj is ChunkCoord other && Equals(other);
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + X;
                hash = hash * 31 + Y;
                hash = hash * 31 + Z;
                return hash;
            }
        }
        public override string ToString() => $"ChunkCoord({X},{Y},{Z})";
    }
}


