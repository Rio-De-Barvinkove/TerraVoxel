using System;
using UnityEngine;

namespace TerraVoxel.Voxel.Generation
{
    public enum NoiseType
    {
        Perlin,
        Simplex,
        Voronoi
    }

    [Serializable]
    public struct NoiseLayer
    {
        public NoiseType Type;
        public float Scale;
        public int Octaves;
        public float Persistence;
        public float Lacunarity;
        [Range(0f, 1f)] public float Weight;
    }

    /// <summary>
    /// Scriptable noise stack description.
    /// </summary>
    [CreateAssetMenu(menuName = "TerraVoxel/Noise Stack", fileName = "NoiseStack")]
    public class NoiseStack : ScriptableObject
    {
        public NoiseLayer[] Layers;
    }
}


