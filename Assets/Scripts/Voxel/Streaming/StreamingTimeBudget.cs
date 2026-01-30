using UnityEngine;

namespace TerraVoxel.Voxel.Streaming
{
    /// <summary>
    /// Frame-time budget for streaming. If not using Burst/Jobs for gen/mesh,
    /// consider moving heavy work into Jobs and using this budget to cap main-thread time.
    /// </summary>
    [System.Serializable]
    public class StreamingTimeBudget
    {
        [SerializeField] bool enabled;
        [SerializeField] float maxMsPerFrame = 2f;

        [System.NonSerialized] double _frameStart;

        public bool Enabled => enabled && maxMsPerFrame > 0f;

        public void BeginFrame()
        {
            if (Enabled)
                _frameStart = Time.realtimeSinceStartupAsDouble;
        }

        public bool IsExceeded()
        {
            if (!Enabled) return false;
            double elapsedMs = (Time.realtimeSinceStartupAsDouble - _frameStart) * 1000.0;
            return elapsedMs >= maxMsPerFrame;
        }
    }
}
