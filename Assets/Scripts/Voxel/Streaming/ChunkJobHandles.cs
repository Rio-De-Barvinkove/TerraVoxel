
using System;
using TerraVoxel.Voxel.Generation;
using TerraVoxel.Voxel.Meshing;
using Unity.Collections;
using Unity.Jobs;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>Job handle and noise layers for chunk generation; caller must Dispose.</summary>
    public struct ChunkGenJobHandle : IDisposable
    {
        /// <summary>Job handle; call Complete() before using results.</summary>
        public JobHandle Handle;
        /// <summary>Noise layers buffer used by the job.</summary>
        public NativeArray<NoiseLayer> Layers;

        public void Dispose()
        {
            if (Layers.IsCreated)
            {
                Layers.Dispose();
            }
        }
    }

    /// <summary>Neighbor voxel buffers for mesh jobs; caller must Dispose. Each face buffer is independent; Dispose checks IsCreated before releasing.</summary>
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

    /// <summary>Job handle and buffers for full-chunk mesh job; caller must Dispose. All buffers are released in Dispose; double-Dispose is safe (IsCreated checks).</summary>
    public struct ChunkMeshJobHandle : IDisposable
    {
        public JobHandle Handle;
        public MeshData MeshData;
        public NativeArray<ushort> MaterialsCopy;
        public NativeArray<GreedyMesher.MaskCell> Mask;
        /// <summary>Shared or owned empty voxel buffer; only disposed when OwnsEmpty is true.</summary>
        public NativeArray<ushort> Empty;
        public NeighborDataBuffers Neighbors;
        public int Epoch;
        public ulong MaterialsHash;
        public int LodStep;
        /// <summary>When true, this handle owns Empty and must dispose it; when false, Empty is shared (e.g. from pool) and must not be disposed.</summary>
        public bool OwnsEmpty;

        public void Dispose()
        {
            try
            {
                if (MaterialsCopy.IsCreated) MaterialsCopy.Dispose();
            }
            catch (Exception) { /* ignore; may already be disposed */ }
            try
            {
                if (Mask.IsCreated) Mask.Dispose();
            }
            catch (Exception) { /* ignore */ }
            try
            {
                if (OwnsEmpty && Empty.IsCreated) Empty.Dispose();
            }
            catch (Exception) { /* ignore */ }
            try { Neighbors.Dispose(); } catch (Exception) { /* ignore */ }
            try { MeshData.Dispose(); } catch (Exception) { /* ignore */ }
        }
    }

    /// <summary>Handle for face-only remesh job: Handle, MeshData, MaterialsCopy, Mask, Neighbors. Caller must Dispose.</summary>
    public struct FaceMeshJobHandle : IDisposable
    {
        public JobHandle Handle;
        public MeshData MeshData;
        public NativeArray<ushort> MaterialsCopy;
        public NativeArray<GreedyMesher.MaskCell> Mask;
        public NeighborDataBuffers Neighbors;

        public void Dispose()
        {
            try
            {
                if (MaterialsCopy.IsCreated) MaterialsCopy.Dispose();
            }
            catch (Exception) { /* ignore */ }
            try
            {
                if (Mask.IsCreated) Mask.Dispose();
            }
            catch (Exception) { /* ignore */ }
            try { Neighbors.Dispose(); } catch (Exception) { /* ignore */ }
            try { MeshData.Dispose(); } catch (Exception) { /* ignore */ }
        }
    }
}

