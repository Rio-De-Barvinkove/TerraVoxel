using TerraVoxel.Voxel.Core;
using UnityEngine;

/*

using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>
    /// Enables colliders only for chunks within a near radius around the player.
    /// Uses hysteresis (inactiveRadius &gt; activeRadius): enable when inside activeRadius, disable only when beyond inactiveRadius. First Tick always runs full logic (_hasLastCenter false).
    /// When includeVerticalDistance is false, Y is ignored so vertical distance does not affect enable/disable.
    /// Main-thread only; iteration over ActiveChunks is not locked (ChunkManager is single-thread).
    /// </summary>
    [DisallowMultipleComponent]
    public class ChunkPhysicsOptimizer : MonoBehaviour
    {
        [SerializeField] bool enableOptimization = true;
        [Tooltip("Chunks within this radius (chunk units) get colliders. Fixed; does not adapt to chunk size changes.")]
        [SerializeField] int activeRadius = 1;
        [Tooltip("Hysteresis: colliders stay on until beyond this radius. Must be >= activeRadius.")]
        [SerializeField] int inactiveRadius = 2;
        [Tooltip("Include Y in distance. No separate vertical hysteresis; may toggle often when moving on Y.")]
        [SerializeField] bool includeVerticalDistance = false;
        [Tooltip("When true, preloaded chunks always have colliders disabled even inside active radius.")]
        [SerializeField] bool disablePreloaded = true;

        readonly HashSet<ChunkCoord> _physicsActive = new HashSet<ChunkCoord>();
        readonly HashSet<ChunkCoord> _seen = new HashSet<ChunkCoord>();
        readonly List<ChunkCoord> _prune = new List<ChunkCoord>();
        readonly object _stateLock = new object();

        ChunkCoord _lastCenter;
        bool _hasLastCenter;
        bool _lastAddColliders;
        bool _wasEnabled;
        bool _lastEnableOptimization;
        int _lastActiveRadius;
        int _lastInactiveRadius;
        bool _lastIncludeVertical;
        bool _lastDisablePreloaded;

        public void Tick(ChunkManager manager)
        {
            // CPU-only rollback: no-op - method body commented
            if (manager == null) return;

            if (!enableOptimization)
            {
                if (_wasEnabled)
                    RestoreAll(manager);
                _wasEnabled = false;
                return;
            }

            var player = manager.PlayerTransform;
            if (player == null) return;

            int chunkSize = manager.ChunkSize;
            if (chunkSize <= 0)
            {
                _wasEnabled = false;
                if (_physicsActive.Count > 0) DisableAll(manager);
                return;
            }

            _wasEnabled = true;

            int active = Mathf.Max(0, activeRadius);
            int inactive = Mathf.Max(active, inactiveRadius);

            bool addColliders = manager.AddColliders;
            bool configChanged = _lastEnableOptimization != enableOptimization
                || _lastActiveRadius != active
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

            var center = PlayerTracker.WorldToChunk(player.position, chunkSize, manager.VoxelSize);
            bool centerChanged = !_hasLastCenter || !center.Equals(_lastCenter);

            if (!centerChanged && !configChanged && !addCollidersChanged)
                return;

            _lastCenter = center;
            _hasLastCenter = true;
            UpdateConfigState(active, inactive, addColliders);

            int activeSq = active * active;
            int inactiveSq = inactive * inactive;

            var activeChunks = manager.ActiveChunks;
            if (activeChunks == null) return;

            lock (_stateLock)
            {
                _seen.Clear();
                foreach (var kvp in activeChunks)
                {
                    var coord = kvp.Key;
                    var chunk = kvp.Value;
                    if (chunk == null) continue;

                    _seen.Add(coord);

                    if (disablePreloaded && manager.IsPreloaded(coord))
                    {
                        if (_physicsActive.Remove(coord))
                        {
                            if (chunk.IsGpuRendered)
                                chunk.SetGpuColliderEnabled(false);
                            else
                                chunk.SetColliderEnabled(false);
                        }
                        continue;
                    }

                    int dx = coord.X - center.X;
                    int dz = coord.Z - center.Z;
                    int dy = includeVerticalDistance ? coord.Y - center.Y : 0;
                    int distSq = dx * dx + dz * dz + dy * dy;
                    // When includeVerticalDistance is false, Y is ignored; vertical chunks get colliders by horizontal distance only.

                    bool isActive = _physicsActive.Contains(coord);
                    bool shouldEnable = distSq <= activeSq || (distSq <= inactiveSq && isActive);

                    if (shouldEnable == isActive) continue;

                    if (shouldEnable && !chunk.IsGpuRendered)
                    {
                        Mesh mesh = chunk.GetRenderMesh();
                        if (mesh == null || mesh.vertexCount == 0)
                        {
                            if (isActive)
                            {
                                chunk.SetColliderEnabled(false);
                                _physicsActive.Remove(coord);
                            }
                            continue;
                        }
                    }

                    if (chunk.IsGpuRendered)
                    {
                        bool hasGeometry = shouldEnable && chunk.Data.GpuSlot >= 0;
                        if (hasGeometry && manager.GpuWorldState != null)
                        {
                            var desc = manager.GpuWorldState.GetDescriptor(chunk.Data.GpuSlot);
                            hasGeometry = desc.VertexCount > 0;
                        }
                        chunk.SetGpuColliderEnabled(shouldEnable && hasGeometry);
                    }
                    else
                        chunk.SetColliderEnabled(shouldEnable);
                    if (shouldEnable)
                        _physicsActive.Add(coord);
                    else
                        _physicsActive.Remove(coord);
                }

                PruneMissingInner();
            }
        }

        void UpdateConfigState(int active, int inactive, bool addColliders)
        {
            _lastEnableOptimization = enableOptimization;
            _lastActiveRadius = active;
            _lastInactiveRadius = inactive;
            _lastIncludeVertical = includeVerticalDistance;
            _lastDisablePreloaded = disablePreloaded;
            _lastAddColliders = addColliders;
        }

        void DisableAll(ChunkManager manager)
        {
            var activeChunks = manager?.ActiveChunks;
            if (activeChunks == null) return;
            lock (_stateLock)
            {
                foreach (var kvp in activeChunks)
                {
                    if (kvp.Value == null) continue;
                    if (kvp.Value.IsGpuRendered)
                        kvp.Value.SetGpuColliderEnabled(false);
                    else
                        kvp.Value.SetColliderEnabled(false);
                }
                _physicsActive.Clear();
                _seen.Clear();
                _prune.Clear();
            }
        }

        void RestoreAll(ChunkManager manager)
        {
            if (manager == null || !manager.AddColliders) return;
            var activeChunks = manager.ActiveChunks;
            if (activeChunks == null) return;
            var gpuWorldState = manager.GpuWorldState;
            lock (_stateLock)
            {
                foreach (var kvp in activeChunks)
                {
                    if (kvp.Value == null) continue;
                    if (disablePreloaded && manager.IsPreloaded(kvp.Key)) continue;
                    if (kvp.Value.IsGpuRendered)
                    {
                        bool hasGeometry = kvp.Value.Data.GpuSlot >= 0 && gpuWorldState != null
                            && gpuWorldState.GetDescriptor(kvp.Value.Data.GpuSlot).VertexCount > 0;
                        kvp.Value.SetGpuColliderEnabled(hasGeometry);
                    }
                    else
                        kvp.Value.SetColliderEnabled(true);
                    _physicsActive.Add(kvp.Key);
                }
                _seen.Clear();
                _prune.Clear();
            }
        }

        /// <summary>Removes coords from _physicsActive that were not seen this tick (chunk unloaded or not in ActiveChunks).</summary>
        void PruneMissingInner()
        {
            if (_physicsActive.Count == 0) return;

            _prune.Clear();
            if (_prune.Capacity < _physicsActive.Count)
                _prune.Capacity = _physicsActive.Count;
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
*/

namespace TerraVoxel.Voxel.Streaming
{
    [DisallowMultipleComponent]
    public class ChunkPhysicsOptimizer : MonoBehaviour
    {
        public void Tick(ChunkManager manager) { }
    }
}