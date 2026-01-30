using Unity.Collections;
using Unity.Mathematics;

namespace TerraVoxel.Voxel.Svo
{
    /// <summary>
    /// SVO volume with NativeList for Burst/Jobs compatibility and fewer allocations.
    /// Node uses byte Material + byte Density for smooth LOD; empty leaf has Material==0.
    /// </summary>
    public sealed class SvoVolume
    {
        public struct Node
        {
            public byte ChildMask;
            public byte Material;
            public byte Density;
            public byte Pad;
            public int FirstChild;

            public bool IsLeaf => ChildMask == 0;
            public bool IsEmptyLeaf => ChildMask == 0 && Material == 0;
        }

        public int RootSize { get; }
        public int LeafSize { get; }
        public NativeList<Node> Nodes { get; }

        public SvoVolume(int rootSize, int leafSize, Allocator allocator = Allocator.TempJob)
        {
            RootSize = rootSize;
            LeafSize = leafSize;
            Nodes = new NativeList<Node>(64, allocator);
        }

        public void Dispose()
        {
            if (Nodes.IsCreated)
                Nodes.Dispose();
        }
    }
}
