using System;
using TerraVoxel.Voxel.Generation;
using TerraVoxel.Voxel.Meshing;
using Unity.Collections;
using Unity.Jobs;

namespace TerraVoxel.Voxel.Streaming
{
    public struct ChunkGenJobHandle : IDisposable
    {
        public JobHandle Handle;
        public NativeArray<NoiseLayer> Layers;

        public void Dispose()
        {
            if (Layers.IsCreated) Layers.Dispose();
        }
    }

    public struct NeighborDataBuffers : IDisposable
    {
        public GreedyMesher.NeighborData Data;
            public NativeArray<ushort> NegX;
            public NativeArray<ushort> PosX;
            public NativeArray<ushort> NegY;
            public NativeArray<ushort> PosY;
            public NativeArray<ushort> NegZ;
            public NativeArray<ushort> PosZ;

        public void Dispose()
        {
            if (NegX.IsCreated) NegX.Dispose();
            if (PosX.IsCreated) PosX.Dispose();
            if (NegY.IsCreated) NegY.Dispose();
            if (PosY.IsCreated) PosY.Dispose();
            if (NegZ.IsCreated) NegZ.Dispose();
            if (PosZ.IsCreated) PosZ.Dispose();
        }
    }

    public struct ChunkMeshJobHandle : IDisposable
    {
        public JobHandle Handle;
        public MeshData MeshData;
        public NativeArray<ushort> MaterialsCopy;
        public NativeArray<GreedyMesher.MaskCell> Mask;
        public NativeArray<ushort> Empty;
        public NeighborDataBuffers Neighbors;

        public void Dispose()
        {
            if (MaterialsCopy.IsCreated) MaterialsCopy.Dispose();
            if (Mask.IsCreated) Mask.Dispose();
            if (Empty.IsCreated) Empty.Dispose();
            Neighbors.Dispose();
            MeshData.Dispose();
        }
    }
}

