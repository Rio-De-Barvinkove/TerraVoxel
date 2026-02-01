using System.Collections.Generic;
using UnityEngine;

namespace TerraVoxel.Voxel.Lod
{
    [CreateAssetMenu(menuName = "TerraVoxel/Chunk LOD Settings", fileName = "ChunkLodSettings")]
    public class ChunkLodSettings : ScriptableObject
    {
        public int DefaultLodStep = 1;
        public ChunkLodMode DefaultMode = ChunkLodMode.Mesh;
        [Tooltip("Used when level.Hysteresis is 0. Clamped to ChunkLodLevel.MaxHysteresis.")]
        public int DefaultHysteresis = 1;
        [Tooltip("When dist >= this, DefaultLevel uses Mode.None (far-range). 0 = disabled (DefaultMode always).")]
        public int DefaultLevelFarDistance = 64;

        [Header("Mode weights for GetDetailRank (higher = coarser). Should satisfy Mesh <= Billboard <= Svo <= None.")]
        [Range(0, 256)]
        [SerializeField] int modeMeshWeight = 0;
        [Range(0, 256)]
        [SerializeField] int modeBillboardWeight = 8;
        [Range(0, 256)]
        [SerializeField] int modeSvoWeight = 16;
        [Range(0, 256)]
        [SerializeField] int modeNoneWeight = 32;

        [Tooltip("Distance is measured in chunk units (max(|dx|,|dz|)).")]
        public List<ChunkLodLevel> Levels = new List<ChunkLodLevel>();

        /// <summary>Single pass: exact match dist in [MinDistance, MaxDistance], else coarsest with largest MaxDistance (then by detail rank). Gap returns DefaultLevel(dist) and false.</summary>
        public bool TryGetLevelForDistance(int dist, out ChunkLodLevel level)
        {
            level = DefaultLevel(dist);
            if (Levels == null || Levels.Count == 0)
                return false;

            int maxMaxDist = -1;
            int coarsestIndex = -1;
            int coarsestRank = -1;

            for (int i = 0; i < Levels.Count; i++)
            {
                var candidate = Levels[i];
                if (!candidate.IsValid) continue;

                if (dist >= candidate.MinDistance && dist <= candidate.MaxDistance)
                {
                    level = candidate;
                    return true;
                }

                if (dist > candidate.MaxDistance)
                {
                    int rank = GetDetailRank(candidate);
                    bool better = maxMaxDist < candidate.MaxDistance ||
                        (maxMaxDist == candidate.MaxDistance && rank > coarsestRank);
                    if (better)
                    {
                        maxMaxDist = candidate.MaxDistance;
                        coarsestIndex = i;
                        coarsestRank = rank;
                    }
                }
            }

            if (coarsestIndex >= 0)
            {
                level = Levels[coarsestIndex];
                return true;
            }

            level = DefaultLevel(dist);
            return false;
        }

        public bool TryGetLevelForState(int lodStep, ChunkLodMode mode, out ChunkLodLevel level)
        {
            if (Levels != null)
            {
                for (int i = 0; i < Levels.Count; i++)
                {
                    var candidate = Levels[i];
                    if (!candidate.IsValid) continue;
                    if (candidate.LodStep == lodStep && candidate.Mode == mode)
                    {
                        level = candidate;
                        return true;
                    }
                }
            }

            level = DefaultLevel(-1);
            return false;
        }

        /// <summary>Target level from dist; then hysteresis: when moving to coarser, keep current if dist &lt;= current.MaxDistance + upHysteresis; when moving to finer, keep current if dist &gt;= current.MinDistance - downHysteresis (downHysteresis = hysteresis/2). Uses DefaultHysteresis when level.Hysteresis is 0. When in a gap (no level matches) and dist &gt; current.MaxDistance, forces switch to Default to avoid stale level.</summary>
        public ChunkLodLevel ResolveLevel(int dist, int currentStep, ChunkLodMode currentMode)
        {
            ChunkLodLevel target = DefaultLevel(dist);
            bool matchedLevel = TryGetLevelForDistance(dist, out target);

            ChunkLodLevel current;
            if (!TryGetLevelForState(currentStep, currentMode, out current))
                current = target;

            if (current.LodStep == target.LodStep && current.Mode == target.Mode)
                return target;

            // Gap bug fix: when in uncovered range and we've left current level's range, switch to Default (rank compare may not trigger otherwise)
            if (!matchedLevel && dist > current.MaxDistance && current.MaxDistance < int.MaxValue)
                return target;

            int hysteresis = current.Hysteresis > 0 ? current.Hysteresis : DefaultHysteresis;
            hysteresis = Mathf.Min(hysteresis, ChunkLodLevel.MaxHysteresis);
            int upHysteresis = hysteresis;
            // Asymmetry: downHysteresis = hysteresis/2 — upgrade to finer (closer) switches faster; downgrade to coarser keeps hysteresis to reduce boundary flicker. Int div (1/2=0) makes small hysteresis 0 for down.
            int downHysteresis = Mathf.Max(0, hysteresis / 2);

            int currentDetailRank = GetDetailRank(current);
            int targetDetailRank = GetDetailRank(target);
            bool movingToCoarser = targetDetailRank > currentDetailRank;

            if (movingToCoarser)
            {
                if (current.MaxDistance == int.MaxValue) return current;
                int maxWithHyst = ChunkLodLevel.MaxDistanceWithHysteresis(current.MaxDistance, upHysteresis);
                if (dist <= maxWithHyst) return current;
            }
            if (!movingToCoarser)
            {
                int minWithHyst = current.MinDistance <= downHysteresis ? 0 : current.MinDistance - downHysteresis;
                if (dist >= minWithHyst) return current;
            }

            return target;
        }

        /// <summary>Default level when no Levels match. When DefaultLevelFarDistance is 0, far-range switch is disabled and DefaultMode is always used (dist ignored).</summary>
        ChunkLodLevel DefaultLevel(int dist = -1)
        {
            ChunkLodMode mode = DefaultMode;
            if (DefaultLevelFarDistance > 0 && dist >= 0 && dist >= DefaultLevelFarDistance)
                mode = ChunkLodMode.None;
            int hyst = Mathf.Clamp(DefaultHysteresis, 0, ChunkLodLevel.MaxHysteresis);
            return new ChunkLodLevel
            {
                MinDistance = 0,
                MaxDistance = int.MaxValue,
                LodStep = Mathf.Max(1, DefaultLodStep),
                Hysteresis = hyst,
                Mode = mode
            };
        }

        /// <summary>Higher rank = coarser (less detail). Uses configurable mode weights; stepRank * (modeWeight + 1).</summary>
        int GetDetailRank(ChunkLodLevel level)
        {
            int stepRank = Mathf.Max(1, level.LodStep);
            int modeWeight = level.Mode == ChunkLodMode.None ? modeNoneWeight
                : (level.Mode == ChunkLodMode.Svo ? modeSvoWeight
                : (level.Mode == ChunkLodMode.Billboard ? modeBillboardWeight : modeMeshWeight));
            return stepRank * (Mathf.Max(0, modeWeight) + 1);
        }

        /// <summary>Public helper for comparing LOD detail (lower = finer).</summary>
        public int GetDetailRankFor(ChunkLodLevel level) => GetDetailRank(level);

        /// <summary>Editor-only: sort by MinDistance; warn on duplicates, Hysteresis, gaps, overlaps, and weight order. Non-destructive; use ContextMenu to manually remove overlapping levels.</summary>
        void OnValidate()
        {
            if (Levels == null) return;
            Levels.Sort((a, b) => a.MinDistance.CompareTo(b.MinDistance));

            var seen = new HashSet<long>();
            for (int i = 0; i < Levels.Count; i++)
            {
                var curr = Levels[i];
                var prev = i > 0 ? Levels[i - 1] : default;
                var next = i < Levels.Count - 1 ? Levels[i + 1] : default;

                if (!curr.IsValid) continue;

                // Hysteresis + duplicate
                if (curr.Hysteresis > ChunkLodLevel.MaxHysteresis)
                    Debug.LogWarning($"[ChunkLodSettings] Hysteresis {curr.Hysteresis} at index {i} exceeds MaxHysteresis ({ChunkLodLevel.MaxHysteresis}).");
                long key = (long)curr.MinDistance << 32 | (uint)curr.MaxDistance;
                if (seen.Contains(key))
                    Debug.LogWarning($"[ChunkLodSettings] Duplicate level range Min={curr.MinDistance} Max={curr.MaxDistance} at index {i}.");
                seen.Add(key);

                // Gap (avoid overflow: MaxDistance + 1)
                if (next.IsValid && curr.MaxDistance < int.MaxValue && next.MinDistance > curr.MaxDistance + 1)
                    Debug.LogWarning($"[ChunkLodSettings] Gap between levels {i} and {i + 1}: [{curr.MinDistance},{curr.MaxDistance}] vs [{next.MinDistance},{next.MaxDistance}].");

                // Overlap
                if (prev.IsValid && curr.MinDistance <= prev.MaxDistance)
                    Debug.LogWarning($"[ChunkLodSettings] Overlapping LOD levels at index {i}. Right-click asset → Remove Overlapping Levels.");
            }

            // Far-range warning
            const int farRangeWarnThreshold = 100000;
            if (Levels.Count > 0)
            {
                var last = Levels[Levels.Count - 1];
                if (last.IsValid && last.MaxDistance < farRangeWarnThreshold)
                    Debug.LogWarning($"[ChunkLodSettings] Last level MaxDistance={last.MaxDistance} < {farRangeWarnThreshold}. Far-range uncovered.");
            }

            // Weight order: Mesh <= Billboard <= Svo <= None
            if (modeMeshWeight > modeBillboardWeight || modeBillboardWeight > modeSvoWeight || modeSvoWeight > modeNoneWeight)
                Debug.LogWarning($"[ChunkLodSettings] Mode weights should satisfy Mesh <= Billboard <= Svo <= None (current: {modeMeshWeight}, {modeBillboardWeight}, {modeSvoWeight}, {modeNoneWeight}).");
        }

        [ContextMenu("Extend Last Level to Far Range")]
        void ExtendLastLevelToFarRange()
        {
            if (Levels == null || Levels.Count == 0) return;
            var last = Levels[Levels.Count - 1];
            if (!last.IsValid) return;
            last.MaxDistance = 999999;
            Levels[Levels.Count - 1] = last;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        [ContextMenu("Remove Overlapping Levels")]
        void RemoveOverlappingLevels()
        {
            if (Levels == null) return;
            Levels.Sort((a, b) => a.MinDistance.CompareTo(b.MinDistance));
            bool changed = false;
            for (int i = Levels.Count - 1; i >= 1; i--)
            {
                var prev = Levels[i - 1];
                var curr = Levels[i];
                if (!prev.IsValid || !curr.IsValid) continue;
                if (curr.MinDistance <= prev.MaxDistance)
                {
                    Levels.RemoveAt(i);
                    changed = true;
                }
            }
            if (changed)
            {
#if UNITY_EDITOR
                UnityEditor.EditorUtility.SetDirty(this);
#endif
            }
        }
    }
}
