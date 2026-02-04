using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace TerraVoxel.Voxel.GPU
{
    /// <summary>
    /// Renders all visible chunks via DrawProceduralIndirect using instanced shader and GPU buffers.
    /// Call after GpuCuller.Cull. Uses a material instance with buffers set directly to avoid DX12 "buffer not provided" crash (MaterialPropertyBlock SRV binding can fail).
    /// If instanced material has no Texture2DArray set, call ConfigureFromVoxelMaterial(library) so terrain is not black/pink.
    /// </summary>
    public sealed class GpuDrivenRenderer : MonoBehaviour
    {
        [SerializeField] Material instancedMaterial;
        [Tooltip("Optional. If instanced material has no Texture Array, these are applied at runtime so terrain is not black/pink.")]
        [SerializeField] VoxelMaterialLibrary voxelMaterialLibrary;
        [Tooltip("Layer name (e.g. Default, Terrain). Used for culling and sorting.")]
        [SerializeField] string layerName = "Default";
        [Tooltip("Disable if you get D3D12 crash or nothing renders (shadow pass may not receive GPU buffers).")]
        [SerializeField] bool shadowCasting = false;
        [Tooltip("Log visible chunk count once per second. Enable temporarily for debugging (Visible: 0 = check Cull + meshing). Disable in production.")]
        [SerializeField] bool debugLog = false;
        [Tooltip("When true, draw is done by URP Render Feature (fixes DX12 buffer binding). Leave false if not using GpuDrivenRenderFeature.")]
        [SerializeField] bool drawViaRenderFeature = false;

        GpuWorldState _worldState;
        Material _materialInstance;
        MaterialPropertyBlock _properties;
        Bounds _bounds;
        static readonly uint[] _visibleCountReadback = new uint[1];
        float _debugLogNext;
        bool _materialConfigured;
        static bool _warnedNoTexture;

        /// <summary>Apply texture array and triplanar params from library so instanced material is not black/pink. Call from ChunkManager when GPU pipeline starts.</summary>
        public void ConfigureFromVoxelMaterial(VoxelMaterialLibrary library)
        {
            if (library == null) return;
            voxelMaterialLibrary = library;
            _materialConfigured = false;
            if (_materialInstance != null)
                ApplyVoxelMaterialToInstance();
        }

        void ApplyVoxelMaterialToInstance()
        {
            if (_materialInstance == null || voxelMaterialLibrary == null) return;
            if (voxelMaterialLibrary.TextureArray == null) return;
            _materialInstance.SetTexture("_MainTexArr", voxelMaterialLibrary.TextureArray);
            _materialInstance.SetFloat("_TriplanarScale", voxelMaterialLibrary.TriplanarScale);
            _materialInstance.SetFloat("_NormalStrength", voxelMaterialLibrary.NormalStrength);
            _materialInstance.SetInt("_LayerIndex", voxelMaterialLibrary.DefaultLayerIndex);
            _materialConfigured = true;
        }

        public void SetWorldState(GpuWorldState state)
        {
            _worldState = state;
            if (state != null)
            {
                float chunkWorldSize = state.ChunkSize * VoxelConstants.VoxelSize;
                float extent = state.MaxChunks * chunkWorldSize * 0.5f;
                _bounds = new Bounds(Vector3.zero, new Vector3(extent * 2, extent * 2, extent * 2));
            }
        }

        /// <summary>Render visible chunks. Call after Culler.Cull. Requires instancedMaterial and worldState set.</summary>
        public void Render(Camera camera)
        {
            if (_worldState == null || instancedMaterial == null || camera == null) return;

            // DX12 can fail to bind StructuredBuffers from MaterialPropertyBlock; set on material instance instead.
            if (_materialInstance == null)
            {
                _materialInstance = new Material(instancedMaterial);
                _materialConfigured = false;
            }
            if (!_materialConfigured && voxelMaterialLibrary != null)
                ApplyVoxelMaterialToInstance();
            if (!_materialConfigured && instancedMaterial != null && instancedMaterial.GetTexture("_MainTexArr") == null && !_warnedNoTexture)
            {
                _warnedNoTexture = true;
                Debug.LogWarning("[GpuDrivenRenderer] Instanced material has no Texture Array (_MainTexArr). Assign Voxel Material Library in Inspector or set texture on the material so terrain is not black/pink.");
            }

            if (_worldState.InstanceMatrices == null || _worldState.VisibleChunkIndices == null ||
                _worldState.ChunkDescriptors == null || _worldState.MeshVertexBuffer == null ||
                _worldState.MeshNormalBuffer == null || _worldState.DrawArgsBuffer == null ||
                _worldState.VisibleCountBuffer == null)
                return;

            _worldState.VisibleCountBuffer.GetData(_visibleCountReadback, 0, 0, 1);
            int visibleCount = (int)_visibleCountReadback[0];

            if (debugLog && Time.time >= _debugLogNext)
            {
                _debugLogNext = Time.time + 1f;
                bool ok = instancedMaterial != null && instancedMaterial.shader != null &&
                    instancedMaterial.shader.name.Contains("Instanced");
                if (!ok)
                    Debug.LogWarning("[GpuDrivenRenderer] Instanced Material must use shader 'TerraVoxel/VoxelTriplanarURP_Instanced'. Current: " + (instancedMaterial?.shader?.name ?? "null"));
                Debug.Log($"[GpuDrivenRenderer] Visible: {visibleCount}, ShadowCasting: {shadowCasting}" + (visibleCount <= 0 ? " (nothing to draw – check Cull + meshing)" : ""));
            }

            if (visibleCount <= 0)
                return;

            if (drawViaRenderFeature)
                return;

            int layer = LayerMask.NameToLayer(layerName);
            if (layer < 0) layer = 0;

            PrepareDrawBuffers();
            Graphics.DrawProceduralIndirect(
                _materialInstance,
                _bounds,
                MeshTopology.Triangles,
                _worldState.DrawArgsBuffer,
                0,
                camera,
                _properties,
                shadowCasting ? ShadowCastingMode.On : ShadowCastingMode.Off,
                false,
                layer
            );
        }

        void PrepareDrawBuffers()
        {
            _materialInstance.SetBuffer("_InstanceMatrices", _worldState.InstanceMatrices);
            _materialInstance.SetBuffer("_VisibleChunkIndices", _worldState.VisibleChunkIndices);
            _materialInstance.SetBuffer("_ChunkDescriptors", _worldState.ChunkDescriptors);
            _materialInstance.SetBuffer("_MeshVertexBuffer", _worldState.MeshVertexBuffer);
            _materialInstance.SetBuffer("_MeshNormalBuffer", _worldState.MeshNormalBuffer);
            if (_properties == null)
                _properties = new MaterialPropertyBlock();
            _properties.SetBuffer("_InstanceMatrices", _worldState.InstanceMatrices);
            _properties.SetBuffer("_VisibleChunkIndices", _worldState.VisibleChunkIndices);
            _properties.SetBuffer("_ChunkDescriptors", _worldState.ChunkDescriptors);
            _properties.SetBuffer("_MeshVertexBuffer", _worldState.MeshVertexBuffer);
            _properties.SetBuffer("_MeshNormalBuffer", _worldState.MeshNormalBuffer);
        }

        /// <summary>Record draw into a CommandBuffer (for URP RenderFeature). Call after Cull; returns true if draw was recorded.</summary>
        public bool RecordDrawToCommandBuffer(CommandBuffer cmd)
        {
            if (_worldState == null || instancedMaterial == null) return false;
            if (_materialInstance == null)
                _materialInstance = new Material(instancedMaterial);
            if (_worldState.InstanceMatrices == null || _worldState.VisibleChunkIndices == null ||
                _worldState.ChunkDescriptors == null || _worldState.MeshVertexBuffer == null ||
                _worldState.MeshNormalBuffer == null || _worldState.DrawArgsBuffer == null ||
                _worldState.VisibleCountBuffer == null)
                return false;

            _worldState.VisibleCountBuffer.GetData(_visibleCountReadback, 0, 0, 1);
            int visibleCount = (int)_visibleCountReadback[0];
            if (visibleCount <= 0) return false;

            PrepareDrawBuffers();
            cmd.DrawProceduralIndirect(Matrix4x4.identity, _materialInstance, -1, MeshTopology.Triangles, _worldState.DrawArgsBuffer, 0, _properties);
            return true;
        }

        void OnDestroy()
        {
            if (_materialInstance != null)
            {
                Destroy(_materialInstance);
                _materialInstance = null;
            }
        }
    }
}
