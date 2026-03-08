/*
using System.Collections.Concurrent;
using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace TerraVoxel.Voxel.GPU
{
    /// <summary>
    /// Async GPU readback for mesh colliders. Avoids sync GetData stalls.
    /// Request FaceCounter immediately after MeshChunk; callback updates descriptor and queues vertex readback.
    /// ProcessQueue runs vertex readbacks (limited per frame).
    /// Uses pooled Vector3[] and int[] to avoid per-readback allocations.
    /// </summary>
    public sealed class GpuColliderReadbackQueue
    {
        readonly ConcurrentQueue<PendingVertexReadback> _vertexQueue = new ConcurrentQueue<PendingVertexReadback>();
        readonly int _maxVertexReadbacksPerFrame;
        readonly int _maxVerticesPerChunk;
        readonly UnityEngine.Pool.ObjectPool<Vector3[]> _vertPool;
        readonly UnityEngine.Pool.ObjectPool<int[]> _indexPool;
        GpuWorldState _worldState;
        System.Func<ChunkCoord, Chunk> _getChunk;

        struct PendingVertexReadback
        {
            public ChunkCoord Coord;
            public int Slot;
            public int VertexCount;
            public int MeshVertexOffset;
            public int ChunkSize;
            public float VoxelSize;
        }

        public GpuColliderReadbackQueue(int maxVertexReadbacksPerFrame = 2, int maxVerticesPerChunk = 50000)
        {
            _maxVertexReadbacksPerFrame = Mathf.Max(1, maxVertexReadbacksPerFrame);
            _maxVerticesPerChunk = Mathf.Max(1, maxVerticesPerChunk);
            _vertPool = new UnityEngine.Pool.ObjectPool<Vector3[]>(
                createFunc: () => new Vector3[_maxVerticesPerChunk],
                actionOnGet: _ => { },
                actionOnRelease: _ => { },
                actionOnDestroy: _ => { },
                collectionCheck: false,
                defaultCapacity: Mathf.Max(2, _maxVertexReadbacksPerFrame * 2),
                maxSize: 8);
            _indexPool = new UnityEngine.Pool.ObjectPool<int[]>(
                createFunc: () => new int[_maxVerticesPerChunk],
                actionOnGet: _ => { },
                actionOnRelease: _ => { },
                actionOnDestroy: _ => { },
                collectionCheck: false,
                defaultCapacity: Mathf.Max(2, _maxVertexReadbacksPerFrame * 2),
                maxSize: 8);
        }

        public void SetWorldState(GpuWorldState state)
        {
            _worldState = state;
        }

        public void SetChunkResolver(System.Func<ChunkCoord, Chunk> getChunk)
        {
            _getChunk = getChunk;
        }

        /// <summary>Request collider via async readback. Call immediately after MeshChunk for this chunk. Uses FaceCounter to get face count, then queues vertex readback.</summary>
        public void RequestColliderAsync(
            GpuWorldState state,
            ComputeBuffer faceCounter,
            int slot,
            ChunkCoord coord,
            Chunk chunk,
            uint maxFacesForSlot,
            int meshVertexOffset,
            int chunkSize,
            float voxelSize,
            uint descFlags)
        {
            if (state == null || faceCounter == null || chunk == null) return;

            AsyncGPUReadback.Request(faceCounter, (req) =>
            {
                if (req.hasError) return;
                if (state == null || state.ChunkDescriptors == null) return;
                var data = req.GetData<uint>();
                if (data.Length == 0) return;

                uint faceCount = data[0];
                if (faceCount > maxFacesForSlot) faceCount = maxFacesForSlot;
                uint vertexCount = faceCount * 6;
                uint meshOffset = faceCount == 0 ? GpuChunkDescriptor.MeshOffsetNone : (uint)meshVertexOffset;

                uint flags = (vertexCount == 0) ? ChunkDescriptorFlags.Empty : (descFlags & ~ChunkDescriptorFlags.Empty);
                state.UpdateDescriptor(slot, meshOffset, vertexCount, flags);

                if (vertexCount == 0)
                {
                    var chk = _getChunk?.Invoke(coord);
                    if (chk != null && chk.Data.GpuSlot == slot)
                        chk.SetGpuMeshCollider(null);
                    return;
                }

                _vertexQueue.Enqueue(new PendingVertexReadback
                {
                    Coord = coord,
                    Slot = slot,
                    VertexCount = (int)vertexCount,
                    MeshVertexOffset = meshVertexOffset,
                    ChunkSize = chunkSize,
                    VoxelSize = voxelSize
                });
            });
        }

        /// <summary>Process pending vertex readbacks. Call from ChunkManager.Update. Limited to maxVertexReadbacksPerFrame per frame.</summary>
        public void ProcessQueue()
        {
            if (_worldState == null || _getChunk == null) return;

            int processed = 0;
            while (processed < _maxVertexReadbacksPerFrame)
            {
                if (!_vertexQueue.TryDequeue(out PendingVertexReadback pending))
                    break;

                var chunk = _getChunk(pending.Coord);
                if (chunk == null || chunk.Data.GpuSlot != pending.Slot)
                    continue;

                int vertexCount = pending.VertexCount;
                int meshVertexOffset = pending.MeshVertexOffset;
                int chunkSize = pending.ChunkSize;
                float voxelSize = pending.VoxelSize;
                var coord = pending.Coord;
                int slot = pending.Slot;

                var meshBuffer = _worldState.MeshVertexBuffer;
                AsyncGPUReadback.Request(meshBuffer, vertexCount * 12, meshVertexOffset * 12, (req) =>
                {
                    if (req.hasError) return;
                    if (_getChunk == null || _worldState == null) return;
                    var chunkCheck = _getChunk(coord);
                    if (chunkCheck == null || chunkCheck.Data.GpuSlot != slot)
                        return;

                    var nativeVerts = req.GetData<Vector3>();
                    if (nativeVerts.Length == 0) return;
                    int len = nativeVerts.Length;
                    if (len > _maxVerticesPerChunk) return;

                    float half = chunkSize * 0.5f;
                    var verts = _vertPool.Get();
                    for (int i = 0; i < len; i++)
                    {
                        var v = nativeVerts[i];
                        verts[i] = new Vector3((v.x - half) * voxelSize, (v.y - half) * voxelSize, (v.z - half) * voxelSize);
                    }

                    var indices = _indexPool.Get();
                    for (int i = 0; i < len; i++) indices[i] = i;

                    var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                    mesh.SetVertices(verts, 0, len);
                    mesh.SetIndices(indices, 0, len, MeshTopology.Triangles, 0);
                    mesh.RecalculateBounds();

                    _vertPool.Release(verts);
                    _indexPool.Release(indices);

                    chunkCheck.SetGpuMeshCollider(mesh);
                });

                processed++;
            }
        }

        public int PendingCount => _vertexQueue.Count;
    }
}
*/

using TerraVoxel.Voxel.Core;
using UnityEngine;

namespace TerraVoxel.Voxel.GPU
{
    public sealed class GpuColliderReadbackQueue
    {
        public GpuColliderReadbackQueue(int maxVertexReadbacksPerFrame = 2, int maxVerticesPerChunk = 50000) { }
        public void SetWorldState(GpuWorldState state) { }
        public void SetChunkResolver(System.Func<ChunkCoord, Chunk> getChunk) { }
        public void RequestColliderAsync(GpuWorldState state, ComputeBuffer faceCounter, int slot, ChunkCoord coord, Chunk chunk, uint maxFacesForSlot, int meshVertexOffset, int chunkSize, float voxelSize, uint descFlags) { }
        public void ProcessQueue() { }
        public int PendingCount => 0;
    }
}