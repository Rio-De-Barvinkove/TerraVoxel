using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Streaming;
using UnityEngine;

namespace TerraVoxel.Voxel.Occlusion
{
    /// <summary>
    /// Occlusion culling for chunk renderers: frustum and optional raycast.
    /// Uses fixed maxChecksPerFrame and tickBudgetMs; consider adaptive limits for highly variable load.
    /// </summary>
    [DisallowMultipleComponent]
    public class ChunkOcclusionCuller : MonoBehaviour
    {
        [SerializeField] bool enableOcclusion = true;
        [SerializeField] bool frustumCulling = true;
        [SerializeField] bool raycastOcclusion = false;
        [Tooltip("Fixed cap per frame; may cause uneven FPS with many chunks. Consider adaptive budget later.")]
        [SerializeField] int maxChecksPerFrame = 256;
        [SerializeField] LayerMask occluderMask = ~0;
        [Tooltip("Layer name for raycast; if missing, occluderMask is used (warning logged once).")]
        [SerializeField] string occluderLayerName = "Terrain";
        [Tooltip("Padding along ray; fixed value may not suit all chunk scales.")]
        [SerializeField] float raycastPadding = 0.1f;
        [Tooltip("When true, preloaded chunks are skipped for culling unless cullPreloaded is true.")]
        [SerializeField] bool ignorePreloaded = true;
        [Tooltip("When true, preloaded chunks can be occluded; when false and ignorePreloaded true, they are neither tested nor culled.")]
        [SerializeField] bool cullPreloaded = false;
        [SerializeField] float maxRayDist = 200f;
        [Tooltip("Max ms per Tick; fixed budget. System overload may cause timing drift.")]
        [SerializeField] float tickBudgetMs = 5f;

        struct Candidate
        {
            public ChunkCoord Coord;
            public float DistSq;
            public Chunk Chunk;
        }

        readonly HashSet<ChunkCoord> _occluded = new HashSet<ChunkCoord>();
        readonly List<Candidate> _candidates = new List<Candidate>(256);
        readonly HashSet<ChunkCoord> _activeCoordsThisTick = new HashSet<ChunkCoord>();
        readonly object _occludedLock = new object();
        static readonly Vector3[] BoundsCorners = new Vector3[8];
        bool _wasEnabled;
        float _maxRayDistSq;
        static bool _warnedLayerMissing;

        void OnValidate()
        {
            _maxRayDistSq = maxRayDist * maxRayDist;
        }

        void Awake()
        {
            _maxRayDistSq = maxRayDist * maxRayDist;
        }

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

            Plane[] planes = frustumCulling ? GeometryUtility.CalculateFrustumPlanes(cam) : null;
            Vector3 camPos = cam.transform.position;

            LayerMask raycastMask = GetRaycastMask();

            _candidates.Clear();
            _activeCoordsThisTick.Clear();
            var activeChunks = manager.ActiveChunks;
            if (activeChunks == null) return;

            foreach (var kvp in activeChunks)
            {
                if (kvp.Value == null) continue;
                if (ignorePreloaded && !cullPreloaded && manager.IsPreloaded(kvp.Key)) continue;

                Vector3 pos = kvp.Value.transform.position;
                float distSq = (pos - camPos).sqrMagnitude;
                _candidates.Add(new Candidate { Coord = kvp.Key, DistSq = distSq, Chunk = kvp.Value });
                _activeCoordsThisTick.Add(kvp.Key);
            }

            lock (_occludedLock)
            {
                _occluded.RemoveWhere(c => !_activeCoordsThisTick.Contains(c));
            }

            _candidates.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));

            float startTime = Time.realtimeSinceStartup;
            float budgetSec = tickBudgetMs > 0 ? tickBudgetMs * 0.001f : float.MaxValue;
            int checks = 0;

            for (int i = 0; i < _candidates.Count; i++)
            {
                if (maxChecksPerFrame > 0 && checks >= maxChecksPerFrame) break;
                if (Time.realtimeSinceStartup - startTime > budgetSec) break;

                var candidate = _candidates[i];
                var coord = candidate.Coord;
                var chunk = candidate.Chunk;

                Bounds bounds = GetChunkBounds(chunk, manager.ChunkSize);

                bool visible = true;
                if (frustumCulling && planes != null && !GeometryUtility.TestPlanesAABB(planes, bounds))
                    visible = false;

                if (visible && raycastOcclusion && !chunk.UsesSvo && chunk.LodStep <= 1)
                {
                    if (candidate.DistSq <= _maxRayDistSq)
                    {
                        if (!AnyRayUnblocked(camPos, bounds, raycastMask))
                            visible = false;
                    }
                }

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
        }

        LayerMask GetRaycastMask()
        {
            if (string.IsNullOrEmpty(occluderLayerName)) return occluderMask;
            int layer = LayerMask.NameToLayer(occluderLayerName);
            if (layer < 0)
            {
                if (!_warnedLayerMissing)
                {
                    _warnedLayerMissing = true;
                    Debug.LogWarning($"[ChunkOcclusionCuller] Layer '{occluderLayerName}' not found; using occluderMask.");
                }
                return occluderMask;
            }
            return (LayerMask)(1 << layer);
        }

        /// <summary>Tests 8 corners + center only; partial visibility through gaps may be missed. raycastPadding is fixed.</summary>
        bool AnyRayUnblocked(Vector3 camPos, Bounds bounds, LayerMask mask)
        {
            FillBoundsCorners(bounds);
            float padding = raycastPadding;
            for (int i = 0; i < 8; i++)
            {
                Vector3 target = BoundsCorners[i];
                Vector3 dir = target - camPos;
                float dist = dir.magnitude;
                if (dist <= padding) return true;
                dir /= dist;
                if (!Physics.Raycast(camPos, dir, dist - padding, mask, QueryTriggerInteraction.Ignore))
                    return true;
            }
            Vector3 centerDir = bounds.center - camPos;
            float centerDist = centerDir.magnitude;
            if (centerDist <= padding) return true;
            centerDir /= centerDist;
            if (!Physics.Raycast(camPos, centerDir, centerDist - padding, mask, QueryTriggerInteraction.Ignore))
                return true;
            return false;
        }

        static void FillBoundsCorners(Bounds b)
        {
            Vector3 min = b.min;
            Vector3 max = b.max;
            BoundsCorners[0] = new Vector3(min.x, min.y, min.z);
            BoundsCorners[1] = new Vector3(max.x, min.y, min.z);
            BoundsCorners[2] = new Vector3(max.x, max.y, min.z);
            BoundsCorners[3] = new Vector3(min.x, max.y, min.z);
            BoundsCorners[4] = new Vector3(min.x, min.y, max.z);
            BoundsCorners[5] = new Vector3(max.x, min.y, max.z);
            BoundsCorners[6] = new Vector3(max.x, max.y, max.z);
            BoundsCorners[7] = new Vector3(min.x, max.y, max.z);
        }

        /// <summary>Uses mesh bounds when valid and non-empty; otherwise fallback from chunkSize (may be inaccurate for culling).</summary>
        Bounds GetChunkBounds(Chunk chunk, int chunkSize)
        {
            if (chunk == null)
            {
                float size = chunkSize * VoxelConstants.VoxelSize;
                return new Bounds(Vector3.zero, new Vector3(size, size, size));
            }
            Mesh mesh = chunk.GetRenderMesh();
            if (mesh != null && mesh.vertexCount > 0 && mesh.bounds.size.sqrMagnitude > 0.0001f)
            {
                Vector3 center = chunk.transform.position + mesh.bounds.center;
                return new Bounds(center, mesh.bounds.size);
            }
            float sizeF = chunkSize * VoxelConstants.VoxelSize;
            Vector3 fallbackCenter = chunk.transform.position + new Vector3(sizeF * 0.5f, sizeF * 0.5f, sizeF * 0.5f);
            return new Bounds(fallbackCenter, new Vector3(sizeF, sizeF, sizeF));
        }

        /// <summary>Restores renderers for all occluded coords. Chunks already unloaded are skipped (TryGetChunk returns false).</summary>
        void RestoreAll(ChunkManager manager)
        {
            List<ChunkCoord> toRestore;
            lock (_occludedLock)
            {
                toRestore = new List<ChunkCoord>(_occluded);
                _occluded.Clear();
            }
            if (manager == null) return;
            foreach (var coord in toRestore)
            {
                if (manager.TryGetChunk(coord, out var chunk) && chunk != null)
                    chunk.SetRendererEnabled(true);
            }
        }
    }
}
