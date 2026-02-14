using System;
using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Meshing;
using Unity.Collections;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>Facade: delegates gen/mesh/face job processing and scheduling to ChunkManager (Owner). When UseGpuPipeline, ProcessGenJobs/ScheduleMesh are no-op on Owner. Main-thread only; no lock. Owner must implement all methods.</summary>
    internal sealed class ChunkJobsManager
    {
        readonly ChunkManager.Context _ctx;

        public ChunkJobsManager(ChunkManager.Context ctx)
        {
            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        }

        internal void ProcessGenJobs() => _ctx.Owner.ProcessGenJobs();
        internal void ProcessMeshJobs() => _ctx.Owner.ProcessMeshJobs();
        internal void ProcessRemeshQueue() => _ctx.Owner.ProcessRemeshQueue();
        internal void ProcessFaceRemeshQueue() => _ctx.Owner.ProcessFaceRemeshQueue();
        internal void ProcessFaceMeshJobs() => _ctx.Owner.ProcessFaceMeshJobs();

        internal void ScheduleGenJob(ChunkCoord coord, Chunk chunk, double spawnStart, bool applySafeSpawn, bool applyDelta, int lodStepOverride = 0)
            => _ctx.Owner.ScheduleGenJob(coord, chunk, spawnStart, applySafeSpawn, applyDelta, lodStepOverride);

        internal bool ScheduleMeshForChunk(ChunkCoord coord, double spawnStart, int lodStep = 1)
            => _ctx.Owner.ScheduleMeshForChunk(coord, spawnStart, lodStep);

        internal bool ScheduleFaceRemeshJobAsync(ChunkCoord coord, Chunk chunk, int faceMask)
            => _ctx.Owner.ScheduleFaceRemeshJobAsync(coord, chunk, faceMask);

        internal NeighborDataBuffers GatherNeighborCopies(ChunkCoord coord)
            => _ctx.Owner.GatherNeighborCopies(coord);

        /// <summary>Owner must validate lodStep &gt; 0, srcSize &gt;= lodSize to avoid out-of-bounds. Returns default (empty buffers) if params invalid.</summary>
        internal NeighborDataBuffers GatherNeighborCopiesLod(ChunkCoord coord, int lodStep, int lodSize, int srcSize)
        {
            if (lodStep <= 0 || lodSize <= 0 || srcSize < lodSize)
                return default;
            return _ctx.Owner.GatherNeighborCopiesLod(coord, lodStep, lodSize, srcSize);
        }

        internal void DownsampleMaterials(NativeArray<ushort> src, int srcSize, int lodStep, NativeArray<ushort> dst)
            => _ctx.Owner.DownsampleMaterials(src, srcSize, lodStep, dst);

        internal void GetMeshMaterialSettings(Chunk chunk, out byte maxMaterialIndex, out byte fallbackMaterialIndex)
            => _ctx.Owner.GetMeshMaterialSettings(chunk, out maxMaterialIndex, out fallbackMaterialIndex);

        internal void CompleteAllJobs() => _ctx.Owner.CompleteAllJobs();

        internal bool IsChunkBusy(ChunkCoord coord) => _ctx.Owner.IsChunkBusy(coord);
        internal bool IsChunkGenerating(ChunkCoord coord) => _ctx.Owner.IsChunkGenerating(coord);
    }
}
