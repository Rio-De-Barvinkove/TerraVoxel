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

        MeshFilter _filter;
        MeshRenderer _renderer;
        MeshCollider _collider;
        Mesh _mesh;

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
            if (_mesh == null) return;
            MeshBuilder.Apply(_mesh, meshData);
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

            if (_collider == null)
                _collider = gameObject.AddComponent<MeshCollider>();

            _collider.enabled = true;
            if (_mesh != null && _mesh.vertexCount > 0)
            {
                _collider.sharedMesh = null;
                _collider.sharedMesh = _mesh;
            }
        }

        public void SetRendererEnabled(bool enabled)
        {
            if (_renderer == null) _renderer = gameObject.GetComponent<MeshRenderer>();
            if (_renderer != null) _renderer.enabled = enabled;
        }
    }
}

