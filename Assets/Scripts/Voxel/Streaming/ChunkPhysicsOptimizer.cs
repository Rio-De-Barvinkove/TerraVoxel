using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>
    /// Enables colliders only for chunks within a near radius around the player.
    /// Uses hysteresis to avoid toggling colliders every frame at the boundary.
    /// </summary>
    [DisallowMultipleComponent]
    public class ChunkPhysicsOptimizer : MonoBehaviour
    {
        [SerializeField] bool enableOptimization = true;
        [SerializeField] int activeRadius = 1;
        [SerializeField] int inactiveRadius = 2;
        [SerializeField] bool includeVerticalDistance = false;
        [SerializeField] bool disablePreloaded = true;

        readonly HashSet<ChunkCoord> _physicsActive = new HashSet<ChunkCoord>();
        readonly HashSet<ChunkCoord> _seen = new HashSet<ChunkCoord>();
        readonly List<ChunkCoord> _prune = new List<ChunkCoord>();

        ChunkCoord _lastCenter;
        bool _hasLastCenter;
        bool _lastAddColliders;
        bool _wasEnabled;
        int _lastActiveRadius;
        int _lastInactiveRadius;
        bool _lastIncludeVertical;
        bool _lastDisablePreloaded;

        public void Tick(ChunkManager manager)
        {
            if (manager == null) return;

            if (!enableOptimization)
            {
                if (_wasEnabled)
                    RestoreAll(manager);
                _wasEnabled = false;
                return;
            }

            _wasEnabled = true;

            var player = manager.PlayerTransform;
            if (player == null) return;

            int chunkSize = manager.ChunkSize;
            if (chunkSize <= 0) return;

            int active = Mathf.Max(0, activeRadius);
            int inactive = Mathf.Max(active, inactiveRadius);

            bool addColliders = manager.AddColliders;
            bool configChanged = _lastActiveRadius != active
                || _lastInactiveRadius != inactive
                || _lastIncludeVertical != includeVerticalDistance
                || _lastDisablePreloaded != disablePreloaded;
            bool addCollidersChanged = addColliders != _lastAddColliders;

            if (!addColliders)
            {
                if (addCollidersChanged || _physicsActive.Count > 0)
                    DisableAll(manager);
                UpdateConfigState(active, inactive, addColliders);
                return;
            }

            var center = PlayerTracker.WorldToChunk(player.position, chunkSize);
            bool centerChanged = !_hasLastCenter || !center.Equals(_lastCenter);

            if (!centerChanged && !configChanged && !addCollidersChanged)
                return;

            _lastCenter = center;
            _hasLastCenter = true;
            UpdateConfigState(active, inactive, addColliders);

            int activeSq = active * active;
            int inactiveSq = inactive * inactive;

            _seen.Clear();
            foreach (var kvp in manager.ActiveChunks)
            {
                var coord = kvp.Key;
                var chunk = kvp.Value;
                if (chunk == null) continue;

                _seen.Add(coord);

                if (disablePreloaded && manager.IsPreloaded(coord))
                {
                    if (_physicsActive.Remove(coord))
                        chunk.SetColliderEnabled(false);
                    continue;
                }

                int dx = coord.X - center.X;
                int dz = coord.Z - center.Z;
                int dy = includeVerticalDistance ? coord.Y - center.Y : 0;
                int distSq = dx * dx + dz * dz + dy * dy;

                bool isActive = _physicsActive.Contains(coord);
                bool shouldEnable = distSq <= activeSq || (distSq <= inactiveSq && isActive);

                if (shouldEnable == isActive) continue;

                // Only enable collider if chunk has a valid mesh with vertices
                if (shouldEnable)
                {
                    Mesh mesh = chunk.GetRenderMesh();
                    if (mesh == null || mesh.vertexCount == 0)
                    {
                        // Chunk has no mesh yet - don't enable collider
                        if (isActive)
                        {
                            chunk.SetColliderEnabled(false);
                            _physicsActive.Remove(coord);
                        }
                        continue;
                    }
                }

                chunk.SetColliderEnabled(shouldEnable);
                if (shouldEnable)
                    _physicsActive.Add(coord);
                else
                    _physicsActive.Remove(coord);
            }

            PruneMissing();
        }

        void UpdateConfigState(int active, int inactive, bool addColliders)
        {
            _lastActiveRadius = active;
            _lastInactiveRadius = inactive;
            _lastIncludeVertical = includeVerticalDistance;
            _lastDisablePreloaded = disablePreloaded;
            _lastAddColliders = addColliders;
        }

        void DisableAll(ChunkManager manager)
        {
            foreach (var kvp in manager.ActiveChunks)
            {
                if (kvp.Value != null)
                    kvp.Value.SetColliderEnabled(false);
            }
            _physicsActive.Clear();
            _seen.Clear();
            _prune.Clear();
        }

        void RestoreAll(ChunkManager manager)
        {
            if (!manager.AddColliders) return;

            foreach (var kvp in manager.ActiveChunks)
            {
                if (kvp.Value == null) continue;
                if (disablePreloaded && manager.IsPreloaded(kvp.Key)) continue;
                kvp.Value.SetColliderEnabled(true);
                _physicsActive.Add(kvp.Key);
            }
            _seen.Clear();
            _prune.Clear();
        }

        void PruneMissing()
        {
            if (_physicsActive.Count == 0) return;

            _prune.Clear();
            foreach (var coord in _physicsActive)
            {
                if (!_seen.Contains(coord))
                    _prune.Add(coord);
            }
            for (int i = 0; i < _prune.Count; i++)
                _physicsActive.Remove(_prune[i]);
            _prune.Clear();
        }
    }
}
