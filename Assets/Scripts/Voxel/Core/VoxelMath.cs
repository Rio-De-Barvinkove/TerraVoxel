using System;
using UnityEngine;

namespace TerraVoxel.Voxel.Core
{
    public static class VoxelMath
    {
        static bool _loggedClamp;

        public static int FloorToIntClamped(double value)
        {
            if (value > int.MaxValue)
            {
                LogClampOnce(value);
                return int.MaxValue;
            }
            if (value < int.MinValue)
            {
                LogClampOnce(value);
                return int.MinValue;
            }
            return (int)Math.Floor(value);
        }

        static void LogClampOnce(double value)
        {
            if (_loggedClamp) return;
            _loggedClamp = true;
            Debug.LogWarning($"[VoxelMath] Clamped world coordinate {value} to int range. World size exceeds int32.");
        }
    }
}
