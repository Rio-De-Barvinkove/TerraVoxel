using TerraVoxel.Voxel.Meshing;
using UnityEngine;

namespace TerraVoxel.Voxel.Core
{
    /// <summary>
    /// Runtime component for a chunk instance (mesh holder).
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
        Mesh _mesh;
        bool _usingSharedMesh;

        public void Initialize(ChunkCoord coord)
        {
            Coord = coord;
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

        public void ApplyMesh(MeshData meshData, bool addCollider = false)
        {
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

        public void ApplySharedMesh(Mesh sharedMesh, bool addCollider = false)
        {
            if (sharedMesh == null) return;
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

