// Job-related partial: gen/mesh/face jobs, scheduling, neighbor copies, downsampling, material settings, remesh queues.

using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Generation;
using TerraVoxel.Voxel.Lod;
using TerraVoxel.Voxel.Meshing;
using TerraVoxel.Voxel.Rendering;
using TerraVoxel.Voxel.Save;
using TerraVoxel.Voxel.Svo;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>Partial: CompleteAllJobs, ProcessGenJobs, ProcessMeshJobs, ScheduleGenJob, ScheduleMeshForChunk, GatherNeighbor*, DownsampleMaterials, GetMeshMaterialSettings, ProcessFaceRemesh*, ProcessRemeshQueue.</summary>
    public partial class ChunkManager
    {
        internal void CompleteAllJobs()
        {
            foreach (var kvp in _genJobs)
            {
                var job = kvp.Value.Job;
                job.Handle.Complete();
                job.Dispose();
            }
            foreach (var kvp in _meshJobs)
            {
                var job = kvp.Value.Job;
                job.Handle.Complete();
                job.Dispose();
            }
            foreach (var kvp in _faceMeshJobs)
            {
                kvp.Value.Job.Handle.Complete();
                kvp.Value.Job.Dispose();
            }
            _genJobs.Clear();
            _meshJobs.Clear();
            _faceMeshJobs.Clear();
            _genCompleted.Clear();
            _meshCompleted.Clear();
        }

        /// <summary>Completes finished gen jobs on main thread; applies safe spawn, delta, schedules mesh. Exceptions in Complete() or subsequent logic are not caught.</summary>
        internal void ProcessGenJobs()
        {
            if (_genJobs.Count == 0) return;
            _genCompleted.Clear();
            foreach (var kvp in _genJobs)
            {
                if (kvp.Value.Job.Handle.IsCompleted)
                    _genCompleted.Add(kvp.Key);
            }

            ChunkCoord center = default;
            int keepRadius = 0;
            bool hasCenter = player != null && worldGen != null;
            if (hasCenter)
            {
                center = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize);
                keepRadius = EffectiveUnloadRadius();
                if (enablePreload)
                    keepRadius = Mathf.Max(keepRadius, EffectivePreloadRadius());
            }

            foreach (var coord in _genCompleted)
            {
                if (!_genJobs.TryGetValue(coord, out var task)) continue;
                if (task.Epoch != _streamingEpoch && hasCenter && !IsWithinKeepRadius(coord, center, keepRadius))
                {
                    task.Job.Handle.Complete();
                    task.Job.Dispose();
                    _genJobs.Remove(coord);
                    if (_active.ContainsKey(coord))
                        QueueRemoval(coord);
                    continue;
                }
                task.Job.Handle.Complete();
                task.Job.Dispose();

                if (task.UseSlices && task.SliceIndex + 1 < task.SliceCount)
                {
                    if (_generator != null && worldGen != null && task.Chunk != null && task.Chunk.Data.IsCreated)
                    {
                        int nextIndex = task.SliceIndex + 1;
                        int startIndex = nextIndex * task.SliceSize;
                        int total = task.Chunk.Data.Materials.Length;
                        int count = Mathf.Min(task.SliceSize, total - startIndex);
                        if (count > 0)
                        {
                            var handle = _generator.Schedule(task.Chunk.Data, coord, worldGen, noiseStack, out var layers, startIndex, count);
                            task.Job = new ChunkGenJobHandle
                            {
                                Handle = handle,
                                Layers = layers
                            };
                            task.SliceIndex = nextIndex;
                            _genJobs[coord] = task;
                            continue;
                        }
                    }
                }

                _genJobs.Remove(coord);

                if (!_active.TryGetValue(coord, out var chunk) || chunk != task.Chunk)
                    continue;

                _lastGenMs = (long)((Time.realtimeSinceStartupAsDouble - task.StartTime) * 1000.0);

                bool appliedSafeSpawn = false;
                if (task.ApplySafeSpawn)
                    appliedSafeSpawn = ApplySafeSpawnToChunk(chunk, coord);

                if (hybridSave != null && task.ApplyDelta)
                {
                    hybridSave.ApplyDeltaIfAny(coord, chunk.Data);
                    if (modManager != null && modManager.GetDeltaCount(coord) > 0)
                        modManager.ApplyModsToChunk(coord, chunk.Data);
                }
                else if (hybridSave == null && modManager != null)
                {
                    modManager.ApplyModsToChunk(coord, chunk.Data);
                }

                if (appliedSafeSpawn && _pendingSafeSpawnSnap)
                {
                    SnapPlayerToSafeSpawn();
                    _pendingSafeSpawnSnap = false;
                }

                if (initialLodFromDistance && lodSettings != null && player != null && worldGen != null)
                {
                    ChunkCoord lodCenter = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize);
                    int dx = Mathf.Abs(coord.X - lodCenter.X);
                    int dz = Mathf.Abs(coord.Z - lodCenter.Z);
                    int dist = Mathf.Max(dx, dz);
                    var desired = lodSettings.ResolveLevel(dist, 1, ChunkLodMode.Mesh);

                    if (desired.Mode == ChunkLodMode.None)
                    {
                        chunk.SetRendererEnabled(false);
                        chunk.SetColliderEnabled(false);
                        chunk.UsesSvo = false;
                        chunk.IsLowLod = true;
                        chunk.LodStep = Mathf.Max(1, desired.LodStep);
                        chunk.LodStartTime = Time.realtimeSinceStartupAsDouble;
                    }
                    else if (desired.Mode == ChunkLodMode.Svo && svoManager != null)
                    {
                        GetMeshMaterialSettings(chunk, out var maxMaterialIndex, out var fallbackMaterialIndex);
                        if (svoManager.TryGetOrBuildMesh(coord, chunk.Data, Mathf.Max(1, desired.LodStep), maxMaterialIndex, fallbackMaterialIndex, out var svoMesh))
                        {
                            chunk.ApplySharedMesh(svoMesh, addCollider: false);
                            if (srpBatchingConfig != null) srpBatchingConfig.ApplyToChunk(chunk);
                            else if (voxelMaterial != null) chunk.SetSharedMaterial(voxelMaterial);
                            chunk.UsesSvo = true;
                            chunk.LodStep = desired.LodStep;
                            chunk.IsLowLod = true;
                            chunk.LodStartTime = Time.realtimeSinceStartupAsDouble;
                        }
                        else
                            ScheduleMeshForChunk(coord, task.SpawnStart, Mathf.Max(1, desired.LodStep));
                    }
                    else
                    {
                        int lodStep = Mathf.Max(1, desired.LodStep);
                        if (!ScheduleMeshForChunk(coord, task.SpawnStart, lodStep))
                            QueueRemesh(coord);
                    }
                }
                else
                {
                    if (!ScheduleMeshForChunk(coord, task.SpawnStart, GetInitialLodStep(coord)))
                        QueueRemesh(coord);
                }
            }
        }

        /// <summary>Completes finished mesh jobs on main thread; queues integration. Exceptions in Complete() or subsequent logic are not caught.</summary>
        internal void ProcessMeshJobs()
        {
            if (_meshJobs.Count == 0) return;
            _meshCompleted.Clear();
            foreach (var kvp in _meshJobs)
            {
                if (kvp.Value.Job.Handle.IsCompleted)
                    _meshCompleted.Add(kvp.Key);
            }

            ChunkCoord center = default;
            int keepRadius = 0;
            bool hasCenter = player != null && worldGen != null;
            if (hasCenter)
            {
                center = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize);
                keepRadius = EffectiveUnloadRadius();
                if (enablePreload)
                    keepRadius = Mathf.Max(keepRadius, EffectivePreloadRadius());
            }

            foreach (var coord in _meshCompleted)
            {
                if (!_meshJobs.TryGetValue(coord, out var task)) continue;
                if (task.Job.Epoch != _streamingEpoch && hasCenter && !IsWithinKeepRadius(coord, center, keepRadius))
                {
                    task.Job.Handle.Complete();
                    task.Job.Dispose();
                    _meshJobs.Remove(coord);
                    if (_active.ContainsKey(coord))
                        QueueRemoval(coord);
                    continue;
                }
                task.Job.Handle.Complete();
                _meshJobs.Remove(coord);

                if (!_active.TryGetValue(coord, out var chunk) || chunk != task.Chunk)
                {
                    task.Job.Dispose();
                    continue;
                }

                if (worldGen != null && worldGen.EnableSafeSpawn && worldGen.SafeSpawnRevalidate && _safeSpawnInitialized)
                {
                    if (ReapplySafeSpawnToChunk(chunk, coord, out var changed) && changed)
                    {
                        task.Job.Dispose();
                        RequestRemesh(coord, includeNeighbors: true);
                        continue;
                    }
                }

                if (_integrationSet.TryAdd(coord, 0))
                {
                    _pendingMeshJobs[coord] = task.Job;
                    _integrationQueue.Enqueue(coord);
                }
                else
                {
                    if (_pendingMeshJobs.TryGetValue(coord, out var oldJob))
                        oldJob.Dispose();
                    _pendingMeshJobs[coord] = task.Job;
                }

                _lastMeshMs = (long)((Time.realtimeSinceStartupAsDouble - task.StartTime) * 1000.0);
                _lastTotalMs = task.SpawnStart > 0
                    ? (long)((Time.realtimeSinceStartupAsDouble - task.SpawnStart) * 1000.0)
                    : _lastMeshMs;
                _lastSpawnCoord = coord;
            }
        }

        internal bool IsChunkBusy(ChunkCoord coord)
        {
            return _genJobs.ContainsKey(coord) || _meshJobs.ContainsKey(coord);
        }

        internal bool IsChunkGenerating(ChunkCoord coord)
        {
            return _genJobs.ContainsKey(coord);
        }

        internal void ScheduleGenJob(ChunkCoord coord, Chunk chunk, double spawnStart, bool applySafeSpawn, bool applyDelta)
        {
            if (_genJobs.ContainsKey(coord)) return;
            if (_generator == null || worldGen == null) return;
            if (chunk == null || !chunk.Data.IsCreated) return;
            bool useSlices = enableGenSlicing && genSliceCount > 1;
            int total = chunk.Data.Materials.Length;
            int slices = useSlices ? Mathf.Max(1, genSliceCount) : 1;
            int sliceSize = useSlices ? (total + slices - 1) / slices : total;
            int count = useSlices ? Mathf.Min(sliceSize, total) : total;

            var handle = _generator.Schedule(chunk.Data, coord, worldGen, noiseStack, out var layers, 0, count);
            var job = new ChunkGenJobHandle
            {
                Handle = handle,
                Layers = layers
            };

            _genJobs[coord] = new GenTask
            {
                Coord = coord,
                Chunk = chunk,
                Job = job,
                StartTime = Time.realtimeSinceStartupAsDouble,
                SpawnStart = spawnStart,
                ApplySafeSpawn = applySafeSpawn,
                ApplyDelta = applyDelta,
                Epoch = _streamingEpoch,
                UseSlices = useSlices,
                SliceIndex = 0,
                SliceCount = slices,
                SliceSize = sliceSize
            };
        }

        internal bool ScheduleMeshForChunk(ChunkCoord coord, double spawnStart, int lodStep = 1)
        {
            if (!_active.TryGetValue(coord, out var chunk)) return false;
            if (!chunk.Data.IsCreated) return false;
            if (IsChunkGenerating(coord)) return false;
            if (_meshJobs.ContainsKey(coord)) return false;
            if (IsInIntegrationSet(coord) || _pendingCachedMeshes.ContainsKey(coord))
            {
                _remeshAfterIntegration.Add(coord);
                return false;
            }
            if (_meshJobs.Count >= CurrentMaxMeshJobsInFlight) return false;

            lodStep = Mathf.Max(1, lodStep);
            int chunkSize = chunk.Data.Size;
            if (lodStep > 1 && (lodStep > chunkSize || (chunkSize % lodStep) != 0))
            {
                if (!_warnedLodStepMismatch)
                {
                    Debug.LogWarning($"[ChunkManager] Reverse LOD step {lodStep} is invalid for chunk size {chunkSize}. Falling back to full detail.");
                    _warnedLodStepMismatch = true;
                }
                lodStep = 1;
            }

            ulong materialsHash = 0;
            bool useCache = enableMeshCache && maxMeshCacheEntries > 0 && lodStep == 1;

            NeighborDataBuffers neighbors = default;
            var meshData = new MeshData(Allocator.Persistent);
            NativeArray<ushort> materialsCopy;
            ChunkData dataCopy;
            float voxelScale = VoxelConstants.VoxelSize;

            if (lodStep > 1)
            {
                int srcSize = chunk.Data.Size;
                int lodSize = Mathf.Max(1, srcSize / lodStep);
                materialsCopy = new NativeArray<ushort>(lodSize * lodSize * lodSize, Allocator.Persistent);
                DownsampleMaterials(chunk.Data.Materials, srcSize, lodStep, materialsCopy);
                dataCopy = new ChunkData { Materials = materialsCopy, Size = lodSize };
                voxelScale = VoxelConstants.VoxelSize * lodStep;
                neighbors = GatherNeighborCopiesLod(coord, lodStep, lodSize, srcSize);
            }
            else
            {
                neighbors = GatherNeighborCopies(coord);
                materialsCopy = new NativeArray<ushort>(chunk.Data.Materials.Length, Allocator.Persistent);
                NativeArray<ushort>.Copy(chunk.Data.Materials, materialsCopy);
                dataCopy = new ChunkData { Materials = materialsCopy, Size = chunk.Data.Size };
            }

            if (useCache)
            {
                if (!HasAllNeighbors(neighbors.Data))
                {
                    useCache = false;
                }
                else
                {
                    materialsHash = ComputeMeshCacheHash(chunk.Data.Materials, chunk.Data.Size, neighbors, lodStep, chunk.Data.Density);

                    if (_meshCache.TryGetValue(materialsHash, out var cachedEntry) && cachedEntry.Mesh != null)
                    {
                        var negXCoord = new ChunkCoord(coord.X - 1, coord.Y, coord.Z);
                        var posXCoord = new ChunkCoord(coord.X + 1, coord.Y, coord.Z);
                        var negYCoord = new ChunkCoord(coord.X, coord.Y - 1, coord.Z);
                        var posYCoord = new ChunkCoord(coord.X, coord.Y + 1, coord.Z);
                        var negZCoord = new ChunkCoord(coord.X, coord.Y, coord.Z - 1);
                        var posZCoord = new ChunkCoord(coord.X, coord.Y, coord.Z + 1);

                        bool neighborsValid = true;
                        neighborsValid &= !IsChunkGenerating(negXCoord) && _active.ContainsKey(negXCoord);
                        neighborsValid &= !IsChunkGenerating(posXCoord) && _active.ContainsKey(posXCoord);
                        neighborsValid &= !IsChunkGenerating(negYCoord) && _active.ContainsKey(negYCoord);
                        neighborsValid &= !IsChunkGenerating(posYCoord) && _active.ContainsKey(posYCoord);
                        neighborsValid &= !IsChunkGenerating(negZCoord) && _active.ContainsKey(negZCoord);
                        neighborsValid &= !IsChunkGenerating(posZCoord) && _active.ContainsKey(posZCoord);

                        if (!neighborsValid)
                        {
                            useCache = false;
                        }
                    }
                }

                if (useCache && _meshCache.TryGetValue(materialsHash, out var cachedMesh) && cachedMesh.Mesh != null)
                {
                    if (cachedMesh.Mesh.vertexCount == 0)
                    {
                        useCache = false;
                    }
                    else
                    {
                        cachedMesh.LastUsedFrame = Time.frameCount;
                        _meshCache[materialsHash] = cachedMesh;
                        if (TryQueueCachedMesh(coord, materialsHash, cachedMesh.Mesh))
                        {
                            neighbors.Dispose();
                            materialsCopy.Dispose();
                            meshData.Dispose();
                            return true;
                        }
                    }
                }
            }

            var mask = new NativeArray<GreedyMesher.MaskCell>(dataCopy.Size * dataCopy.Size, Allocator.Persistent);
            if (!_emptyMaterials.IsCreated)
                _emptyMaterials = new NativeArray<ushort>(0, Allocator.Persistent);
            var empty = _emptyMaterials;

            GetMeshMaterialSettings(chunk, out var maxMaterialIndex, out var fallbackMaterialIndex);
            var handle = GreedyMesher.Schedule(dataCopy, neighbors.Data, maxMaterialIndex, fallbackMaterialIndex, mask, empty, ref meshData, voxelScale, 0, enableSeamSkirts, seamSkirtOffset);

            var meshJob = new ChunkMeshJobHandle
            {
                Handle = handle,
                MeshData = meshData,
                MaterialsCopy = materialsCopy,
                Mask = mask,
                Empty = empty,
                Neighbors = neighbors,
                Epoch = _streamingEpoch,
                MaterialsHash = materialsHash,
                LodStep = lodStep,
                OwnsEmpty = false
            };

            _meshJobs[coord] = new MeshTask
            {
                Coord = coord,
                Chunk = chunk,
                Job = meshJob,
                StartTime = Time.realtimeSinceStartupAsDouble,
                SpawnStart = spawnStart
            };

            return true;
        }

        internal NeighborDataBuffers GatherNeighborCopies(ChunkCoord coord)
        {
            var buffers = new NeighborDataBuffers();
            var data = new GreedyMesher.NeighborData();

            var negXCoord = new ChunkCoord(coord.X - 1, coord.Y, coord.Z);
            if (_active.TryGetValue(negXCoord, out var negX) && negX.Data.IsCreated && !IsChunkGenerating(negXCoord))
            {
                data.HasNegX = true;
                buffers.NegX = new NativeArray<ushort>(negX.Data.Materials.Length, Allocator.Persistent);
                NativeArray<ushort>.Copy(negX.Data.Materials, buffers.NegX);
                data.NegX = buffers.NegX;
            }
            var posXCoord = new ChunkCoord(coord.X + 1, coord.Y, coord.Z);
            if (_active.TryGetValue(posXCoord, out var posX) && posX.Data.IsCreated && !IsChunkGenerating(posXCoord))
            {
                data.HasPosX = true;
                buffers.PosX = new NativeArray<ushort>(posX.Data.Materials.Length, Allocator.Persistent);
                NativeArray<ushort>.Copy(posX.Data.Materials, buffers.PosX);
                data.PosX = buffers.PosX;
            }
            var negYCoord = new ChunkCoord(coord.X, coord.Y - 1, coord.Z);
            if (_active.TryGetValue(negYCoord, out var negY) && negY.Data.IsCreated && !IsChunkGenerating(negYCoord))
            {
                data.HasNegY = true;
                buffers.NegY = new NativeArray<ushort>(negY.Data.Materials.Length, Allocator.Persistent);
                NativeArray<ushort>.Copy(negY.Data.Materials, buffers.NegY);
                data.NegY = buffers.NegY;
            }
            var posYCoord = new ChunkCoord(coord.X, coord.Y + 1, coord.Z);
            if (_active.TryGetValue(posYCoord, out var posY) && posY.Data.IsCreated && !IsChunkGenerating(posYCoord))
            {
                data.HasPosY = true;
                buffers.PosY = new NativeArray<ushort>(posY.Data.Materials.Length, Allocator.Persistent);
                NativeArray<ushort>.Copy(posY.Data.Materials, buffers.PosY);
                data.PosY = buffers.PosY;
            }
            var negZCoord = new ChunkCoord(coord.X, coord.Y, coord.Z - 1);
            if (_active.TryGetValue(negZCoord, out var negZ) && negZ.Data.IsCreated && !IsChunkGenerating(negZCoord))
            {
                data.HasNegZ = true;
                buffers.NegZ = new NativeArray<ushort>(negZ.Data.Materials.Length, Allocator.Persistent);
                NativeArray<ushort>.Copy(negZ.Data.Materials, buffers.NegZ);
                data.NegZ = buffers.NegZ;
            }
            var posZCoord = new ChunkCoord(coord.X, coord.Y, coord.Z + 1);
            if (_active.TryGetValue(posZCoord, out var posZ) && posZ.Data.IsCreated && !IsChunkGenerating(posZCoord))
            {
                data.HasPosZ = true;
                buffers.PosZ = new NativeArray<ushort>(posZ.Data.Materials.Length, Allocator.Persistent);
                NativeArray<ushort>.Copy(posZ.Data.Materials, buffers.PosZ);
                data.PosZ = buffers.PosZ;
            }

            buffers.Data = data;
            return buffers;
        }

        internal NeighborDataBuffers GatherNeighborCopiesLod(ChunkCoord coord, int lodStep, int lodSize, int srcSize)
        {
            var buffers = new NeighborDataBuffers();
            var data = new GreedyMesher.NeighborData();
            int lodCount = lodSize * lodSize * lodSize;

            var negXCoord = new ChunkCoord(coord.X - 1, coord.Y, coord.Z);
            if (_active.TryGetValue(negXCoord, out var negX) && negX.Data.IsCreated && !IsChunkGenerating(negXCoord))
            {
                data.HasNegX = true;
                buffers.NegX = new NativeArray<ushort>(lodCount, Allocator.Persistent);
                DownsampleMaterials(negX.Data.Materials, srcSize, lodStep, buffers.NegX);
                data.NegX = buffers.NegX;
            }
            var posXCoord = new ChunkCoord(coord.X + 1, coord.Y, coord.Z);
            if (_active.TryGetValue(posXCoord, out var posX) && posX.Data.IsCreated && !IsChunkGenerating(posXCoord))
            {
                data.HasPosX = true;
                buffers.PosX = new NativeArray<ushort>(lodCount, Allocator.Persistent);
                DownsampleMaterials(posX.Data.Materials, srcSize, lodStep, buffers.PosX);
                data.PosX = buffers.PosX;
            }
            var negYCoord = new ChunkCoord(coord.X, coord.Y - 1, coord.Z);
            if (_active.TryGetValue(negYCoord, out var negY) && negY.Data.IsCreated && !IsChunkGenerating(negYCoord))
            {
                data.HasNegY = true;
                buffers.NegY = new NativeArray<ushort>(lodCount, Allocator.Persistent);
                DownsampleMaterials(negY.Data.Materials, srcSize, lodStep, buffers.NegY);
                data.NegY = buffers.NegY;
            }
            var posYCoord = new ChunkCoord(coord.X, coord.Y + 1, coord.Z);
            if (_active.TryGetValue(posYCoord, out var posY) && posY.Data.IsCreated && !IsChunkGenerating(posYCoord))
            {
                data.HasPosY = true;
                buffers.PosY = new NativeArray<ushort>(lodCount, Allocator.Persistent);
                DownsampleMaterials(posY.Data.Materials, srcSize, lodStep, buffers.PosY);
                data.PosY = buffers.PosY;
            }
            var negZCoord = new ChunkCoord(coord.X, coord.Y, coord.Z - 1);
            if (_active.TryGetValue(negZCoord, out var negZ) && negZ.Data.IsCreated && !IsChunkGenerating(negZCoord))
            {
                data.HasNegZ = true;
                buffers.NegZ = new NativeArray<ushort>(lodCount, Allocator.Persistent);
                DownsampleMaterials(negZ.Data.Materials, srcSize, lodStep, buffers.NegZ);
                data.NegZ = buffers.NegZ;
            }
            var posZCoord = new ChunkCoord(coord.X, coord.Y, coord.Z + 1);
            if (_active.TryGetValue(posZCoord, out var posZ) && posZ.Data.IsCreated && !IsChunkGenerating(posZCoord))
            {
                data.HasPosZ = true;
                buffers.PosZ = new NativeArray<ushort>(lodCount, Allocator.Persistent);
                DownsampleMaterials(posZ.Data.Materials, srcSize, lodStep, buffers.PosZ);
                data.PosZ = buffers.PosZ;
            }

            buffers.Data = data;
            return buffers;
        }

        internal void DownsampleMaterials(NativeArray<ushort> source, int srcSize, int lodStep, NativeArray<ushort> dest)
        {
            if (!source.IsCreated || source.Length == 0 || lodStep <= 1)
            {
                if (source.IsCreated && dest.Length == source.Length)
                    NativeArray<ushort>.Copy(source, dest);
                return;
            }

            int lodSize = srcSize / lodStep;
            for (int z = 0; z < lodSize; z++)
            {
                int sz = z * lodStep;
                for (int y = 0; y < lodSize; y++)
                {
                    int sy = y * lodStep;
                    for (int x = 0; x < lodSize; x++)
                    {
                        int sx = x * lodStep;
                        int dstIndex = x + lodSize * (y + lodSize * z);
                        ushort material = 0;
                        int maxX = Mathf.Min(srcSize, sx + lodStep);
                        int maxY = Mathf.Min(srcSize, sy + lodStep);
                        int maxZ = Mathf.Min(srcSize, sz + lodStep);
                        bool found = false;
                        for (int zz = sz; zz < maxZ && !found; zz++)
                        {
                            for (int yy = sy; yy < maxY && !found; yy++)
                            {
                                int baseIndex = srcSize * (yy + srcSize * zz);
                                for (int xx = sx; xx < maxX; xx++)
                                {
                                    ushort m = source[baseIndex + xx];
                                    if (m != 0)
                                    {
                                        material = m;
                                        found = true;
                                        break;
                                    }
                                }
                            }
                        }
                        dest[dstIndex] = material;
                    }
                }
            }
        }

        internal void GetMeshMaterialSettings(Chunk chunk, out byte maxMaterialIndex, out byte fallbackMaterialIndex)
        {
            maxMaterialIndex = 255;
            fallbackMaterialIndex = 1;
            if (worldGen != null)
            {
                int defaultIndex = worldGen.DefaultMaterialIndex <= 0 ? 1 : Mathf.Clamp(worldGen.DefaultMaterialIndex, 1, 255);
                fallbackMaterialIndex = (byte)defaultIndex;
            }

            var binder = chunk != null ? chunk.GetComponent<VoxelMaterialBinder>() : null;
            if (binder != null && binder.Library != null)
            {
                if (binder.Library.TextureArray != null)
                {
                    int maxLayerIndex = Mathf.Clamp(binder.Library.TextureArray.depth - 1, 0, 255);
                    maxMaterialIndex = (byte)maxLayerIndex;
                }

                int fallbackIndex = Mathf.Clamp(binder.Library.DefaultLayerIndex, 0, maxMaterialIndex);
                fallbackMaterialIndex = (byte)fallbackIndex;
            }
        }

        internal void ProcessFaceRemeshQueue()
        {
            int count = 0;
            int guard = _faceRemeshQueue.Count;
            int limit = Mathf.Max(1, maxFaceRemeshPerFrame);
            int maxInFlight = CurrentMaxMeshJobsInFlight;

            while (_faceRemeshQueue.Count > 0 && count < limit && guard-- > 0)
            {
                if (BudgetExceeded()) break;
                if (_meshJobs.Count + _faceMeshJobs.Count >= maxInFlight) break;

                var coord = _faceRemeshQueue.Dequeue();
                _faceRemeshSet.Remove(coord);
                if (!_neighborDirtyFaces.TryGetValue(coord, out int faceMask))
                {
                    _neighborDirtyFaces.Remove(coord);
                    continue;
                }
                _neighborDirtyFaces.Remove(coord);

                if (!_active.TryGetValue(coord, out var chunk)) continue;
                if (_remeshSet.Contains(coord)) continue;
                if (_meshJobs.ContainsKey(coord)) continue;
                if (_faceMeshJobs.ContainsKey(coord)) continue;
                if (IsChunkGenerating(coord))
                {
                    _neighborDirtyFaces[coord] = faceMask;
                    if (_faceRemeshSet.Add(coord))
                        _faceRemeshQueue.Enqueue(coord);
                    continue;
                }
                if (IsInIntegrationSet(coord) || _pendingCachedMeshes.ContainsKey(coord))
                {
                    _remeshAfterIntegration.Add(coord);
                    continue;
                }
                if (chunk.UsesSvo || chunk.LodStep > 1)
                {
                    QueueRemesh(coord);
                    continue;
                }

                if (ScheduleFaceRemeshJobAsync(coord, chunk, faceMask))
                    count++;
                else
                {
                    _neighborDirtyFaces[coord] = faceMask;
                    if (_faceRemeshSet.Add(coord))
                        _faceRemeshQueue.Enqueue(coord);
                }
            }
        }

        internal void ProcessFaceMeshJobs()
        {
            if (_faceMeshJobs.Count == 0) return;

            var completed = new List<ChunkCoord>();
            foreach (var kvp in _faceMeshJobs)
            {
                if (kvp.Value.Job.Handle.IsCompleted)
                    completed.Add(kvp.Key);
            }

            foreach (var coord in completed)
            {
                if (!_faceMeshJobs.TryGetValue(coord, out var task)) continue;
                task.Job.Handle.Complete();
                _faceMeshJobs.Remove(coord);

                if (!_active.TryGetValue(coord, out var chunk) || chunk != task.Chunk)
                {
                    task.Job.Dispose();
                    continue;
                }

                chunk.ApplyMesh(task.Job.MeshData, addCollider: false);
                ReleaseFaceCacheForChunk(coord);
                task.Job.Dispose();
            }
        }

        internal bool ScheduleFaceRemeshJobAsync(ChunkCoord coord, Chunk chunk, int faceMask)
        {
            if (!chunk.Data.IsCreated) return false;
            if (_meshJobs.Count + _faceMeshJobs.Count >= CurrentMaxMeshJobsInFlight) return false;
            var neighbors = GatherNeighborCopies(coord);
            if (!HasAllNeighbors(neighbors.Data))
            {
                neighbors.Dispose();
                QueueRemesh(coord);
                return true;
            }

            GetMeshMaterialSettings(chunk, out var maxMaterialIndex, out var fallbackMaterialIndex);
            int chunkSize = chunk.Data.Size;
            float voxelScale = VoxelConstants.VoxelSize;

            var materialsCopy = new NativeArray<ushort>(chunk.Data.Materials.Length, Allocator.Persistent);
            NativeArray<ushort>.Copy(chunk.Data.Materials, materialsCopy);

            var dataForJob = default(ChunkData);
            dataForJob.Materials = materialsCopy;
            dataForJob.Size = chunkSize;

            var meshData = new MeshData(Allocator.Persistent);
            var mask = new NativeArray<GreedyMesher.MaskCell>(chunkSize * chunkSize, Allocator.Persistent);
            if (!_emptyMaterials.IsCreated)
                _emptyMaterials = new NativeArray<ushort>(0, Allocator.Persistent);

            var handle = GreedyMesher.Schedule(dataForJob, neighbors.Data, maxMaterialIndex, fallbackMaterialIndex, mask, _emptyMaterials, ref meshData, voxelScale, GreedyMesher.FaceMaskAll, enableSeamSkirts, seamSkirtOffset);

            var job = new FaceMeshJobHandle
            {
                Handle = handle,
                MeshData = meshData,
                MaterialsCopy = materialsCopy,
                Mask = mask,
                Neighbors = neighbors
            };

            _faceMeshJobs[coord] = new FaceMeshTask
            {
                Coord = coord,
                Chunk = chunk,
                Job = job,
                FaceMask = faceMask
            };

            return true;
        }

        internal void ProcessRemeshQueue()
        {
            if (_remeshSet.Count == 0) return;
            if (player == null || worldGen == null) return;
            ChunkCoord center = PlayerTracker.WorldToChunk(player.position, worldGen.ChunkSize);

            int count = 0;
            int guard = _remeshSet.Count;
            while (_remeshSet.Count > 0 && count < maxRemeshPerFrame && guard-- > 0)
            {
                if (BudgetExceeded()) break;
                if (_meshJobs.Count >= CurrentMaxMeshJobsInFlight) break;
                if (!TryDequeueClosestRemesh(center, out var coord))
                    break;

                if (!_active.ContainsKey(coord)) continue;
                if (_meshJobs.ContainsKey(coord)) continue;
                if (IsChunkGenerating(coord))
                {
                    _remeshSet.Add(coord);
                    continue;
                }
                if (IsInIntegrationSet(coord) || _pendingCachedMeshes.ContainsKey(coord))
                {
                    _remeshAfterIntegration.Add(coord);
                    continue;
                }

                int remeshLodStep = GetInitialLodStep(coord);
                if (ScheduleMeshForChunk(coord, 0, remeshLodStep))
                {
                    count++;
                }
                else
                    _remeshSet.Add(coord);
            }
        }
    }
}
