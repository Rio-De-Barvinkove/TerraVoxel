using TerraVoxel.Voxel.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace TerraVoxel.Voxel.Generation
{
    /// <summary>
    /// Heightmap-based generator with Burst job.
    /// </summary>
    public class ChunkGenerator : IChunkGenerator
    {
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

        public JobHandle Schedule(ChunkData data, ChunkCoord coord, WorldGenConfig config, NoiseStack noiseStack, out NativeArray<NoiseLayer> layers, int startIndex = 0, int count = -1)
        {
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

        public static float SampleHeightAt(int worldX, int worldZ, WorldGenConfig config, NoiseStack stack)
        {
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

