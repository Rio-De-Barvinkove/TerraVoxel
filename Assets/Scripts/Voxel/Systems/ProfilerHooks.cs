using System.Diagnostics;

namespace TerraVoxel.Voxel.Systems
{
    /// <summary>
    /// Lightweight stopwatch helper.
    /// </summary>
    public struct ProfilerScope
    {
        readonly Stopwatch _sw;

        public ProfilerScope(bool start)
        {
            _sw = new Stopwatch();
            if (start) _sw.Start();
        }

        public void Restart() => _sw.Restart();
        public long ElapsedMilliseconds => _sw.ElapsedMilliseconds;
    }
}


