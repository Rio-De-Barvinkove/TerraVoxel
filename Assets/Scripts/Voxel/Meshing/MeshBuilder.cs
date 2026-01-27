using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace TerraVoxel.Voxel.Meshing
{
    /// <summary>
    /// Copies MeshData into a Unity Mesh using NativeArray views.
    /// </summary>
    public static class MeshBuilder
    {
        public static void Apply(Mesh mesh, MeshData data)
        {
            if (mesh == null) return;
            mesh.Clear();

            int vertCount = data.Vertices.Length;
            int triCount = data.Triangles.Length;
            if (vertCount == 0 || triCount == 0)
            {
                mesh.bounds = new Bounds(Vector3.zero, Vector3.zero);
                return;
            }

            mesh.indexFormat = vertCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;

            // NativeList -> NativeArray view (no alloc).
            NativeArray<Vector3> verts = data.Vertices.AsArray();
            NativeArray<int> tris = data.Triangles.AsArray();
            NativeArray<Color32> cols = data.Colors.AsArray();

            mesh.SetVertices(verts);
            mesh.SetIndices(tris, MeshTopology.Triangles, 0, false);

            if (data.Normals.IsCreated && data.Normals.Length == data.Vertices.Length)
            {
                NativeArray<Vector3> norms = data.Normals.AsArray();
                mesh.SetNormals(norms);
            }
            else
            {
                mesh.RecalculateNormals();
            }

            if (data.Colors.IsCreated && data.Colors.Length == data.Vertices.Length)
            {
                mesh.SetColors(cols);
            }

            mesh.RecalculateBounds();
        }
    }
}

