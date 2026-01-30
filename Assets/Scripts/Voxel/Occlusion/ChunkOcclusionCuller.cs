using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Streaming;
using UnityEngine;

namespace TerraVoxel.Voxel.Occlusion
{
    [DisallowMultipleComponent]
    public class ChunkOcclusionCuller : MonoBehaviour
    {
        [SerializeField] bool enableOcclusion = true;
        [SerializeField] bool frustumCulling = true;
        [SerializeField] bool raycastOcclusion = false;
        [SerializeField] int maxChecksPerFrame = 256;
        [SerializeField] LayerMask occluderMask = ~0;
        [SerializeField] string occluderLayerName = "Terrain";
        [SerializeField] float raycastPadding = 0.1f;
        [SerializeField] bool ignorePreloaded = true;
        [SerializeField] bool cullPreloaded = false;
        [SerializeField] float maxRayDist = 200f;
        [SerializeField] float tickBudgetMs = 5f;

        struct Candidate
        {
            public ChunkCoord Coord;
            public float DistSq;
            public Chunk Chunk;
        }

        readonly HashSet<ChunkCoord> _occluded = new HashSet<ChunkCoord>();
        readonly List<Candidate> _candidates = new List<Candidate>(256);
        static readonly Vector3[] BoundsCorners = new Vector3[8];
        bool _wasEnabled;
        float _maxRayDistSq;

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
            foreach (var kvp in manager.ActiveChunks)
            {
                if (kvp.Value == null) continue;
                if (ignorePreloaded && !cullPreloaded && manager.IsPreloaded(kvp.Key)) continue;

                Vector3 pos = kvp.Value.transform.position;
                float distSq = (pos - camPos).sqrMagnitude;
                _candidates.Add(new Candidate { Coord = kvp.Key, DistSq = distSq, Chunk = kvp.Value });
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
                    if (_occluded.Add(coord))
                        chunk.SetRendererEnabled(false);
                }
                else if (_occluded.Remove(coord))
                {
                    chunk.SetRendererEnabled(true);
                }
                checks++;
            }
        }

        LayerMask GetRaycastMask()
        {
            if (string.IsNullOrEmpty(occluderLayerName)) return occluderMask;
            int layer = LayerMask.NameToLayer(occluderLayerName);
            if (layer < 0) return occluderMask;
            return (LayerMask)(1 << layer);
        }

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

        Bounds GetChunkBounds(Chunk chunk, int chunkSize)
        {
            Mesh mesh = chunk.GetRenderMesh();
            if (mesh != null && mesh.vertexCount > 0 && mesh.bounds.size.sqrMagnitude > 0.0001f)
            {
                Vector3 center = chunk.transform.position + mesh.bounds.center;
                return new Bounds(center, mesh.bounds.size);
            }
            float size = chunkSize * VoxelConstants.VoxelSize;
            Vector3 fallbackCenter = chunk.transform.position + new Vector3(size * 0.5f, size * 0.5f, size * 0.5f);
            return new Bounds(fallbackCenter, new Vector3(size, size, size));
        }

        void RestoreAll(ChunkManager manager)
        {
            foreach (var coord in _occluded)
            {
                if (manager.TryGetChunk(coord, out var chunk) && chunk != null)
                    chunk.SetRendererEnabled(true);
            }
            _occluded.Clear();
        }
    }
}
