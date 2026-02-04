using System;
using System.Collections.Generic;
using TerraVoxel.Voxel.Core;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>Delegates collider enable/disable and Tick to PhysicsOptimizer when set. Config lives on ChunkPhysicsOptimizer. Main-thread only; no lock (Context.Active/Preloaded are main-thread).</summary>
    internal sealed class ChunkPhysicsManager
    {
        readonly ChunkManager.Context _ctx;

        public ChunkPhysicsManager(ChunkManager.Context ctx)
        {
            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        }

        internal void SetCollidersEnabled(bool enabled)
        {
            _ctx.AddColliders = enabled;
            Dictionary<ChunkCoord, Chunk> active = _ctx.Active;
            foreach (var chunk in active.Values)
            {
                if (chunk == null) continue;
                if (_ctx.Preloaded.Contains(chunk.Coord))
                {
                    chunk.SetColliderEnabled(enabled);
                    continue;
                }
                chunk.SetColliderEnabled(enabled);
            }
        }

        internal void Tick()
        {
            if (!_ctx.AddColliders)
                return;
            var optimizer = _ctx.PhysicsOptimizer;
            if (optimizer != null)
                optimizer.Tick(_ctx.Owner);
            else
                SetCollidersEnabled(_ctx.AddColliders);
        }
    }
}
