/*
using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Generation;
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>
    /// Prioritizes pending chunk spawns by camera/view direction (view cone).
    /// Uses a priority queue (O(log n) dequeue) with score computed once at enqueue.
    /// </summary>
    [DisallowMultipleComponent]
    public class ChunkViewConePrioritizer : MonoBehaviour
    {
        [SerializeField] bool enable = true;
        [Tooltip("When true: strict priority = distance only (closer = higher). Ignores view cone and visual bonuses.")]
        [SerializeField] bool distanceOnly = true;
        [SerializeField] Transform viewTransform;
        [SerializeField] bool useMainCamera = true;
        [SerializeField] bool ignoreVertical = true;
        [SerializeField] bool includeVerticalDistance = false;
        [Tooltip("Half-angle of view cone in degrees. Clamped to [0, 180] at use.")]
        [SerializeField] [Range(0f, 180f)] float coneHalfAngleDeg = 70f;
        [Header("Visual Weights (not normalized; one may dominate)")]
        [Tooltip("Tune weights relative to each other; not auto-normalized.")]
        [SerializeField] bool useVisualWeights = true;
        [SerializeField] float dotWeight = 1f;
        [SerializeField] float distanceWeight = 1f;
        [SerializeField] float surfaceBonus = 0.5f;
        [SerializeField] float heightBonus = 0.25f;
        [SerializeField] float undergroundPenalty = 0.25f;
        [Tooltip("Optional: used for surface/height bonus. If null or invalid, that part of the score is skipped.")]
        [SerializeField] WorldGenConfig worldGen;

        /// <summary>Main-thread only; one-time warning when worldGen/BaseHeight fails in ComputeScore.</summary>
        static bool _warnedWorldGenScore;
        static bool _warnedViewNull;
        static bool _warnedVerticalConflict;

        /// <summary>Priority queue: higher score = dequeued first. Stored as (coord, score); heap ordered by score descending.</summary>
        readonly List<Entry> _heap = new List<Entry>(256);
        /// <summary>Min-heap by score for TryRemoveLowestPriority (O(log n) remove-lowest). Same entries as _heap.</summary>
        readonly List<Entry> _minHeap = new List<Entry>(256);
        const int HeapInitialCapacity = 256;

        struct Entry
        {
            public ChunkCoord Coord;
            public float Score;
        }

        /// <summary>Comparer for priority: higher score = higher priority (dequeue first).</summary>
        static int CompareEntryByScoreDescending(Entry a, Entry b) => a.Score.CompareTo(b.Score);

        /// <summary>Comparer for min-heap: lower score = higher priority (remove-lowest first).</summary>
        static int CompareEntryByScoreAscending(Entry a, Entry b) => a.Score.CompareTo(b.Score);

        public bool Enabled => enable;
        public bool DistanceOnly => distanceOnly;
        public int Count => _heap.Count;

        /// <summary>Score for a chunk (distance, view cone, surface band). Called once per enqueue; weights not normalized. When distanceOnly: score = 1/(1+dist), strict priority = distance (closer = higher).</summary>
        public float ComputeScore(ChunkCoord c, ChunkCoord center, Vector3 forward, bool includeVerticalDistance)
        {
            if (ignoreVertical && includeVerticalDistance && !_warnedVerticalConflict)
            {
                _warnedVerticalConflict = true;
                Debug.LogWarning("[ChunkViewConePrioritizer] ignoreVertical and includeVerticalDistance are both true; vertical is used in distance but ignored in view cone. Consider aligning settings.");
            }
            int dx = c.X - center.X;
            int dy = c.Y - center.Y;
            int dz = c.Z - center.Z;
            int distSq = dx * dx + dz * dz + (includeVerticalDistance ? dy * dy : 0);
            float dist = Mathf.Sqrt(distSq);
            if (distanceOnly)
                return 1f / (1f + dist);

            Vector3 to = new Vector3(dx, includeVerticalDistance ? dy : 0, dz);
            if (ignoreVertical)
                to.y = 0f;
            float dot = 1f;
            if (to.sqrMagnitude > 0.0001f)
            {
                to.Normalize();
                dot = Vector3.Dot(forward, to);
            }

            float halfAngle = Mathf.Clamp(coneHalfAngleDeg, 0f, 180f);
            float cosHalfAngle = Mathf.Cos(halfAngle * Mathf.Deg2Rad);
            bool inCone = dot >= cosHalfAngle;

            if (!useVisualWeights)
            {
                float distanceScore = 1f / (1f + dist);
                float viewScore = inCone ? (dot + 1f) * 0.5f : 0f;
                return distanceScore * distanceWeight + viewScore * dotWeight;
            }

            // Normalized: distanceScore in (0,1], viewScore in [0,1], visualScore in [0,1] or small penalty
            float distanceScoreNorm = 1f / (1f + dist);
            float viewScoreNorm = (dot + 1f) * 0.5f; // [0,1]
            if (inCone)
                viewScoreNorm = Mathf.Max(viewScoreNorm, 0.5f); // boost chunks barely in cone

            float visualScoreNorm = 0f;
            if (worldGen != null)
            {
                try
                {
                    float baseHeight = worldGen.BaseHeight;
                    if (float.IsNaN(baseHeight) || baseHeight < 0f)
                        baseHeight = 0f;
                    int surfaceY = Mathf.RoundToInt(baseHeight / VoxelConstants.ChunkSize);
                    int absDy = Mathf.Abs(c.Y - surfaceY);
                    if (absDy <= 1)
                        visualScoreNorm += surfaceBonus; // does not check chunk visibility
                    else if (dy > 0)
                        visualScoreNorm += heightBonus;
                    else if (dy < 0)
                        visualScoreNorm -= undergroundPenalty;
                }
                catch (System.Exception e)
                {
                    if (!_warnedWorldGenScore)
                    {
                        _warnedWorldGenScore = true;
                        Debug.LogWarning($"[ChunkViewConePrioritizer] worldGen/BaseHeight error in ComputeScore: {e.Message}. Skipping surface/height bonus.");
                    }
                }
            }
            visualScoreNorm = Mathf.Clamp01(visualScoreNorm + 0.5f) - 0.5f; // keep small range around 0

            return distanceScoreNorm * distanceWeight + viewScoreNorm * dotWeight + visualScoreNorm;
        }

        /// <summary>Add a coord to the priority queue; score is computed once here (center/forward from player).</summary>
        public void EnqueueWithPriority(ChunkCoord coord, ChunkCoord center, Transform player)
        {
            if (!enable) return;
            Transform view = ResolveViewTransform(player);
            if (view == null) return;
            Vector3 forward = view.forward;
            if (ignoreVertical)
                forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                forward = Vector3.forward;
            else
                forward.Normalize();

            float score = ComputeScore(coord, center, forward, includeVerticalDistance);
            Enqueue(coord, score);
        }

        void Enqueue(ChunkCoord coord, float score)
        {
            var entry = new Entry { Coord = coord, Score = score };
            _heap.Add(entry);
            HeapBubbleUp(_heap.Count - 1);
            _minHeap.Add(entry);
            MinHeapBubbleUp(_minHeap.Count - 1);
        }

        /// <summary>Dequeue the coord with highest score. Returns false if queue empty (no fallback to random coord).</summary>
        public bool TryDequeue(out ChunkCoord coord)
        {
            if (_heap.Count == 0)
            {
                coord = default;
                return false;
            }
            coord = _heap[0].Coord;
            int last = _heap.Count - 1;
            _heap[0] = _heap[last];
            _heap.RemoveAt(last);
            if (_heap.Count > 0)
                HeapBubbleDown(0);
            RemoveCoordFromMinHeap(coord);
            return true;
        }

        /// <summary>Remove the lowest-priority entry (smallest score). O(log n) via min-heap.</summary>
        public bool TryRemoveLowestPriority(out ChunkCoord coord)
        {
            coord = default;
            if (_minHeap.Count == 0) return false;
            coord = _minHeap[0].Coord;
            int last = _minHeap.Count - 1;
            _minHeap[0] = _minHeap[last];
            _minHeap.RemoveAt(last);
            if (_minHeap.Count > 0)
                MinHeapBubbleDown(0);
            RemoveCoordFromHeap(coord);
            return true;
        }

        void RemoveCoordFromMinHeap(ChunkCoord coord)
        {
            for (int i = 0; i < _minHeap.Count; i++)
            {
                if (!_minHeap[i].Coord.Equals(coord)) continue;
                int last = _minHeap.Count - 1;
                if (i == last)
                {
                    _minHeap.RemoveAt(last);
                    return;
                }
                _minHeap[i] = _minHeap[last];
                _minHeap.RemoveAt(last);
                MinHeapBubbleDown(i);
                MinHeapBubbleUp(i);
                return;
            }
        }

        void RemoveCoordFromHeap(ChunkCoord coord)
        {
            for (int i = 0; i < _heap.Count; i++)
            {
                if (!_heap[i].Coord.Equals(coord)) continue;
                int last = _heap.Count - 1;
                if (i == last)
                {
                    _heap.RemoveAt(last);
                    return;
                }
                _heap[i] = _heap[last];
                _heap.RemoveAt(last);
                HeapBubbleDown(i);
                HeapBubbleUp(i);
                return;
            }
        }

        /// <summary>True if chunk is within the view cone from center. Forward/to normalized per call (no shared cache).</summary>
        public bool IsInViewCone(ChunkCoord c, ChunkCoord center, Transform player)
        {
            if (!enable) return true;
            Transform view = ResolveViewTransform(player);
            if (view == null) return true;
            Vector3 forward = view.forward;
            if (ignoreVertical) forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
            else forward.Normalize();
            int dx = c.X - center.X;
            int dy = c.Y - center.Y;
            int dz = c.Z - center.Z;
            Vector3 to = new Vector3(dx, includeVerticalDistance ? dy : 0, dz);
            if (ignoreVertical) to.y = 0f;
            if (to.sqrMagnitude < 0.0001f) return true;
            to.Normalize();
            float dot = Vector3.Dot(forward, to);
            float cosHalfAngle = Mathf.Cos(Mathf.Clamp(coneHalfAngleDeg, 0f, 180f) * Mathf.Deg2Rad);
            return dot >= cosHalfAngle;
        }

        /// <summary>Clear the queue and trim capacity to avoid unbounded growth.</summary>
        public void Clear()
        {
            _heap.Clear();
            _minHeap.Clear();
            if (_heap.Capacity > HeapInitialCapacity)
                _heap.Capacity = HeapInitialCapacity;
            if (_minHeap.Capacity > HeapInitialCapacity)
                _minHeap.Capacity = HeapInitialCapacity;
        }

        void HeapBubbleUp(int i)
        {
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (CompareEntryByScoreDescending(_heap[i], _heap[parent]) <= 0)
                    break;
                Swap(i, parent);
                i = parent;
            }
        }

        void HeapBubbleDown(int i)
        {
            int count = _heap.Count;
            while (true)
            {
                int left = 2 * i + 1;
                int right = 2 * i + 2;
                int best = i;
                if (left < count && CompareEntryByScoreDescending(_heap[left], _heap[best]) > 0)
                    best = left;
                if (right < count && CompareEntryByScoreDescending(_heap[right], _heap[best]) > 0)
                    best = right;
                if (best == i) break;
                Swap(i, best);
                i = best;
            }
        }

        void Swap(int a, int b)
        {
            var t = _heap[a];
            _heap[a] = _heap[b];
            _heap[b] = t;
        }

        void MinHeapSwap(int a, int b)
        {
            var t = _minHeap[a];
            _minHeap[a] = _minHeap[b];
            _minHeap[b] = t;
        }

        void MinHeapBubbleUp(int i)
        {
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (CompareEntryByScoreAscending(_minHeap[i], _minHeap[parent]) >= 0)
                    break;
                MinHeapSwap(i, parent);
                i = parent;
            }
        }

        void MinHeapBubbleDown(int i)
        {
            int count = _minHeap.Count;
            while (true)
            {
                int left = 2 * i + 1;
                int right = 2 * i + 2;
                int smallest = i;
                if (left < count && CompareEntryByScoreAscending(_minHeap[left], _minHeap[smallest]) < 0)
                    smallest = left;
                if (right < count && CompareEntryByScoreAscending(_minHeap[right], _minHeap[smallest]) < 0)
                    smallest = right;
                if (smallest == i) break;
                MinHeapSwap(i, smallest);
                i = smallest;
            }
        }

        /// <summary>viewTransform, else Camera.main if useMainCamera, else player. Returns null if all null; callers handle (EnqueueWithPriority returns, IsInViewCone returns true).</summary>
        Transform ResolveViewTransform(Transform player)
        {
            if (viewTransform != null) return viewTransform;
            if (useMainCamera && Camera.main != null) return Camera.main.transform;
            if (player != null) return player;
            if (!_warnedViewNull)
            {
                _warnedViewNull = true;
                Debug.LogWarning("[ChunkViewConePrioritizer] ResolveViewTransform: viewTransform, Camera.main, and player are null. Assign viewTransform or ensure Camera.main/player is set.");
            }
            return null;
        }
    }
}
*/