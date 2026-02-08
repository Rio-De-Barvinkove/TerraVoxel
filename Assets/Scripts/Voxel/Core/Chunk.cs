using TerraVoxel.Voxel.Meshing;
using UnityEngine;

namespace TerraVoxel.Voxel.Core
{
    /// <summary>
    /// Runtime component for a chunk instance (mesh holder). When GPU-rendered (ApplyGpuMeshRef), the renderer is disabled and drawing is done by GpuDrivenRenderer.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class Chunk : MonoBehaviour
    {
        public ChunkCoord Coord { get; private set; }
        public ChunkData Data;
        public bool IsLowLod { get; set; }
        public double LodStartTime { get; set; }
        public int LodStep { get; set; }
        public bool UsesSvo { get; set; }

        MeshFilter _filter;
        MeshRenderer _renderer;
        MeshCollider _collider;
        BoxCollider _boxCollider;
        Mesh _mesh;
        bool _usingSharedMesh;
        bool _gpuRendered;

        /// <summary>Initialize with coord only; transform.position is not set (caller must set).</summary>
        public void Initialize(ChunkCoord coord)
        {
            Initialize(coord, 0f);
        }

        /// <summary>Initialize with coord and set transform.position to chunk center (coord + 0.5) * chunkWorldSize. Use chunkWorldSize = ChunkSize * VoxelSize.</summary>
        public void Initialize(ChunkCoord coord, float chunkWorldSize)
        {
            Coord = coord;
            if (chunkWorldSize > 0f)
            {
                float h = chunkWorldSize * 0.5f;
                transform.position = new Vector3(
                    coord.X * chunkWorldSize + h,
                    coord.Y * chunkWorldSize + h,
                    coord.Z * chunkWorldSize + h);
            }
            if (_filter == null) _filter = gameObject.GetComponent<MeshFilter>();
            if (_renderer == null) _renderer = gameObject.GetComponent<MeshRenderer>();
            if (_collider == null) _collider = gameObject.GetComponent<MeshCollider>();
            if (_mesh == null)
            {
                _mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                _mesh.MarkDynamic();
            }
            _filter.sharedMesh = _mesh;
            _usingSharedMesh = false;
            IsLowLod = false;
            LodStartTime = 0;
            LodStep = 1;
            UsesSvo = false;
            _mesh.Clear();
            if (_collider != null)
            {
                _collider.sharedMesh = null;
                _collider.enabled = false;
            }

            // Assign a default URP Lit material if none set.
            if (_renderer.sharedMaterial == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                _renderer.sharedMaterial = new Material(shader);
            }
        }

        /// <summary>Mark chunk as rendered by GPU at slot; disables Renderer to avoid double-draw. No-op if slot &lt; 0.</summary>
        public void ApplyGpuMeshRef(int slot)
        {
            if (slot < 0) return;
            Data.GpuSlot = slot;
            _gpuRendered = true;
            if (_renderer != null) _renderer.enabled = false;
        }

        /// <summary>Enable or disable BoxCollider for GPU path (no mesh on chunk; use box per chunk for collision). Chunk transform is at chunk center; box center (0,0,0) and size chunkWorldSize so world bounds match coord*chunkWorldSize..(coord+1)*chunkWorldSize.</summary>
        public void SetGpuBoxCollider(bool enabled, float chunkWorldSize)
        {
            if (enabled && chunkWorldSize > 0.001f)
            {
                if (_boxCollider == null) _boxCollider = gameObject.GetComponent<BoxCollider>();
                if (_boxCollider == null) _boxCollider = gameObject.AddComponent<BoxCollider>();
                _boxCollider.center = Vector3.zero;
                _boxCollider.size = new Vector3(chunkWorldSize, chunkWorldSize, chunkWorldSize);
                _boxCollider.enabled = true;
            }
            else if (_boxCollider != null)
            {
                _boxCollider.enabled = false;
            }
        }

        /// <summary>Clear GPU ref and re-enable Renderer for CPU rendering. Disables GPU box collider.</summary>
        public void ClearGpuMeshRef()
        {
            SetGpuBoxCollider(false, 0f);
            _gpuRendered = false;
            Data.GpuSlot = -1;
            Data.GpuOffset = -1;
            var mesh = GetRenderMesh();
            if (_renderer != null) _renderer.enabled = mesh != null && mesh.vertexCount > 0;
        }

        public bool IsGpuRendered => _gpuRendered;

        public void ApplyMesh(MeshData meshData, bool addCollider = false)
        {
            if (_gpuRendered) return;
            EnsureLocalMesh();
            if (_mesh == null) return;
            MeshBuilder.Apply(_mesh, meshData);
            
            // Ensure renderer is enabled if mesh has vertices
            if (_renderer == null) _renderer = gameObject.GetComponent<MeshRenderer>();
            if (_renderer != null)
            {
                _renderer.enabled = meshData.Vertices.Length > 0;
            }
            
            if (addCollider)
            {
                if (meshData.Vertices.Length == 0)
                {
                    if (_collider != null)
                    {
                        _collider.sharedMesh = null;
                        _collider.enabled = false;
                    }
                    return;
                }

                if (_collider == null)
                    _collider = gameObject.AddComponent<MeshCollider>();

                _collider.enabled = true;
                _collider.sharedMesh = null;
                _collider.sharedMesh = _mesh;
            }
        }

        public void SetColliderEnabled(bool enabled)
        {
            if (!enabled)
            {
                if (_collider != null)
                {
                    _collider.enabled = false;
                    _collider.sharedMesh = null;
                }
                return;
            }

            Mesh currentMesh = _usingSharedMesh && _filter != null ? _filter.sharedMesh : _mesh;
            if (currentMesh == null || currentMesh.vertexCount == 0)
            {
                if (_collider != null)
                {
                    _collider.enabled = false;
                    _collider.sharedMesh = null;
                }
                return;
            }

            if (_collider == null)
                _collider = gameObject.AddComponent<MeshCollider>();

            _collider.enabled = true;
            _collider.sharedMesh = null;
            _collider.sharedMesh = currentMesh;
        }

        public void SetRendererEnabled(bool enabled)
        {
            if (_renderer == null) _renderer = gameObject.GetComponent<MeshRenderer>();
            if (_renderer != null) _renderer.enabled = enabled;
        }

        /// <summary>Assign shared material for SRP Batching. Use sharedMaterial (not material) to avoid per-renderer instances.</summary>
        public void SetSharedMaterial(Material sharedMaterial)
        {
            if (sharedMaterial == null) return;
            if (_renderer == null) _renderer = gameObject.GetComponent<MeshRenderer>();
            if (_renderer != null) _renderer.sharedMaterial = sharedMaterial;
        }

        public void ApplySharedMesh(Mesh sharedMesh, bool addCollider = false)
        {
            if (sharedMesh == null) return;
            if (_gpuRendered) return;
            if (_filter == null) _filter = gameObject.GetComponent<MeshFilter>();
            _filter.sharedMesh = sharedMesh;
            _usingSharedMesh = true;

            // Ensure renderer is enabled if mesh has vertices
            if (_renderer == null) _renderer = gameObject.GetComponent<MeshRenderer>();
            if (_renderer != null)
            {
                _renderer.enabled = sharedMesh.vertexCount > 0;
            }

            if (addCollider)
            {
                if (sharedMesh.vertexCount == 0)
                {
                    if (_collider != null)
                    {
                        _collider.enabled = false;
                        _collider.sharedMesh = null;
                    }
                }
                else
                {
                    if (_collider == null)
                        _collider = gameObject.AddComponent<MeshCollider>();
                    _collider.enabled = true;
                    _collider.sharedMesh = null;
                    _collider.sharedMesh = sharedMesh;
                }
            }
            else if (_collider != null)
            {
                _collider.enabled = false;
                _collider.sharedMesh = null;
            }
        }

        public Mesh GetRenderMesh()
        {
            if (_filter == null) _filter = gameObject.GetComponent<MeshFilter>();
            return _filter != null ? _filter.sharedMesh : _mesh;
        }

        void EnsureLocalMesh()
        {
            if (_filter == null) _filter = gameObject.GetComponent<MeshFilter>();
            if (_mesh == null || _usingSharedMesh)
            {
                _mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                _mesh.MarkDynamic();
                if (_filter != null)
                    _filter.sharedMesh = _mesh;
                _usingSharedMesh = false;
            }
        }
    }
}

