using System;

namespace TerraVoxel.Voxel.Save
{
    public enum ChunkSaveMode : byte
    {
        GeneratedOnly = 0,
        DeltaBacked = 1,
        SnapshotBacked = 2
    }

    [Serializable]
    public struct ChunkMeta
    {
        public ChunkSaveMode SaveMode;
        public int GeneratorVersion;
        public int LastSimTick;
        public int DeltaCount;
        public bool HasSimulatedData;
        public bool IsStructurallyInvalid;

        public static ChunkMeta Default(ChunkSaveMode mode, int generatorVersion)
        {
            return new ChunkMeta
            {
                SaveMode = mode,
                GeneratorVersion = generatorVersion,
                LastSimTick = 0,
                DeltaCount = 0,
                HasSimulatedData = false,
                IsStructurallyInvalid = false
            };
        }
    }
}

