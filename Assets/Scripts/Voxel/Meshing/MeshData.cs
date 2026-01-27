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

        public void Dispose()
        {
            if (Vertices.IsCreated) Vertices.Dispose();
            if (Triangles.IsCreated) Triangles.Dispose();
            if (Normals.IsCreated) Normals.Dispose();
            if (Colors.IsCreated) Colors.Dispose();
        }
    }
}


