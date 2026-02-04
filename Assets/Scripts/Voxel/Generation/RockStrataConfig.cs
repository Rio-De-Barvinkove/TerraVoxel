using System;
using UnityEngine;

namespace TerraVoxel.Voxel.Generation
{
    [Serializable]
    public struct StrataThickness
    {
        public string Name;
        public float Sedimentary;
        public float Metamorphic;
        public float Igneous;
    }

    /// <summary>
    /// Placeholder for rock layer thickness tables (Provinces). Used for strata-based generation when wired.
    /// </summary>
    [CreateAssetMenu(menuName = "TerraVoxel/Rock Strata Config", fileName = "RockStrataConfig")]
    public class RockStrataConfig : ScriptableObject
    {
        public StrataThickness[] Provinces;
    }
}


