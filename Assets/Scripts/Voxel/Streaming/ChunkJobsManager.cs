using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Meshing;

namespace TerraVoxel.Voxel.Streaming
{
    internal sealed class ChunkJobsManager
    {
        readonly ChunkManager.Context _ctx;

        public ChunkJobsManager(ChunkManager.Context ctx)
        {
            _ctx = ctx;
        }

        internal void ProcessGenJobs() => _ctx.Owner.ProcessGenJobs();
        internal void ProcessMeshJobs() => _ctx.Owner.ProcessMeshJobs();
        internal void ProcessRemeshQueue() => _ctx.Owner.ProcessRemeshQueue();
        internal void ProcessFaceRemeshQueue() => _ctx.Owner.ProcessFaceRemeshQueue();
        internal void ProcessFaceMeshJobs() => _ctx.Owner.ProcessFaceMeshJobs();

        internal void ScheduleGenJob(ChunkCoord coord, Chunk chunk, double spawnStart, bool applySafeSpawn, bool applyDelta)
            => _ctx.Owner.ScheduleGenJob(coord, chunk, spawnStart, applySafeSpawn, applyDelta);

        internal bool ScheduleMeshForChunk(ChunkCoord coord, double spawnStart, int lodStep = 1)
            => _ctx.Owner.ScheduleMeshForChunk(coord, spawnStart, lodStep);

        internal bool ScheduleFaceRemeshJobAsync(ChunkCoord coord, Chunk chunk, int faceMask)
            => _ctx.Owner.ScheduleFaceRemeshJobAsync(coord, chunk, faceMask);

        internal NeighborDataBuffers GatherNeighborCopies(ChunkCoord coord)
            => _ctx.Owner.GatherNeighborCopies(coord);

        internal NeighborDataBuffers GatherNeighborCopiesLod(ChunkCoord coord, int lodStep, int lodSize, int srcSize)
            => _ctx.Owner.GatherNeighborCopiesLod(coord, lodStep, lodSize, srcSize);

        internal void DownsampleMaterials(Unity.Collections.NativeArray<ushort> src, int srcSize, int lodStep, Unity.Collections.NativeArray<ushort> dst)
            => _ctx.Owner.DownsampleMaterials(src, srcSize, lodStep, dst);

        internal void GetMeshMaterialSettings(Chunk chunk, out byte maxMaterialIndex, out byte fallbackMaterialIndex)
            => _ctx.Owner.GetMeshMaterialSettings(chunk, out maxMaterialIndex, out fallbackMaterialIndex);

        internal void CompleteAllJobs() => _ctx.Owner.CompleteAllJobs();

        internal bool IsChunkBusy(ChunkCoord coord) => _ctx.Owner.IsChunkBusy(coord);
        internal bool IsChunkGenerating(ChunkCoord coord) => _ctx.Owner.IsChunkGenerating(coord);
    }
}
