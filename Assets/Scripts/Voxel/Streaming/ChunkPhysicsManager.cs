using System.Collections.Generic;
using TerraVoxel.Voxel.Core;

namespace TerraVoxel.Voxel.Streaming
{
    internal sealed class ChunkPhysicsManager
    {
        readonly ChunkManager.Context _ctx;

        public ChunkPhysicsManager(ChunkManager.Context ctx)
        {
            _ctx = ctx;
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
                    chunk.SetColliderEnabled(false);
                    continue;
                }
                chunk.SetColliderEnabled(enabled);
            }
        }

        internal void Tick()
        {
            var optimizer = _ctx.PhysicsOptimizer;
            if (optimizer != null)
                optimizer.Tick(_ctx.Owner);
        }
    }
}
