using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Streaming;
using UnityEngine;

namespace TerraVoxel.Voxel.Lod
{
    /// <summary>Streaming-side LOD: implements ProcessFullLod, ProcessLodUpgrades; delegates ProcessFarRangeLod, GetInitialLodStep to ChunkManager. Runs on main thread only; no synchronization required.</summary>
    internal sealed class ChunkLodManager
    {
        readonly ChunkManager.Context _ctx;

        public ChunkLodManager(ChunkManager.Context ctx)
        {
            _ctx = ctx;
        }

        internal void ProcessFullLod()
        {
            if (!_ctx.EnableFullLod) return;
            if (_ctx.LodSettings == null) return;
            if (_ctx.Player == null || _ctx.WorldGen == null) return;

            ChunkCoord center = PlayerTracker.WorldToChunk(_ctx.Player.position, _ctx.WorldGen.ChunkSize);
            int transitions = 0;
            int svoBuilds = 0;
            double now = Time.realtimeSinceStartupAsDouble;
            int transitionLimit = _ctx.MaxLodTransitionsPerFrame;
            if (_ctx.ScaleJobsByProcessorCount)
                transitionLimit = Mathf.Max(transitionLimit, _ctx.CurrentMaxMeshJobsInFlight * 2);
            transitionLimit = Mathf.Min(transitionLimit, 64);

            var upgrades = new List<(ChunkCoord coord, Chunk chunk, int dist, ChunkLodLevel desired, ChunkLodLevel current)>();
            var downgrades = new List<(ChunkCoord coord, Chunk chunk, int dist, ChunkLodLevel desired, ChunkLodLevel current)>();

            foreach (var kvp in _ctx.Active)
            {
                if (_ctx.BudgetExceeded()) break;
                var coord = kvp.Key;
                var chunk = kvp.Value;
                if (chunk == null) continue;
                if (_ctx.Preloaded.Contains(coord)) continue;
                if (_ctx.IsChunkBusy(coord) || _ctx.Integration.IsInIntegrationSet(coord) || _ctx.PendingCachedMeshes.ContainsKey(coord)) continue;

                int dist = Mathf.Max(0, Mathf.Max(Mathf.Abs(coord.X - center.X), Mathf.Abs(coord.Z - center.Z)));
                ChunkLodMode currentMode = chunk.UsesSvo ? ChunkLodMode.Svo : ChunkLodMode.Mesh;
                int currentStep = Mathf.Max(1, chunk.LodStep);
                var desired = _ctx.LodSettings.ResolveLevel(dist, currentStep, currentMode);
                if (desired.Mode == currentMode && desired.LodStep == currentStep) continue;

                var currentLevel = new ChunkLodLevel { MinDistance = 0, MaxDistance = int.MaxValue, LodStep = currentStep, Hysteresis = 0, Mode = currentMode };
                bool isUpgrade = _ctx.LodSettings.GetDetailRankFor(desired) < _ctx.LodSettings.GetDetailRankFor(currentLevel);
                if (!isUpgrade && _ctx.LodTransitionCooldown > 0f && now - chunk.LodStartTime < _ctx.LodTransitionCooldown) continue;

                if (isUpgrade)
                    upgrades.Add((coord, chunk, dist, desired, currentLevel));
                else
                    downgrades.Add((coord, chunk, dist, desired, currentLevel));
            }

            upgrades.Sort((a, b) => a.dist.CompareTo(b.dist));
            downgrades.Sort((a, b) => b.dist.CompareTo(a.dist));

            foreach (var t in upgrades)
            {
                if (transitions >= transitionLimit) break;
                if (_ctx.BudgetExceeded()) break;
                if (_ctx.MeshJobs.Count >= _ctx.CurrentMaxMeshJobsInFlight) break;
                var (coord, chunk, dist, desired, currentLevel) = t;
                if (chunk == null) continue;
                if (_ctx.IsChunkBusy(coord) || _ctx.Integration.IsInIntegrationSet(coord) || _ctx.PendingCachedMeshes.ContainsKey(coord)) continue;

                if (_ctx.EnableLodTransitionLog)
                    Debug.Log($"[ChunkManager] LOD upgrade: Dist={dist}, Current Step={currentLevel.LodStep} Mode={currentLevel.Mode}, Target Step={desired.LodStep} Mode={desired.Mode}");

                if (desired.Mode == ChunkLodMode.None)
                {
                    chunk.SetRendererEnabled(false);
                    chunk.SetColliderEnabled(false);
                    chunk.UsesSvo = false;
                    chunk.LodStep = desired.LodStep;
                    chunk.IsLowLod = true;
                    chunk.LodStartTime = now;
                    transitions++;
                    continue;
                }

                if (desired.Mode == ChunkLodMode.Svo)
                {
                    if (_ctx.SvoManager == null) continue;
                    if (svoBuilds >= _ctx.MaxSvoBuildsPerFrame) continue;
                    _ctx.GetMeshMaterialSettings(chunk, out var maxMaterialIndex, out var fallbackMaterialIndex);
                    if (_ctx.SvoManager.TryGetOrBuildMesh(coord, chunk.Data, desired.LodStep, maxMaterialIndex, fallbackMaterialIndex, out var svoMesh))
                    {
                        chunk.ApplySharedMesh(svoMesh, addCollider: false);
                        if (_ctx.SrpBatchingConfig != null) _ctx.SrpBatchingConfig.ApplyToChunk(chunk);
                        else if (_ctx.VoxelMaterial != null) chunk.SetSharedMaterial(_ctx.VoxelMaterial);
                        chunk.UsesSvo = true;
                        chunk.LodStep = desired.LodStep;
                        chunk.IsLowLod = true;
                        chunk.LodStartTime = now;
                        transitions++;
                        svoBuilds++;
                    }
                    continue;
                }

                if (_ctx.ScheduleMeshForChunk(coord, 0, desired.LodStep))
                {
                    chunk.UsesSvo = false;
                    chunk.LodStep = desired.LodStep;
                    chunk.IsLowLod = desired.LodStep > 1;
                    chunk.LodStartTime = now;
                    transitions++;
                }
            }

            foreach (var t in downgrades)
            {
                if (transitions >= transitionLimit) break;
                if (_ctx.BudgetExceeded()) break;
                if (_ctx.MeshJobs.Count >= _ctx.CurrentMaxMeshJobsInFlight) break;
                var (coord, chunk, dist, desired, currentLevel) = t;
                if (chunk == null) continue;
                if (_ctx.IsChunkBusy(coord) || _ctx.Integration.IsInIntegrationSet(coord) || _ctx.PendingCachedMeshes.ContainsKey(coord)) continue;

                if (_ctx.EnableLodTransitionLog)
                    Debug.Log($"[ChunkManager] LOD downgrade: Dist={dist}, Current Step={currentLevel.LodStep} Mode={currentLevel.Mode}, Target Step={desired.LodStep} Mode={desired.Mode}");

                if (desired.Mode == ChunkLodMode.None)
                {
                    chunk.SetRendererEnabled(false);
                    chunk.SetColliderEnabled(false);
                    chunk.UsesSvo = false;
                    chunk.LodStep = desired.LodStep;
                    chunk.IsLowLod = true;
                    chunk.LodStartTime = now;
                    transitions++;
                    continue;
                }

                if (desired.Mode == ChunkLodMode.Svo)
                {
                    if (_ctx.SvoManager == null) continue;
                    if (svoBuilds >= _ctx.MaxSvoBuildsPerFrame) continue;
                    _ctx.GetMeshMaterialSettings(chunk, out var maxMaterialIndex, out var fallbackMaterialIndex);
                    if (_ctx.SvoManager.TryGetOrBuildMesh(coord, chunk.Data, desired.LodStep, maxMaterialIndex, fallbackMaterialIndex, out var svoMesh))
                    {
                        chunk.ApplySharedMesh(svoMesh, addCollider: false);
                        if (_ctx.SrpBatchingConfig != null) _ctx.SrpBatchingConfig.ApplyToChunk(chunk);
                        else if (_ctx.VoxelMaterial != null) chunk.SetSharedMaterial(_ctx.VoxelMaterial);
                        chunk.UsesSvo = true;
                        chunk.LodStep = desired.LodStep;
                        chunk.IsLowLod = true;
                        chunk.LodStartTime = now;
                        transitions++;
                        svoBuilds++;
                    }
                    continue;
                }

                if (_ctx.ScheduleMeshForChunk(coord, 0, desired.LodStep))
                {
                    chunk.UsesSvo = false;
                    chunk.LodStep = desired.LodStep;
                    chunk.IsLowLod = desired.LodStep > 1;
                    chunk.LodStartTime = now;
                    transitions++;
                }
            }
        }

        internal void ProcessLodUpgrades()
        {
            if (!_ctx.EnableReverseLod) return;
            if (_ctx.ReverseLodStep <= 1) return;
            if (_ctx.ReverseLodUpgradeSeconds <= 0f) return;
            if (_ctx.MaxLodUpgradesPerFrame <= 0) return;
            if (_ctx.Player == null || _ctx.WorldGen == null) return;

            ChunkCoord center = PlayerTracker.WorldToChunk(_ctx.Player.position, _ctx.WorldGen.ChunkSize);
            int upgrades = 0;
            foreach (var kvp in _ctx.Active)
            {
                if (upgrades >= _ctx.MaxLodUpgradesPerFrame) break;
                if (_ctx.BudgetExceeded()) break;
                if (_ctx.MeshJobs.Count >= _ctx.CurrentMaxMeshJobsInFlight) break;

                var coord = kvp.Key;
                var chunk = kvp.Value;
                if (chunk == null) continue;

                bool isLowLod = chunk.IsLowLod;
                if (!isLowLod)
                {
                    Mesh mesh = chunk.GetRenderMesh();
                    if (mesh != null && mesh.vertexCount > 0)
                    {
                        int expectedFullVertices = chunk.Data.Size * chunk.Data.Size * 6 * 4;
                        if (mesh.vertexCount < expectedFullVertices * 0.3f)
                            isLowLod = true;
                    }
                    else if (mesh == null || mesh.vertexCount == 0)
                    {
                        _ctx.QueueRemesh(coord);
                        continue;
                    }
                }

                if (!isLowLod) continue;
                if (chunk.IsLowLod && _ctx.ReverseLodUpgradeSeconds > 0f && (Time.realtimeSinceStartupAsDouble - chunk.LodStartTime) < _ctx.ReverseLodUpgradeSeconds) continue;
                if (!_ctx.IsWithinLoadRadius(coord, center, _ctx.LoadRadius)) continue;
                if (_ctx.IsChunkBusy(coord)) continue;
                if (_ctx.Integration.IsInIntegrationSet(coord) || _ctx.PendingCachedMeshes.ContainsKey(coord)) continue;

                chunk.IsLowLod = false;
                chunk.LodStartTime = 0;
                if (_ctx.ScheduleMeshForChunk(coord, 0, 1))
                    upgrades++;
            }
        }

        internal void ProcessFarRangeLod() => _ctx.Owner.ProcessFarRangeLod();
        internal int GetInitialLodStep(ChunkCoord coord) => _ctx.Owner.GetInitialLodStep(coord);
    }
}
