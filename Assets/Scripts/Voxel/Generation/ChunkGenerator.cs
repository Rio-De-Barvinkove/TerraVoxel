using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.GPU;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace TerraVoxel.Voxel.Generation
{
    /// <summary>
    /// Heightmap-based generator. Facade: when useGpu and GpuPipeline set, delegates to GpuChunkGenerator; otherwise runs Burst job. Schedule returns empty layers and default handle when useGpu (caller must still dispose layers).
    /// </summary>
    public class ChunkGenerator : IChunkGenerator
    {
        bool useGpu = true;
        GpuChunkGenerator _gpuChunkGenerator;
        GpuWorldState _gpuWorldState;

        /// <summary>Enable GPU generation when GpuChunkGenerator and GpuWorldState are set.</summary>
        public bool UseGpu { get => useGpu; set => useGpu = value; }
        public bool SupportsGpuGeneration => useGpu && _gpuChunkGenerator != null && _gpuWorldState != null;
        /// <summary>Set for GPU path. When both are set, ScheduleGpuGeneration dispatches to GPU.</summary>
        public void SetGpuPipeline(GpuWorldState worldState, GpuChunkGenerator gpuGenerator)
        {
            _gpuWorldState = worldState;
            _gpuChunkGenerator = gpuGenerator;
        }
        [BurstCompile]
        struct ChunkGeneratorJob : IJobParallelFor
        {
            [WriteOnly] public NativeSlice<ushort> Materials;
            [ReadOnly] public NativeArray<NoiseLayer> Layers;
            public int Size;
            public int CoordX;
            public int CoordY;
            public int CoordZ;
            public float BaseHeight;
            public float HeightScale;
            public float HorizontalScale;
            public int ColumnChunks;
            public int Seed;
            public ushort MaterialIndex;
            public int StartIndex;

            public void Execute(int index)
            {
                int idx = StartIndex + index;
                int size = Size;
                int x = idx % size;
                int y = (idx / size) % size;
                int z = idx / (size * size);

                int worldX = CoordX * size + x;
                int worldY = CoordY * size + y;
                int worldZ = CoordZ * size + z;

                float height = SampleNoise(worldX, worldZ);
                int h = (int)math.clamp(math.floor(height), 0, ColumnChunks * size - 1);

                Materials[index] = worldY <= h ? MaterialIndex : (ushort)VoxelMaterial.Air;
            }

            float SampleNoise(int wx, int wz)
            {
                float totalWeight = 0f;
                float value = 0f;

                for (int i = 0; i < Layers.Length; i++)
                {
                    var layer = Layers[i];
                    float2 uv = new float2((wx + Seed) * layer.Scale * HorizontalScale,
                                           (wz + Seed) * layer.Scale * HorizontalScale);
                    float v = 0f;
                    switch (layer.Type)
                    {
                        case NoiseType.Perlin:
                        case NoiseType.Voronoi: // placeholder
                            v = noise.snoise(uv); // simplex as stand-in
                            v = (v + 1f) * 0.5f;
                            break;
                        case NoiseType.Simplex:
                            v = noise.snoise(uv);
                            v = (v + 1f) * 0.5f;
                            break;
                    }
                    value += v * layer.Weight;
                    totalWeight += math.max(layer.Weight, 0.0001f);
                }

                if (totalWeight < 0.0001f)
                {
                    float2 uv = new float2(wx, wz) * HorizontalScale;
                    float v = noise.snoise(uv);
                    v = (v + 1f) * 0.5f;
                    value = v;
                    totalWeight = 1f;
                }

                return BaseHeight + (value / totalWeight) * HeightScale;
            }
        }

        /// <summary>Schedule GPU generation (coord, slot). Call when useGpu and GpuPipeline set; no ChunkData needed.</summary>
        public void ScheduleGpuGeneration(GpuWorldState state, ChunkCoord coord, int slot, WorldGenConfig config, NoiseStack noiseStack)
        {
            var s = state != null ? state : _gpuWorldState;
            if (useGpu && _gpuChunkGenerator != null && _gpuChunkGenerator.IsValid && s != null)
            {
                _gpuChunkGenerator.ScheduleGeneration(s, coord, slot, config, noiseStack);
                return;
            }
#if UNITY_EDITOR && ALLOW_CPU_FALLBACK
            Debug.LogWarning("[ChunkGenerator] ScheduleGpuGeneration called but GPU not configured; no work done.");
#endif
        }

        public JobHandle Schedule(ChunkData data, ChunkCoord coord, WorldGenConfig config, NoiseStack noiseStack, out NativeArray<NoiseLayer> layers, int startIndex = 0, int count = -1)
        {
            if (useGpu && _gpuChunkGenerator != null && _gpuWorldState != null)
            {
                layers = new NativeArray<NoiseLayer>(0, Allocator.Persistent);
                return default;
            }
            layers = (noiseStack != null && noiseStack.Layers != null)
                ? new NativeArray<NoiseLayer>(noiseStack.Layers, Allocator.Persistent)
                : new NativeArray<NoiseLayer>(0, Allocator.Persistent);

            int matIndex = config.DefaultMaterialIndex <= 0
                ? 2
                : Mathf.Clamp(config.DefaultMaterialIndex, 1, ushort.MaxValue);

            int total = data.Materials.Length;
            if (startIndex < 0) startIndex = 0;
            if (startIndex >= total)
            {
                startIndex = 0;
                count = total;
            }
            if (count <= 0)
            {
                startIndex = 0;
                count = total;
            }
            if (startIndex + count > total)
                count = total - startIndex;

            var materialSlice = new NativeSlice<ushort>(data.Materials, startIndex, count);
            var job = new ChunkGeneratorJob
            {
                Materials = materialSlice,
                Layers = layers,
                Size = data.Size,
                CoordX = coord.X,
                CoordY = coord.Y,
                CoordZ = coord.Z,
                BaseHeight = config.BaseHeight,
                HeightScale = config.HeightScale,
                HorizontalScale = config.HorizontalScale,
                ColumnChunks = config.ColumnChunks,
                Seed = config.Seed,
                MaterialIndex = (ushort)matIndex,
                StartIndex = startIndex
            };

            return job.Schedule(materialSlice.Length, 64);
        }

        public void Generate(ChunkData data, ChunkCoord coord, WorldGenConfig config, NoiseStack noiseStack)
        {
            JobHandle handle = Schedule(data, coord, config, noiseStack, out var layers);
            handle.Complete();
            layers.Dispose();
        }

        /// <summary>Sample terrain height at world X,Z using config and noise stack. Returns 0 if config is null.</summary>
        public static float SampleHeightAt(int worldX, int worldZ, WorldGenConfig config, NoiseStack stack)
        {
            if (config == null) return 0f;
            float totalWeight = 0f;
            float value = 0f;

            if (stack != null && stack.Layers != null)
            {
                foreach (var layer in stack.Layers)
                {
                    float2 uv = new float2((worldX + config.Seed) * layer.Scale * config.HorizontalScale,
                                           (worldZ + config.Seed) * layer.Scale * config.HorizontalScale);
                    float v = 0f;
                    switch (layer.Type)
                    {
                        case NoiseType.Perlin:
                        case NoiseType.Voronoi:
                            v = noise.snoise(uv);
                            v = (v + 1f) * 0.5f;
                            break;
                        case NoiseType.Simplex:
                            v = noise.snoise(uv);
                            v = (v + 1f) * 0.5f;
                            break;
                    }
                    value += v * layer.Weight;
                    totalWeight += math.max(layer.Weight, 0.0001f);
                }
            }

            if (totalWeight < 0.0001f)
            {
                float2 uv = new float2(worldX, worldZ) * config.HorizontalScale;
                float v = noise.snoise(uv);
                v = (v + 1f) * 0.5f;
                value = v;
                totalWeight = 1f;
            }

            return config.BaseHeight + (value / totalWeight) * config.HeightScale;
        }
    }
}

