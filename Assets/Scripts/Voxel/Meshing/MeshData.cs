using Unity.Collections;
using UnityEngine;

namespace TerraVoxel.Voxel.Meshing
{
    public struct MeshData
    {
        public NativeList<Vector3> Vertices;
        public NativeList<int> Triangles;
        public NativeList<Vector3> Normals;
        public NativeList<Color32> Colors;

        public MeshData(Allocator allocator, int initialCapacity = 1024)
        {
            Vertices = new NativeList<Vector3>(initialCapacity, allocator);
            Triangles = new NativeList<int>(initialCapacity * 2, allocator);
            Normals = new NativeList<Vector3>(initialCapacity, allocator);
            Colors = new NativeList<Color32>(initialCapacity, allocator);
        }

        public void Clear()
        {
            if (Vertices.IsCreated) Vertices.Clear();
            if (Triangles.IsCreated) Triangles.Clear();
            if (Normals.IsCreated) Normals.Clear();
            if (Colors.IsCreated) Colors.Clear();
        }

        /// <summary>Appends another MeshData; triangle indices are offset by current vertex count.</summary>
        public void AppendFrom(MeshData other)
        {
            if (!other.Vertices.IsCreated || other.Vertices.Length == 0) return;
            int vertOffset = Vertices.Length;
            int n = other.Vertices.Length;
            for (int i = 0; i < n; i++)
            {
                Vertices.Add(other.Vertices[i]);
                if (Normals.IsCreated && other.Normals.IsCreated && i < other.Normals.Length)
                    Normals.Add(other.Normals[i]);
                if (Colors.IsCreated && other.Colors.IsCreated && i < other.Colors.Length)
                    Colors.Add(other.Colors[i]);
            }
            if (other.Triangles.IsCreated)
                for (int i = 0; i < other.Triangles.Length; i++)
                    Triangles.Add(other.Triangles[i] + vertOffset);
        }

        public void Dispose()
        {
            if (Vertices.IsCreated) Vertices.Dispose();
            if (Triangles.IsCreated) Triangles.Dispose();
            if (Normals.IsCreated) Normals.Dispose();
            if (Colors.IsCreated) Colors.Dispose();
        }
    }
}


