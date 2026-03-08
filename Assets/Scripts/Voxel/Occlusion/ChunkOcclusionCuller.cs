using System;
using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Streaming;
using UnityEngine;

namespace TerraVoxel.Voxel.Occlusion
{
    /// <summary>
    /// Frustum culling for chunk renderers. Chunks outside camera frustum have renderer disabled.
    /// Re-check budget restores visibility when player looks back.
    /// </summary>
    [DisallowMultipleComponent]
    public class ChunkOcclusionCuller : MonoBehaviour
    {
        [SerializeField] bool enableOcclusion = true;
        [Tooltip("Max chunks tested per frame. Lower = less CPU, higher = faster visibility updates.")]
        [SerializeField] int maxChecksPerFrame = 256;
        [Tooltip("Chunks to re-test per frame so they become visible when player looks back.")]
        [SerializeField] int recheckOccludedPerFrame = 64;
        [Tooltip("Max ms per Tick. Stops early if exceeded.")]
        [SerializeField] float tickBudgetMs = 5f;
        [Tooltip("When false, preloaded chunks are skipped (not culled).")]
        [SerializeField] bool cullPreloaded = false;

        struct Candidate
        {
            public ChunkCoord Coord;
            public float DistSq;
            public Chunk Chunk;
        }

        static readonly Comparison<Candidate> CandidateComparer = (a, b) => a.DistSq.CompareTo(b.DistSq);

        readonly HashSet<ChunkCoord> _occluded = new HashSet<ChunkCoord>();
        readonly List<Candidate> _candidates = new List<Candidate>(256);
        readonly HashSet<ChunkCoord> _activeCoordsThisTick = new HashSet<ChunkCoord>();
        readonly object _occludedLock = new object();
        readonly List<ChunkCoord> _restoreBuffer = new List<ChunkCoord>(256);

        bool _wasEnabled;

        public void Tick(ChunkManager manager)
        {
            if (manager == null) return;

            if (!enableOcclusion)
            {
                if (_wasEnabled)
                    RestoreAll(manager);
                _wasEnabled = false;
                return;
            }

            _wasEnabled = true;

            Camera cam = Camera.main;
            if (cam == null) return;

            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(cam);
            Vector3 camPos = cam.transform.position;

            _candidates.Clear();
            _activeCoordsThisTick.Clear();
            var activeChunks = manager.ActiveChunks;
            if (activeChunks == null) return;

            foreach (var kvp in activeChunks)
            {
                if (kvp.Value == null) continue;
                if (!cullPreloaded && manager.IsPreloaded(kvp.Key)) continue;

                Vector3 pos = kvp.Value.transform.position;
                float distSq = (pos - camPos).sqrMagnitude;
                _candidates.Add(new Candidate { Coord = kvp.Key, DistSq = distSq, Chunk = kvp.Value });
                _activeCoordsThisTick.Add(kvp.Key);
            }

            lock (_occludedLock)
            {
                _occluded.RemoveWhere(c => !_activeCoordsThisTick.Contains(c));
            }

            _candidates.Sort(CandidateComparer);

            int recheckBudget = Mathf.Clamp(recheckOccludedPerFrame, 0, maxChecksPerFrame);
            int mainBudget = Mathf.Max(0, maxChecksPerFrame - recheckBudget);
            float startTime = Time.realtimeSinceStartup;
            float budgetSec = tickBudgetMs > 0 ? tickBudgetMs * 0.001f : float.MaxValue;
            int checks = 0;

            for (int i = 0; i < _candidates.Count; i++)
            {
                if (mainBudget > 0 && checks >= mainBudget) break;
                if (Time.realtimeSinceStartup - startTime > budgetSec) break;

                var candidate = _candidates[i];
                var coord = candidate.Coord;
                var chunk = candidate.Chunk;

                Bounds bounds = GetChunkBounds(chunk, manager);
                bool visible = GeometryUtility.TestPlanesAABB(planes, bounds);

                if (!visible)
                {
                    bool added;
                    lock (_occludedLock) { added = _occluded.Add(coord); }
                    if (added && chunk != null)
                        chunk.SetRendererEnabled(false);
                }
                else
                {
                    bool removed;
                    lock (_occludedLock) { removed = _occluded.Remove(coord); }
                    if (removed && chunk != null)
                        chunk.SetRendererEnabled(true);
                }
                checks++;
            }

            if (recheckBudget > 0 && checks < maxChecksPerFrame)
            {
                lock (_occludedLock)
                {
                    _restoreBuffer.Clear();
                    int n = 0;
                    foreach (var c in _occluded)
                    {
                        if (n >= recheckBudget) break;
                        if (!_activeCoordsThisTick.Contains(c)) continue;
                        _restoreBuffer.Add(c);
                        n++;
                    }
                }
                foreach (var coord in _restoreBuffer)
                {
                    if (checks >= maxChecksPerFrame) break;
                    if (Time.realtimeSinceStartup - startTime > budgetSec) break;
                    if (!manager.TryGetChunk(coord, out var chunk) || chunk == null) continue;

                    Bounds bounds = GetChunkBounds(chunk, manager);
                    if (GeometryUtility.TestPlanesAABB(planes, bounds))
                    {
                        lock (_occludedLock) { _occluded.Remove(coord); }
                        chunk.SetRendererEnabled(true);
                        checks++;
                    }
                }
            }
        }

        Bounds GetChunkBounds(Chunk chunk, ChunkManager manager)
        {
            int chunkSize = manager.ChunkSize;
            float voxelSize = manager.VoxelSize;
            float sizeF = chunkSize > 0 ? chunkSize * voxelSize : 1f;
            if (sizeF < 0.001f) sizeF = 0.001f;
            Vector3 fallbackSize = new Vector3(sizeF, sizeF, sizeF);
            if (chunk == null)
                return new Bounds(Vector3.zero, fallbackSize);
            if (chunk.transform == null)
                return new Bounds(Vector3.zero, fallbackSize);
            Mesh mesh = chunk.GetRenderMesh();
            if (mesh != null && mesh.vertexCount > 0 && mesh.bounds.size.sqrMagnitude > 0.0001f)
            {
                Vector3 center = chunk.transform.position + mesh.bounds.center;
                return new Bounds(center, mesh.bounds.size);
            }
            Vector3 fallbackCenter = chunk.transform.position + new Vector3(sizeF * 0.5f, sizeF * 0.5f, sizeF * 0.5f);
            return new Bounds(fallbackCenter, fallbackSize);
        }

        void RestoreAll(ChunkManager manager)
        {
            lock (_occludedLock)
            {
                _restoreBuffer.Clear();
                _restoreBuffer.AddRange(_occluded);
                _occluded.Clear();
            }
            if (manager == null) return;
            foreach (var coord in _restoreBuffer)
            {
                if (manager.TryGetChunk(coord, out var chunk) && chunk != null)
                    chunk.SetRendererEnabled(true);
            }
        }
    }
}
