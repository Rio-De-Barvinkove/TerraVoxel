using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>
    /// Prioritizes pending chunk spawns by camera/view direction (view cone).
    /// </summary>
    [DisallowMultipleComponent]
    public class ChunkViewConePrioritizer : MonoBehaviour
    {
        [SerializeField] bool enable = true;
        [SerializeField] Transform viewTransform;
        [SerializeField] bool useMainCamera = true;
        [SerializeField] bool ignoreVertical = true;
        [SerializeField] bool includeVerticalDistance = false;
        [SerializeField] float coneHalfAngleDeg = 70f;
        [SerializeField] int maxScan = 512;

        readonly List<ChunkCoord> _buffer = new List<ChunkCoord>(512);

        public bool Enabled => enable;

        public bool TryDequeue(Queue<ChunkCoord> pending, ChunkCoord center, Transform player, out ChunkCoord coord)
        {
            if (!enable || pending == null || pending.Count == 0)
            {
                coord = default;
                return false;
            }

            Transform view = ResolveViewTransform(player);
            Vector3 forward = view != null ? view.forward : Vector3.forward;
            if (ignoreVertical)
                forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                forward = Vector3.forward;
            else
                forward.Normalize();

            int scanCount = pending.Count;
            if (maxScan > 0 && scanCount > maxScan)
                scanCount = maxScan;

            if (_buffer.Capacity < scanCount)
                _buffer.Capacity = scanCount;
            _buffer.Clear();

            for (int i = 0; i < scanCount; i++)
                _buffer.Add(pending.Dequeue());

            float halfAngle = Mathf.Clamp(coneHalfAngleDeg, 0f, 180f);
            float cosHalfAngle = Mathf.Cos(halfAngle * Mathf.Deg2Rad);

            bool foundInCone = false;
            int bestIndex = -1;
            int bestDistSq = int.MaxValue;
            float bestDot = -1f;

            for (int i = 0; i < _buffer.Count; i++)
            {
                var c = _buffer[i];
                int dx = c.X - center.X;
                int dy = c.Y - center.Y;
                int dz = c.Z - center.Z;
                int distSq = dx * dx + dz * dz + (includeVerticalDistance ? dy * dy : 0);

                Vector3 to = new Vector3(dx, includeVerticalDistance ? dy : 0, dz);
                if (ignoreVertical)
                    to.y = 0f;
                float dot = 1f;
                if (to.sqrMagnitude > 0.0001f)
                {
                    to.Normalize();
                    dot = Vector3.Dot(forward, to);
                }

                bool inCone = dot >= cosHalfAngle;
                if (inCone)
                {
                    if (!foundInCone || distSq < bestDistSq || (distSq == bestDistSq && dot > bestDot))
                    {
                        foundInCone = true;
                        bestIndex = i;
                        bestDistSq = distSq;
                        bestDot = dot;
                    }
                }
                else if (!foundInCone)
                {
                    if (distSq < bestDistSq || (distSq == bestDistSq && dot > bestDot))
                    {
                        bestIndex = i;
                        bestDistSq = distSq;
                        bestDot = dot;
                    }
                }
            }

            if (bestIndex < 0)
                bestIndex = 0;

            coord = _buffer[bestIndex];
            for (int i = 0; i < _buffer.Count; i++)
            {
                if (i == bestIndex) continue;
                pending.Enqueue(_buffer[i]);
            }
            _buffer.Clear();
            return true;
        }

        Transform ResolveViewTransform(Transform player)
        {
            if (viewTransform != null) return viewTransform;
            if (useMainCamera && Camera.main != null) return Camera.main.transform;
            return player;
        }
    }
}
