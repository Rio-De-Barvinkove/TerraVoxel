using UnityEngine;

namespace TerraVoxel.Voxel.GPU
{
    /// <summary>
    /// Optional GPU erosion (Phase 9). Stub; wire Erosion.compute and tick when implementing full erosion.
    /// </summary>
    public sealed class GpuErosionSimulator
    {
        ComputeShader _shader;
        int _kernelCommit;

        public bool IsValid => _shader != null && _kernelCommit >= 0;

        public void Initialize(ComputeShader shader)
        {
            _shader = shader;
            _kernelCommit = _shader != null ? _shader.FindKernel("CommitChanges") : -1;
        }

        /// <summary>Optional tick. No-op until erosion logic is implemented.</summary>
        public void Tick(GpuWorldState state)
        {
            if (!IsValid || state == null) return;
            // Placeholder: dispatch CommitChanges per dirty chunk when erosion is implemented.
        }
    }
}
