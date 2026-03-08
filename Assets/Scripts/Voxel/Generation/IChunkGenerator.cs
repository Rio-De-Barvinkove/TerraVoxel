using TerraVoxel.Voxel.Core;
/* using TerraVoxel.Voxel.GPU; */
using Unity.Collections;
using Unity.Jobs;

namespace TerraVoxel.Voxel.Generation
{
    public interface IChunkGenerator
    {
        void Generate(ChunkData data, ChunkCoord coord, WorldGenConfig config, NoiseStack noiseStack);
        JobHandle Schedule(ChunkData data, ChunkCoord coord, WorldGenConfig config, NoiseStack noiseStack, out NativeArray<NoiseLayer> layers, int startIndex = 0, int count = -1);

        /* GPU interface disabled (CPU-only rollback)
        bool SupportsGpuGeneration { get; }
        void ScheduleGpuGeneration(GpuWorldState state, ChunkCoord coord, int slot, WorldGenConfig config, NoiseStack noiseStack);
        */
    }
}


