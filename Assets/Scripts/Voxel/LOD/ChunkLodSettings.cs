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
        [Tooltip("When dist >= this, DefaultLevel uses Mode.None (far-range). 0 = disabled.")]
        public int DefaultLevelFarDistance = 64;

        [Tooltip("Distance is measured in chunk units (max(|dx|,|dz|)).")]
        public List<ChunkLodLevel> Levels = new List<ChunkLodLevel>();

        /// <summary>1) Exact match: dist in [MinDistance, MaxDistance]. 2) Beyond all ranges: coarsest level with largest MaxDistance (then by detail rank). 3) Gap: returns DefaultLevel(dist) and false.</summary>
        public bool TryGetLevelForDistance(int dist, out ChunkLodLevel level)
        {
            if (Levels == null || Levels.Count == 0)
            {
                level = DefaultLevel(dist);
                return false;
            }

            for (int i = 0; i < Levels.Count; i++)
            {
                var candidate = Levels[i];
                if (!candidate.IsValid) continue;
                if (dist >= candidate.MinDistance && dist <= candidate.MaxDistance)
                {
                    level = candidate;
                    return true;
                }
            }

            int maxMaxDist = -1;
            int coarsestIndex = -1;
            int coarsestRank = -1;
            for (int i = 0; i < Levels.Count; i++)
            {
                var candidate = Levels[i];
                if (!candidate.IsValid) continue;
                if (dist <= candidate.MaxDistance) continue;
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
            if (coarsestIndex >= 0)
            {
                level = Levels[coarsestIndex];
                return true;
            }

            // No exact range match and dist not beyond all MaxDistance (e.g. gap in Levels): use default for predictable behaviour.
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

        /// <summary>Target level from dist; then hysteresis: when moving to coarser, keep current if dist &lt;= current.MaxDistance + hysteresis; when moving to finer, keep current if dist &gt;= current.MinDistance - hysteresis. Uses DefaultHysteresis when level.Hysteresis is 0.</summary>
        public ChunkLodLevel ResolveLevel(int dist, int currentStep, ChunkLodMode currentMode)
        {
            ChunkLodLevel target = DefaultLevel(dist);
            TryGetLevelForDistance(dist, out target);

            ChunkLodLevel current;
            if (!TryGetLevelForState(currentStep, currentMode, out current))
                current = target;

            if (current.LodStep == target.LodStep && current.Mode == target.Mode)
                return target;

            int hysteresis = current.Hysteresis > 0 ? current.Hysteresis : DefaultHysteresis;
            hysteresis = Mathf.Min(hysteresis, ChunkLodLevel.MaxHysteresis);
            int currentDetailRank = GetDetailRank(current);
            int targetDetailRank = GetDetailRank(target);
            bool movingToCoarser = targetDetailRank > currentDetailRank;

            if (movingToCoarser)
            {
                if (dist <= current.MaxDistance + hysteresis)
                    return current;
            }
            else
            {
                int downHyst = hysteresis;
                if (dist >= current.MinDistance - downHyst)
                    return current;
            }

            return target;
        }

        ChunkLodLevel DefaultLevel(int dist = -1)
        {
            ChunkLodMode mode = DefaultMode;
            if (DefaultLevelFarDistance > 0 && dist >= DefaultLevelFarDistance)
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

        /// <summary>Higher rank = coarser (less detail). Weights are for ordering only (Mesh &lt; Billboard &lt; Svo &lt; None); tune if needed.</summary>
        int GetDetailRank(ChunkLodLevel level)
        {
            int stepRank = Mathf.Max(1, level.LodStep);
            int modeWeight = level.Mode == ChunkLodMode.None ? 32 : (level.Mode == ChunkLodMode.Svo ? 16 : (level.Mode == ChunkLodMode.Billboard ? 8 : 0));
            return stepRank * (modeWeight + 1);
        }

        /// <summary>Editor-only: sort by MinDistance, warn on duplicate ranges and overlapping ranges. Does not block invalid data.</summary>
        void OnValidate()
        {
            if (Levels == null) return;
            Levels.Sort((a, b) => a.MinDistance.CompareTo(b.MinDistance));

            var seen = new HashSet<(int, int)>();
            for (int i = 0; i < Levels.Count; i++)
            {
                var a = Levels[i];
                if (!a.IsValid) continue;
                if (a.Hysteresis > ChunkLodLevel.MaxHysteresis)
                    Debug.LogWarning($"[ChunkLodSettings] Hysteresis {a.Hysteresis} at index {i} exceeds MaxHysteresis ({ChunkLodLevel.MaxHysteresis}). Clamp to avoid unexpected behaviour.");
                var key = (a.MinDistance, a.MaxDistance);
                if (seen.Contains(key))
                {
                    Debug.LogWarning($"[ChunkLodSettings] Duplicate level range Min={a.MinDistance} Max={a.MaxDistance} at index {i}. Remove duplicate.");
                    continue;
                }
                seen.Add(key);
            }

            for (int i = 1; i < Levels.Count; i++)
            {
                var prev = Levels[i - 1];
                var curr = Levels[i];
                if (!prev.IsValid || !curr.IsValid) continue;
                if (curr.MinDistance < prev.MaxDistance)
                    Debug.LogWarning($"[ChunkLodSettings] Overlapping levels: [{prev.MinDistance},{prev.MaxDistance}] and [{curr.MinDistance},{curr.MaxDistance}] at index {i}.");
            }
        }
    }
}
