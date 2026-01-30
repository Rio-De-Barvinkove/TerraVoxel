using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Meshing;
using Unity.Collections;
using UnityEngine;
namespace TerraVoxel.Voxel.Svo
{
    /// <summary>Builds mesh from SvoVolume. Uses stack-based traversal; for very deep trees an iterative approach may be faster. Mesh color uses only R channel for material index.</summary>
    public static class SvoMeshBuilder
    {
        static readonly float Scale = VoxelConstants.VoxelSize;

        struct TraverseState
        {
            public int NodeIndex;
            public Vector3 Origin;
            public int Size;
        }

        /// <summary>Fills mesh with geometry from volume. Caller owns mesh; volume must not be disposed during call.</summary>
        public static void BuildMesh(SvoVolume volume, Mesh mesh, byte maxMaterialIndex, byte fallbackMaterialIndex)
        {
            if (volume == null || mesh == null || !volume.Nodes.IsCreated || volume.Nodes.Length == 0) return;

            var meshData = new MeshData(Allocator.TempJob);
            var stack = new Stack<TraverseState>();
            stack.Push(new TraverseState { NodeIndex = 0, Origin = Vector3.zero, Size = volume.RootSize });

            while (stack.Count > 0)
            {
                var state = stack.Pop();
                int nodeIndex = state.NodeIndex;
                Vector3 origin = state.Origin;
                int size = state.Size;

                if (nodeIndex < 0 || nodeIndex >= volume.Nodes.Length) continue;
                var node = volume.Nodes[nodeIndex];

                if (node.IsLeaf)
                {
                    if (node.IsEmptyLeaf) continue;
                    byte resolved = ResolveMaterialIndex(node.Material, maxMaterialIndex, fallbackMaterialIndex);
                    Color32 color = new Color32(resolved, 0, 0, 255); // R = material index; extend to RGB if needed
                    AppendCube(volume, ref meshData, origin, size, color);
                    continue;
                }

                int childSize = size / 2;
                int baseIndex = node.FirstChild;
                if (childSize <= 0 || baseIndex < 0) continue;

                for (int i = 7; i >= 0; i--)
                {
                    int ox = (i & 1) != 0 ? childSize : 0;
                    int oy = (i & 2) != 0 ? childSize : 0;
                    int oz = (i & 4) != 0 ? childSize : 0;
                    if ((node.ChildMask & (1 << i)) == 0) continue;
                    stack.Push(new TraverseState
                    {
                        NodeIndex = baseIndex + i,
                        Origin = origin + new Vector3(ox, oy, oz),
                        Size = childSize
                    });
                }
            }

            MeshBuilder.Apply(mesh, meshData);
            meshData.Dispose();
        }

        static void AppendCube(SvoVolume volume, ref MeshData data, Vector3 originVoxels, int sizeVoxels, Color32 color)
        {
            float size = sizeVoxels * Scale;
            Vector3 o = originVoxels * Scale;

            Vector3 p000 = o;
            Vector3 p100 = o + new Vector3(size, 0, 0);
            Vector3 p010 = o + new Vector3(0, size, 0);
            Vector3 p110 = o + new Vector3(size, size, 0);
            Vector3 p001 = o + new Vector3(0, 0, size);
            Vector3 p101 = o + new Vector3(size, 0, size);
            Vector3 p011 = o + new Vector3(0, size, size);
            Vector3 p111 = o + new Vector3(size, size, size);

            bool solidNZ = HasSolidNeighbor(volume, originVoxels, sizeVoxels, -1, 0, 0);
            bool solidPZ = HasSolidNeighbor(volume, originVoxels, sizeVoxels, 1, 0, 0);
            bool solidNX = HasSolidNeighbor(volume, originVoxels, sizeVoxels, 0, 0, -1);
            bool solidPX = HasSolidNeighbor(volume, originVoxels, sizeVoxels, 0, 0, 1);
            bool solidNY = HasSolidNeighbor(volume, originVoxels, sizeVoxels, 0, -1, 0);
            bool solidPY = HasSolidNeighbor(volume, originVoxels, sizeVoxels, 0, 1, 0);

            if (!solidNZ) AppendQuad(ref data, p000, p100, p110, p010, new Vector3(0, 0, -1), color);
            if (!solidPZ) AppendQuad(ref data, p101, p001, p011, p111, new Vector3(0, 0, 1), color);
            if (!solidNX) AppendQuad(ref data, p001, p000, p010, p011, new Vector3(-1, 0, 0), color);
            if (!solidPX) AppendQuad(ref data, p100, p101, p111, p110, new Vector3(1, 0, 0), color);
            if (!solidNY) AppendQuad(ref data, p001, p101, p100, p000, new Vector3(0, -1, 0), color);
            if (!solidPY) AppendQuad(ref data, p010, p110, p111, p011, new Vector3(0, 1, 0), color);
        }

        /// <summary>Voxels outside volume (x,y,z &lt; 0 or &gt;= RootSize) are considered empty (no solid neighbor).</summary>
        static bool HasSolidNeighbor(SvoVolume volume, Vector3 originVoxels, int sizeVoxels, int dx, int dy, int dz)
        {
            int nx = (int)originVoxels.x + dx * sizeVoxels;
            int ny = (int)originVoxels.y + dy * sizeVoxels;
            int nz = (int)originVoxels.z + dz * sizeVoxels;
            byte mat = GetMaterialAt(volume, nx, ny, nz);
            return mat != 0;
        }

        /// <summary>Returns 0 for out-of-bounds (boundary voxels treated as empty).</summary>
        static byte GetMaterialAt(SvoVolume volume, int x, int y, int z)
        {
            int rootSize = volume.RootSize;
            if (x < 0 || x >= rootSize || y < 0 || y >= rootSize || z < 0 || z >= rootSize)
                return 0;
            int nodeIndex = 0;
            int size = volume.RootSize;
            while (size > 0 && nodeIndex >= 0 && nodeIndex < volume.Nodes.Length)
            {
                var node = volume.Nodes[nodeIndex];
                if (node.IsLeaf)
                    return node.Material;
                int half = size / 2;
                int child = 0;
                if (x >= half) { child |= 1; x -= half; }
                if (y >= half) { child |= 2; y -= half; }
                if (z >= half) { child |= 4; z -= half; }
                if ((node.ChildMask & (1 << child)) == 0) return 0;
                nodeIndex = node.FirstChild + child;
                size = half;
            }
            return 0;
        }

        static void AppendQuad(ref MeshData data, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Color32 color)
        {
            int start = data.Vertices.Length;
            data.Vertices.Add(a);
            data.Vertices.Add(b);
            data.Vertices.Add(c);
            data.Vertices.Add(d);

            data.Normals.Add(normal);
            data.Normals.Add(normal);
            data.Normals.Add(normal);
            data.Normals.Add(normal);

            data.Colors.Add(new Color32(color.r, color.g, color.b, 255));
            data.Colors.Add(new Color32(color.r, color.g, color.b, 255));
            data.Colors.Add(new Color32(color.r, color.g, color.b, 255));
            data.Colors.Add(new Color32(color.r, color.g, color.b, 255));

            data.Triangles.Add(start + 0);
            data.Triangles.Add(start + 1);
            data.Triangles.Add(start + 2);
            data.Triangles.Add(start + 0);
            data.Triangles.Add(start + 2);
            data.Triangles.Add(start + 3);
        }

        static byte ResolveMaterialIndex(byte material, byte maxMaterialIndex, byte fallbackMaterialIndex)
        {
            if (material == 0) return 0;
            if (maxMaterialIndex == 0) return fallbackMaterialIndex;
            if (material > maxMaterialIndex) return fallbackMaterialIndex;
            return material;
        }
    }
}
