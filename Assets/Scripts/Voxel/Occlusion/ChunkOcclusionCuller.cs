using System;
using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Streaming;
using UnityEngine;

namespace TerraVoxel.Voxel.Occlusion
{
    /// <summary>
    /// Occlusion culling for chunk renderers: frustum and optional raycast.
    /// Optional adaptive limits (useAdaptiveBudget): effectiveMaxChecks is adjusted each frame from previous Tick duration vs adaptiveTargetMs.
    /// Optional dynamic raycast padding (scalePaddingByBounds): padding scales from bounds.size and is clamped to avoid zero or excessive values.
    /// Raycast occlusion is applied only to full-detail mesh chunks (!UsesSvo &amp;&amp; LodStep &lt;= 1); LOD/SVO chunks are skipped for consistent bounds and performance.
    /// Camera.main and manager are null-checked in Tick. _candidates is reused (Clear + Add each Tick) to avoid per-frame list allocation. Physics errors in AnyRayUnblocked are caught and logged once (chunk treated as visible).
    /// </summary>
    [DisallowMultipleComponent]
    public class ChunkOcclusionCuller : MonoBehaviour
    {
        [SerializeField] bool enableOcclusion = true;
        [SerializeField] bool frustumCulling = true;
        [SerializeField] bool raycastOcclusion = false;
        [Tooltip("Cap per frame when useAdaptiveBudget is false; when true, effective cap is adjusted from previous Tick time vs adaptiveTargetMs.")]
        [SerializeField] int maxChecksPerFrame = 256;
        [Tooltip("Reserve this many checks per frame to re-test occluded chunks so they can become visible again when the player looks back. Prevents distant 'holes' after returning.")]
        [SerializeField] int recheckOccludedPerFrame = 64;
        [SerializeField] LayerMask occluderMask = ~0;
        [Tooltip("Layer name for raycast; if missing, occluderMask is used (warning logged once).")]
        [SerializeField] string occluderLayerName = "Terrain";
        [Tooltip("Padding along ray. When scalePaddingByBounds is true, actual padding is scaled by bounds size (clamped to min/max).")]
        [SerializeField] float raycastPadding = 0.1f;
        [Tooltip("When true, padding is scaled by bounds.size.magnitude * paddingScaleFactor and clamped to avoid zero or excessive values.")]
        [SerializeField] bool scalePaddingByBounds = false;
        [Tooltip("Multiplier applied to bounds.size.magnitude when scalePaddingByBounds is true.")]
        [SerializeField] float paddingScaleFactor = 1f;
        const float MinPadding = 0.01f;
        const float MaxPadding = 2f;
        [Tooltip("When true, preloaded chunks are skipped for culling unless cullPreloaded is true.")]
        [SerializeField] bool ignorePreloaded = true;
        [Tooltip("When true, preloaded chunks can be occluded; when false and ignorePreloaded true, they are neither tested nor culled.")]
        [SerializeField] bool cullPreloaded = false;
        [SerializeField] float maxRayDist = 200f;
        [Tooltip("Max ms per Tick; fixed budget. System overload may cause timing drift.")]
        [SerializeField] float tickBudgetMs = 5f;
        [Tooltip("When true, max checks per frame is adjusted each frame based on previous Tick duration vs adaptiveTargetMs to reduce FPS spikes.")]
        [SerializeField] bool useAdaptiveBudget = false;
        [Tooltip("Target ms per Tick when useAdaptiveBudget is true; over budget reduces effectiveMaxChecks, under budget increases it.")]
        [SerializeField] float adaptiveTargetMs = 2f;
        [Tooltip("Minimum checks per frame when using adaptive budget.")]
        [SerializeField] int adaptiveMinChecks = 32;
        [Tooltip("Maximum checks per frame when using adaptive budget.")]
        [SerializeField] int adaptiveMaxChecks = 512;
        [Tooltip("If true, skip raycasts when no occluder in chunk bounds sphere (reduces raycasts when chunk is clearly visible).")]
        [SerializeField] bool useCoarseSphereCheck = false;

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
        readonly List<ChunkCoord> _restoreBuffer = new List<ChunkCoord>(256);
        static readonly Vector3[] BoundsCorners = new Vector3[8];
        static readonly float[] CornerDistSq = new float[8];
        static readonly int[] CornerIndices = new int[8];
        static readonly Vector3[] TempCorners = new Vector3[8];
        static readonly Comparer<int> CornerDistComparer = Comparer<int>.Create((a, b) => CornerDistSq[a].CompareTo(CornerDistSq[b]));
        bool _wasEnabled;
        float _maxRayDistSq;
        float _lastTickMs;
        int _effectiveMaxChecks;
        static bool _warnedLayerMissing;
        static bool _warnedLayerEmpty;
        static bool _warnedPhysicsError;

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

            int effectiveMaxChecks = maxChecksPerFrame;
            if (useAdaptiveBudget)
            {
                int baseVal = _effectiveMaxChecks > 0 ? _effectiveMaxChecks : maxChecksPerFrame;
                if (_lastTickMs > 0f)
                {
                    if (_lastTickMs > adaptiveTargetMs)
                        baseVal = (int)(baseVal * 0.8f);
                    else
                        baseVal = (int)(baseVal * 1.1f) + 1;
                }
                effectiveMaxChecks = Mathf.Clamp(baseVal, adaptiveMinChecks, adaptiveMaxChecks);
            }

            int recheckBudget = Mathf.Clamp(recheckOccludedPerFrame, 0, effectiveMaxChecks > 0 ? effectiveMaxChecks : int.MaxValue);
            int mainBudget = effectiveMaxChecks > 0 ? Mathf.Max(0, effectiveMaxChecks - recheckBudget) : int.MaxValue;

            float startTime = Time.realtimeSinceStartup;
            float budgetSec = tickBudgetMs > 0 ? tickBudgetMs * 0.001f : float.MaxValue;
            int checks = 0;

            for (int i = 0; i < _candidates.Count; i++)
            {
                if (mainBudget > 0 && checks >= mainBudget) break;
                if (effectiveMaxChecks > 0 && checks >= effectiveMaxChecks) break;
                if (Time.realtimeSinceStartup - startTime > budgetSec) break;

                var candidate = _candidates[i];
                var coord = candidate.Coord;
                var chunk = candidate.Chunk;

                Bounds bounds = GetChunkBounds(chunk, manager.ChunkSize);

                bool visible = true;
                if (frustumCulling && planes != null)
                {
                    if (!GeometryUtility.TestPlanesAABB(planes, bounds))
                        visible = false;
                }

                // Raycast occlusion only for full-detail mesh chunks; LOD/SVO chunks skipped for consistent bounds and performance.
                if (visible && raycastOcclusion && candidate.DistSq <= _maxRayDistSq && !chunk.UsesSvo && chunk.LodStep <= 1)
                {
                    if (!AnyRayUnblocked(camPos, bounds, raycastMask))
                        visible = false;
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

            // Re-check some occluded chunks so they can become visible again when the player looks back (avoids persistent "hole").
            if (recheckBudget > 0 && checks < effectiveMaxChecks)
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
                if (_restoreBuffer.Count == 0)
                {
                    RecordTickTiming(startTime, effectiveMaxChecks);
                    return;
                }
                foreach (var coord in _restoreBuffer)
                {
                    if (effectiveMaxChecks > 0 && checks >= effectiveMaxChecks) break;
                    if (Time.realtimeSinceStartup - startTime > budgetSec) break;
                    if (!manager.TryGetChunk(coord, out var chunk) || chunk == null) continue;

                    Bounds bounds = GetChunkBounds(chunk, manager.ChunkSize);
                    bool visible = true;
                    if (frustumCulling && planes != null)
                    {
                        if (!GeometryUtility.TestPlanesAABB(planes, bounds))
                            visible = false;
                    }
                    if (visible && raycastOcclusion)
                    {
                        float distSq = (chunk.transform.position - camPos).sqrMagnitude;
                        if (distSq <= _maxRayDistSq && !chunk.UsesSvo && chunk.LodStep <= 1)
                        {
                            if (!AnyRayUnblocked(camPos, bounds, raycastMask))
                                visible = false;
                        }
                    }
                    if (visible)
                    {
                        lock (_occludedLock) { _occluded.Remove(coord); }
                        chunk.SetRendererEnabled(true);
                        checks++;
                    }
                }
            }

            RecordTickTiming(startTime, effectiveMaxChecks);
        }

        void RecordTickTiming(float startTime, int effectiveMaxChecks)
        {
            float elapsedMs = (Time.realtimeSinceStartup - startTime) * 1000f;
            _lastTickMs = elapsedMs;
            if (useAdaptiveBudget)
            {
                int next = effectiveMaxChecks;
                if (_lastTickMs > adaptiveTargetMs)
                    next = (int)(next * 0.8f);
                else
                    next = (int)(next * 1.1f) + 1;
                _effectiveMaxChecks = Mathf.Clamp(next, adaptiveMinChecks, adaptiveMaxChecks);
            }
        }

        /// <summary>Returns layer mask for raycasts. If occluderLayerName is empty or layer not found, uses occluderMask and logs a warning once.</summary>
        LayerMask GetRaycastMask()
        {
            if (string.IsNullOrEmpty(occluderLayerName))
            {
                if (!_warnedLayerEmpty)
                {
                    _warnedLayerEmpty = true;
                    Debug.LogWarning("[ChunkOcclusionCuller] Occluder layer name is empty; using occluderMask.");
                }
                return occluderMask;
            }
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

        /// <summary>Tests 8 corners + center; partial visibility through gaps may be missed. Optional coarse CheckSphere skips raycasts when no occluder in bounds sphere; when occluder is present, only 4 nearest corners are raycast to reduce cost. On Physics exception returns true (assume visible) and logs once.</summary>
        bool AnyRayUnblocked(Vector3 camPos, Bounds bounds, LayerMask mask)
        {
            try
            {
                bool occluderInSphere = false;
                if (useCoarseSphereCheck)
                {
                    float r = bounds.extents.magnitude;
                    if (r > 0.0001f && !Physics.CheckSphere(bounds.center, r, mask, QueryTriggerInteraction.Ignore))
                        return true;
                    occluderInSphere = true;
                }
                FillBoundsCorners(bounds);
                float padding = raycastPadding;
                if (scalePaddingByBounds)
                {
                    float scaled = raycastPadding * (bounds.size.magnitude * paddingScaleFactor);
                    padding = Mathf.Clamp(scaled, MinPadding, MaxPadding);
                }
                int rayCount = 8;
                if (occluderInSphere)
                {
                    SortCornersByDistanceTo(camPos);
                    rayCount = 4;
                }
                for (int i = 0; i < rayCount; i++)
                {
                    Vector3 target = BoundsCorners[i];
                    Vector3 dir = target - camPos;
                    float dist = dir.magnitude;
                    if (dist <= padding) return true;
                    dir /= dist;
                    if (!Physics.Raycast(camPos, dir, dist - padding, mask, QueryTriggerInteraction.Ignore))
                        return true;
                }
                if (rayCount == 8)
                {
                    Vector3 centerDir = bounds.center - camPos;
                    float centerDist = centerDir.magnitude;
                    if (centerDist <= padding) return true;
                    centerDir /= centerDist;
                    if (!Physics.Raycast(camPos, centerDir, centerDist - padding, mask, QueryTriggerInteraction.Ignore))
                        return true;
                }
                return false;
            }
            catch (Exception e)
            {
                if (!_warnedPhysicsError)
                {
                    _warnedPhysicsError = true;
                    Debug.LogWarning($"[ChunkOcclusionCuller] Physics error in AnyRayUnblocked: {e.Message}. Treating chunk as visible.");
                }
                return true;
            }
        }

        /// <summary>Sorts static BoundsCorners in place by distance to origin (nearest first). O(8 log 8) via Array.Sort with static buffers; no per-call allocation.</summary>
        static void SortCornersByDistanceTo(Vector3 origin)
        {
            for (int i = 0; i < 8; i++)
            {
                CornerDistSq[i] = (BoundsCorners[i] - origin).sqrMagnitude;
                CornerIndices[i] = i;
            }
            Array.Sort(CornerIndices, 0, 8, CornerDistComparer);
            for (int i = 0; i < 8; i++)
                TempCorners[i] = BoundsCorners[CornerIndices[i]];
            for (int i = 0; i < 8; i++)
                BoundsCorners[i] = TempCorners[i];
        }

        /// <summary>Fills static BoundsCorners with the 8 corners of the bounds (min/max combinations).</summary>
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

        /// <summary>Uses mesh bounds when valid and non-empty; otherwise fallback from chunkSize. Returns fallback if chunk or chunk.transform is null/destroyed. Fallback size is clamped to avoid zero/negative bounds.</summary>
        Bounds GetChunkBounds(Chunk chunk, int chunkSize)
        {
            float sizeF = chunkSize > 0 ? chunkSize * VoxelConstants.VoxelSize : 1f;
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

        /// <summary>Restores renderers for all occluded coords. Chunks already unloaded are skipped (TryGetChunk returns false); no SetRendererEnabled on unloaded chunks. Reuses _restoreBuffer to avoid per-call allocation.</summary>
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
