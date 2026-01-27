using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Generation;
using UnityEngine;

namespace TerraVoxel.Voxel.Save
{
    public class ChunkHybridSaveManager : MonoBehaviour
    {
        [SerializeField] WorldGenConfig worldGen;
        [SerializeField] ChunkSaveManager snapshotManager;
        [SerializeField] ChunkModManager modManager;
        [SerializeField] float deltaPromoteThreshold = 0.3f;
        [SerializeField] bool promoteOnGeneratorVersionChange = true;
        [SerializeField] bool promoteOnSimulatedData = true;
        [SerializeField] bool promoteOnStructuralInvalid = true;
        [SerializeField] bool deleteDeltaWhenSnapshotExists = true;
        [SerializeField] bool alwaysSaveSnapshots = true;

        readonly Dictionary<ChunkCoord, ChunkMeta> _meta = new Dictionary<ChunkCoord, ChunkMeta>();

        void Awake()
        {
            if (snapshotManager == null) snapshotManager = GetComponent<ChunkSaveManager>();
            if (modManager == null) modManager = GetComponent<ChunkModManager>();
        }

        public bool TryLoadSnapshot(ChunkCoord coord, ChunkData data)
        {
            if (snapshotManager == null || !snapshotManager.LoadOnSpawn)
                return false;

            if (!snapshotManager.SnapshotExists(coord))
                return false;

            if (!snapshotManager.TryLoadInto(coord, data, out var meta))
                return false;

            meta.SaveMode = ChunkSaveMode.SnapshotBacked;
            if (meta.GeneratorVersion == 0) meta.GeneratorVersion = GetGeneratorVersion();
            _meta[coord] = meta;

            if (deleteDeltaWhenSnapshotExists && modManager != null && modManager.ModsFileExists(coord))
                modManager.DeleteMods(coord);

            return true;
        }

        public void ApplyDeltaIfAny(ChunkCoord coord, ChunkData data)
        {
            if (modManager == null || !modManager.LoadOnSpawn)
            {
                _meta[coord] = ChunkMeta.Default(ChunkSaveMode.GeneratedOnly, GetGeneratorVersion());
                return;
            }

            if (modManager.ModsFileExists(coord))
            {
                if (modManager.ApplyModsToChunk(coord, data, out var meta))
                {
                    meta.SaveMode = ChunkSaveMode.DeltaBacked;
                    meta.DeltaCount = modManager.GetDeltaCount(coord);
                    if (meta.GeneratorVersion == 0) meta.GeneratorVersion = GetGeneratorVersion();
                    _meta[coord] = meta;
                    return;
                }
            }

            _meta[coord] = ChunkMeta.Default(ChunkSaveMode.GeneratedOnly, GetGeneratorVersion());
        }

        public void HandleChunkUnloaded(ChunkCoord coord, ChunkData data)
        {
            HandleChunkUnloadedInternal(
                coord,
                data,
                snapshotManager != null && snapshotManager.SaveOnUnload,
                modManager != null && modManager.SaveOnUnload);
        }

        public void HandleAllChunksDestroyed(IEnumerable<Chunk> chunks)
        {
            if (chunks == null) return;
            bool saveSnapshots = snapshotManager != null && snapshotManager.SaveOnDestroy;
            bool saveMods = modManager != null && modManager.SaveOnDestroy;
            foreach (var chunk in chunks)
            {
                if (chunk == null || !chunk.Data.IsCreated) continue;
                HandleChunkUnloadedInternal(chunk.Coord, chunk.Data, saveSnapshots, saveMods);
            }
        }

        public void MarkChunkSimulated(ChunkCoord coord, int simTick)
        {
            if (!_meta.TryGetValue(coord, out var meta))
                meta = ChunkMeta.Default(ChunkSaveMode.DeltaBacked, GetGeneratorVersion());
            meta.HasSimulatedData = true;
            meta.LastSimTick = simTick;
            _meta[coord] = meta;
        }

        public void MarkChunkStructurallyInvalid(ChunkCoord coord)
        {
            if (!_meta.TryGetValue(coord, out var meta))
                meta = ChunkMeta.Default(ChunkSaveMode.DeltaBacked, GetGeneratorVersion());
            meta.IsStructurallyInvalid = true;
            _meta[coord] = meta;
        }

        void HandleChunkUnloadedInternal(ChunkCoord coord, ChunkData data, bool saveSnapshots, bool saveMods)
        {
            if (!_meta.TryGetValue(coord, out var meta))
                meta = ChunkMeta.Default(ChunkSaveMode.GeneratedOnly, GetGeneratorVersion());

            int deltaCount = modManager != null ? modManager.GetDeltaCount(coord) : 0;
            meta.DeltaCount = deltaCount;
            if (meta.GeneratorVersion == 0) meta.GeneratorVersion = GetGeneratorVersion();

            bool canSaveSnapshots = saveSnapshots && snapshotManager != null;
            if (alwaysSaveSnapshots && canSaveSnapshots)
            {
                meta.SaveMode = ChunkSaveMode.SnapshotBacked;
                snapshotManager.EnqueueSave(coord, data, meta);
                if (deleteDeltaWhenSnapshotExists && modManager != null)
                    modManager.DeleteMods(coord);
                _meta.Remove(coord);
                return;
            }

            if (meta.SaveMode == ChunkSaveMode.SnapshotBacked)
            {
                if (saveSnapshots && snapshotManager != null)
                    snapshotManager.EnqueueSave(coord, data, meta);

                if (deleteDeltaWhenSnapshotExists && modManager != null)
                    modManager.DeleteMods(coord);

                _meta.Remove(coord);
                return;
            }

            if (deltaCount <= 0)
            {
                if (modManager != null)
                    modManager.DeleteMods(coord);
                meta.SaveMode = ChunkSaveMode.GeneratedOnly;
                _meta.Remove(coord);
                return;
            }

            if (ShouldPromoteToSnapshot(meta, data.Size))
            {
                meta.SaveMode = ChunkSaveMode.SnapshotBacked;
                if (saveSnapshots && snapshotManager != null)
                    snapshotManager.EnqueueSave(coord, data, meta);
                if (modManager != null)
                    modManager.DeleteMods(coord);
                _meta.Remove(coord);
                return;
            }

            meta.SaveMode = ChunkSaveMode.DeltaBacked;
            if (saveMods && modManager != null)
                modManager.SaveMods(coord, meta);
            _meta.Remove(coord);
        }

        bool ShouldPromoteToSnapshot(ChunkMeta meta, int chunkSize)
        {
            int currentVersion = GetGeneratorVersion();
            if (promoteOnGeneratorVersionChange && meta.GeneratorVersion != 0 && meta.GeneratorVersion != currentVersion)
                return true;
            if (promoteOnSimulatedData && meta.HasSimulatedData)
                return true;
            if (promoteOnStructuralInvalid && meta.IsStructurallyInvalid)
                return true;

            int volume = chunkSize * chunkSize * chunkSize;
            float threshold = Mathf.Clamp01(deltaPromoteThreshold);
            return meta.DeltaCount > volume * threshold;
        }

        int GetGeneratorVersion()
        {
            return worldGen != null ? worldGen.GeneratorVersion : 0;
        }
    }
}

