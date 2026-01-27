using System.IO;
using System.Text;
using TerraVoxel.Voxel.Core;
using UnityEngine;

namespace TerraVoxel.Voxel.Systems
{
    /// <summary>
    /// Placeholder save/load for chunk data (JSON stub).
    /// </summary>
    public static class ChunkSaveStub
    {
        public static void Save(ChunkCoord coord, ushort[] materials, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, JsonUtility.ToJson(new Payload { Coord = coord, Materials = materials }), Encoding.UTF8);
        }

        public static Payload Load(string path)
        {
            if (!File.Exists(path)) return default;
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonUtility.FromJson<Payload>(json);
        }

        [System.Serializable]
        public struct Payload
        {
            public ChunkCoord Coord;
            public ushort[] Materials;
        }
    }
}


