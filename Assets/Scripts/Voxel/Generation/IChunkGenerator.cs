using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.GPU;
using Unity.Collections;
using Unity.Jobs;

namespace TerraVoxel.Voxel.Generation
{
    public interface IChunkGenerator
    {
        /// <summary>
        /// Fills chunk data for the given coordinate.
        /// </summary>
        void Generate(ChunkData data, ChunkCoord coord, WorldGenConfig config, NoiseStack noiseStack);

        /// <summary>
        /// Schedules chunk generation and returns the job handle with any temp data.
        /// </summary>
        JobHandle Schedule(ChunkData data, ChunkCoord coord, WorldGenConfig config, NoiseStack noiseStack, out NativeArray<NoiseLayer> layers, int startIndex = 0, int count = -1);

        /// <summary>
        /// True if this generator supports GPU generation (ScheduleGpuGeneration available).
        /// </summary>
        bool SupportsGpuGeneration { get; }

        /// <summary>
        /// Schedules generation on GPU for the given slot. No-op if !SupportsGpuGeneration. Caller must sync (e.g. GpuWorldState.SyncVoxelSlot) before meshing.
        /// </summary>
        void ScheduleGpuGeneration(GpuWorldState state, ChunkCoord coord, int slot, WorldGenConfig config, NoiseStack noiseStack);
    }
}


