using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Generation;
using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    internal sealed class ChunkSafeSpawnManager
    {
        readonly ChunkManager.Context _ctx;

        internal ChunkSafeSpawnManager(ChunkManager.Context ctx)
        {
            _ctx = ctx;
        }

        internal void SetPlayerFrozen(bool frozen)
        {
            var player = _ctx.Player;
            if (player == null) return;

            Behaviour controller = player.GetComponent("PlayerSimpleController") as Behaviour;
            if (controller == null)
            {
                var behaviours = player.GetComponentsInChildren<Behaviour>(true);
                for (int i = 0; i < behaviours.Length; i++)
                {
                    if (behaviours[i] != null && behaviours[i].GetType().Name == "PlayerSimpleController")
                    {
                        controller = behaviours[i];
                        break;
                    }
                }
            }
            var cc = player.GetComponent<CharacterController>() ?? player.GetComponentInChildren<CharacterController>();

            if (frozen)
            {
                if (_ctx.PlayerFrozenForSafeSpawn) return;
                if (controller != null)
                {
                    _ctx.SavedPlayerControllerEnabled = controller.enabled;
                    controller.enabled = false;
                }
                if (cc != null)
                {
                    _ctx.SavedCharacterControllerEnabled = cc.enabled;
                    cc.enabled = false;
                }
                _ctx.PlayerFrozenForSafeSpawn = true;
            }
            else
            {
                if (!_ctx.PlayerFrozenForSafeSpawn) return;
                if (controller != null) controller.enabled = _ctx.SavedPlayerControllerEnabled;
                if (cc != null) cc.enabled = _ctx.SavedCharacterControllerEnabled;
                _ctx.PlayerFrozenForSafeSpawn = false;
            }
        }

        /// <summary>Initializes safe spawn region and optionally freezes player until anchor chunk is meshed. Assumes chunks/mesh will be generated; timeout unfreezes if not ready.</summary>
        internal void TryInitSafeSpawn()
        {
            var worldGen = _ctx.WorldGen;
            var player = _ctx.Player;
            if (worldGen == null || !worldGen.EnableSafeSpawn || player == null) return;

            int chunkSize = worldGen.ChunkSize;
            float voxelSize = VoxelConstants.VoxelSize;
            float sizeChunks = Mathf.Max(0.1f, worldGen.SafeSpawnSizeChunks);
            _ctx.SafeSpawnSizeVoxels = Mathf.Max(1, Mathf.RoundToInt(sizeChunks * chunkSize));

            double scale = chunkSize * voxelSize;
            int baseChunkX = VoxelMath.FloorToIntClamped(player.position.x / scale);
            int baseChunkZ = VoxelMath.FloorToIntClamped(player.position.z / scale);

            _ctx.SafeSpawnWorldX0 = baseChunkX * chunkSize;
            _ctx.SafeSpawnWorldZ0 = baseChunkZ * chunkSize;

            int maxH = 0;
            var noiseStack = _ctx.NoiseStack;
            for (int x = 0; x < _ctx.SafeSpawnSizeVoxels; x++)
            {
                for (int z = 0; z < _ctx.SafeSpawnSizeVoxels; z++)
                {
                    float h = ChunkGenerator.SampleHeightAt(_ctx.SafeSpawnWorldX0 + x, _ctx.SafeSpawnWorldZ0 + z, worldGen, noiseStack);
                    int hi = Mathf.FloorToInt(h);
                    if (hi > maxH) maxH = hi;
                }
            }

            _ctx.SafeSpawnBaseY = maxH + 1;
            _ctx.SafeSpawnTopY = _ctx.SafeSpawnBaseY + Mathf.Max(1, worldGen.SafeSpawnThickness) - 1;
            _ctx.SafeSpawnInitialized = true;

            _ctx.PendingSafeSpawnSnap = worldGen.SnapPlayerToSafeSpawn;
            if (_ctx.PendingSafeSpawnSnap)
            {
                int centerX = _ctx.SafeSpawnWorldX0 + _ctx.SafeSpawnSizeVoxels / 2;
                int centerZ = _ctx.SafeSpawnWorldZ0 + _ctx.SafeSpawnSizeVoxels / 2;
                int anchorY = _ctx.SafeSpawnBaseY;
                _ctx.SafeSpawnAnchorCoord = new ChunkCoord(
                    Mathf.FloorToInt((float)centerX / chunkSize),
                    Mathf.FloorToInt((float)anchorY / chunkSize),
                    Mathf.FloorToInt((float)centerZ / chunkSize)
                );
                _ctx.WaitingSafeSpawnMesh = true;
                _ctx.SafeSpawnWaitStart = Time.realtimeSinceStartupAsDouble;
                SetPlayerFrozen(true);
            }
        }

        internal bool ApplySafeSpawnToChunk(Chunk chunk, ChunkCoord coord)
        {
            var worldGen = _ctx.WorldGen;
            if (worldGen == null) return false;
            int chunkSize = worldGen.ChunkSize;
            int worldX0 = coord.X * chunkSize;
            int worldZ0 = coord.Z * chunkSize;
            int worldX1 = worldX0 + chunkSize - 1;
            int worldZ1 = worldZ0 + chunkSize - 1;

            int spawnX1 = _ctx.SafeSpawnWorldX0 + _ctx.SafeSpawnSizeVoxels - 1;
            int spawnZ1 = _ctx.SafeSpawnWorldZ0 + _ctx.SafeSpawnSizeVoxels - 1;

            if (worldX1 < _ctx.SafeSpawnWorldX0 || worldX0 > spawnX1) return false;
            if (worldZ1 < _ctx.SafeSpawnWorldZ0 || worldZ0 > spawnZ1) return false;

            int worldY0 = coord.Y * chunkSize;
            int worldY1 = worldY0 + chunkSize - 1;
            if (worldY1 < _ctx.SafeSpawnBaseY || worldY0 > _ctx.SafeSpawnTopY) return false;

            int matIndex = worldGen.SafeSpawnMaterialIndex <= 0
                ? 200
                : Mathf.Clamp(worldGen.SafeSpawnMaterialIndex, 1, ushort.MaxValue);
            ushort mat = (ushort)matIndex;

            int startX = Mathf.Max(worldX0, _ctx.SafeSpawnWorldX0);
            int endX = Mathf.Min(worldX1, spawnX1);
            int startZ = Mathf.Max(worldZ0, _ctx.SafeSpawnWorldZ0);
            int endZ = Mathf.Min(worldZ1, spawnZ1);
            int startY = Mathf.Max(worldY0, _ctx.SafeSpawnBaseY);
            int endY = Mathf.Min(worldY1, _ctx.SafeSpawnTopY);

            for (int wx = startX; wx <= endX; wx++)
            {
                int lx = wx - worldX0;
                for (int wz = startZ; wz <= endZ; wz++)
                {
                    int lz = wz - worldZ0;
                    for (int wy = startY; wy <= endY; wy++)
                    {
                        int ly = wy - worldY0;
                        int idx = chunk.Data.Index(lx, ly, lz);
                        chunk.Data.Materials[idx] = mat;
                    }
                }
            }
            return true;
        }

        internal bool ReapplySafeSpawnToChunk(Chunk chunk, ChunkCoord coord, out bool changed)
        {
            changed = false;
            var worldGen = _ctx.WorldGen;
            if (worldGen == null || !worldGen.EnableSafeSpawn) return false;

            int chunkSize = worldGen.ChunkSize;
            int worldX0 = coord.X * chunkSize;
            int worldZ0 = coord.Z * chunkSize;
            int worldX1 = worldX0 + chunkSize - 1;
            int worldZ1 = worldZ0 + chunkSize - 1;

            int spawnX1 = _ctx.SafeSpawnWorldX0 + _ctx.SafeSpawnSizeVoxels - 1;
            int spawnZ1 = _ctx.SafeSpawnWorldZ0 + _ctx.SafeSpawnSizeVoxels - 1;

            if (worldX1 < _ctx.SafeSpawnWorldX0 || worldX0 > spawnX1) return false;
            if (worldZ1 < _ctx.SafeSpawnWorldZ0 || worldZ0 > spawnZ1) return false;

            int worldY0 = coord.Y * chunkSize;
            int worldY1 = worldY0 + chunkSize - 1;
            if (worldY1 < _ctx.SafeSpawnBaseY || worldY0 > _ctx.SafeSpawnTopY) return false;

            int matIndex = worldGen.SafeSpawnMaterialIndex <= 0
                ? 200
                : Mathf.Clamp(worldGen.SafeSpawnMaterialIndex, 1, ushort.MaxValue);
            ushort mat = (ushort)matIndex;

            int startX = Mathf.Max(worldX0, _ctx.SafeSpawnWorldX0);
            int endX = Mathf.Min(worldX1, spawnX1);
            int startZ = Mathf.Max(worldZ0, _ctx.SafeSpawnWorldZ0);
            int endZ = Mathf.Min(worldZ1, spawnZ1);
            int startY = Mathf.Max(worldY0, _ctx.SafeSpawnBaseY);
            int endY = Mathf.Min(worldY1, _ctx.SafeSpawnTopY);

            for (int wx = startX; wx <= endX; wx++)
            {
                int lx = wx - worldX0;
                for (int wz = startZ; wz <= endZ; wz++)
                {
                    int lz = wz - worldZ0;
                    for (int wy = startY; wy <= endY; wy++)
                    {
                        int ly = wy - worldY0;
                        int idx = chunk.Data.Index(lx, ly, lz);
                        if (chunk.Data.Materials[idx] != mat)
                        {
                            chunk.Data.Materials[idx] = mat;
                            changed = true;
                        }
                    }
                }
            }

            return true;
        }

        internal void SnapPlayerToSafeSpawn()
        {
            var player = _ctx.Player;
            if (player == null) return;
            float voxelSize = VoxelConstants.VoxelSize;
            float cx = (_ctx.SafeSpawnWorldX0 + _ctx.SafeSpawnSizeVoxels * 0.5f) * voxelSize;
            float cz = (_ctx.SafeSpawnWorldZ0 + _ctx.SafeSpawnSizeVoxels * 0.5f) * voxelSize;
            float surfaceY = (_ctx.SafeSpawnTopY + 1) * voxelSize;

            float y = surfaceY + 0.1f;
            var cc = player.GetComponent<CharacterController>();
            if (cc == null)
                cc = player.GetComponentInChildren<CharacterController>();

            if (cc != null)
            {
                float bottomOffset = (cc.height * 0.5f) - cc.center.y;
                y = surfaceY + bottomOffset + 0.05f;
            }

            player.position = new Vector3(cx, y, cz);
        }
    }
}
