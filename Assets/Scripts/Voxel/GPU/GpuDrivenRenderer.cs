using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

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
        [Tooltip("When enabled, log DrawArgs (vertex count per instance, instance count, start vertex, start instance) once per second.")]
        [SerializeField] bool debugLogDrawArgs = false;
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
        static bool _warnedVisibleZero;
        static bool _warnedDrawViaFeature;

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

        /// <summary>Update bounds so DrawProceduralIndirect is not culled when camera is far from origin. Call before draw when camera is available.</summary>
        public void SetBoundsFromCamera(Camera camera)
        {
            if (camera == null) return;
            float s = Mathf.Max(camera.farClipPlane * 2f, 1000f);
            _bounds = new Bounds(camera.transform.position, new Vector3(s, s, s));
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

            if (visibleCount <= 0)
            {
                if (!drawViaRenderFeature && _worldState.ChunkCount > 0 && !_warnedVisibleZero)
                {
                    _warnedVisibleZero = true;
                    int sampleSlots = Mathf.Min(5, _worldState.MaxChunks);
                    var msg = new System.Text.StringBuilder("[GpuDrivenRenderer] Visible: 0 but ChunkCount > 0. Nothing will be drawn. First ").Append(sampleSlots).Append(" slots descriptor VertexCount: ");
                    for (int i = 0; i < sampleSlots; i++)
                    {
                        var d = _worldState.GetDescriptor(i);
                        msg.Append(i).Append("=").Append(d.VertexCount).Append(d.Flags != 0 ? "(F=" + d.Flags + ")" : "").Append(i < sampleSlots - 1 ? ", " : "");
                    }
                    msg.Append(". If all 0: GpuMesher did not produce geometry. Else: Cull may have synced late — check GpuCuller runs before Render.");
                    Debug.LogWarning(msg.ToString());
                }
                return;
            }

            if (drawViaRenderFeature)
            {
                if (!_warnedDrawViaFeature)
                {
                    _warnedDrawViaFeature = true;
                    Debug.LogWarning("[GpuDrivenRenderer] Draw Via Render Feature is on; this MonoBehaviour does not draw. Ensure GpuDrivenRenderFeature is on your URP Renderer (e.g. PC_Renderer); it will find this renderer automatically.");
                    if (visibleCount > 0)
                    {
                        uint[] firstSlot = new uint[1];
                        _worldState.VisibleChunkIndices.GetData(firstSlot, 0, 0, 1);
                        var d = _worldState.GetDescriptor((int)firstSlot[0]);
                        int sampleVisible = Mathf.Min(5, visibleCount);
                        uint[] visibleSlots = new uint[sampleVisible];
                        _worldState.VisibleChunkIndices.GetData(visibleSlots, 0, 0, sampleVisible);
                        var coordList = new System.Text.StringBuilder();
                        for (int i = 0; i < sampleVisible; i++)
                        {
                            var vd = _worldState.GetDescriptor((int)visibleSlots[i]);
                            coordList.Append('(').Append(vd.Coord.X).Append(',').Append(vd.Coord.Y).Append(',').Append(vd.Coord.Z).Append(')');
                            if (i < sampleVisible - 1) coordList.Append(", ");
                        }
                        int meshedCount = 0;
                        for (int i = 0; i < _worldState.MaxChunks; i++)
                        {
                            var desc = _worldState.GetDescriptor(i);
                            if (desc.VertexCount > 0 && (desc.Flags & ChunkDescriptorFlags.Empty) == 0)
                                meshedCount++;
                        }
                        Debug.Log($"[GpuDrivenRenderer] VisibleCount={visibleCount}; ChunkCount={_worldState.ChunkCount}; GPU slots with mesh (VertexCount>0, not Empty)={meshedCount}; first visible coord=({d.Coord.X},{d.Coord.Y},{d.Coord.Z}); visible coords sample: {coordList}. If meshedCount is low, GpuMesher is the bottleneck; if meshedCount is high but VisibleCount low, check frustum/culling.");

                        if (visibleCount < meshedCount && _worldState.ExpectedGenerationBuffer != null)
                        {
                            int checkSlots = Mathf.Min(_worldState.MaxChunks, 1024);
                            uint[] expectedGen = new uint[checkSlots];
                            _worldState.ExpectedGenerationBuffer.GetData(expectedGen, 0, 0, checkSlots);
                            int genMismatch = 0;
                            for (int i = 0; i < checkSlots; i++)
                            {
                                var desc = _worldState.GetDescriptor(i);
                                if (desc.VertexCount > 0 && (desc.Flags & ChunkDescriptorFlags.Empty) == 0 && desc.SlotGeneration != expectedGen[i])
                                    genMismatch++;
                            }
                            if (genMismatch > 0)
                                Debug.LogWarning($"[GpuDrivenRenderer] ExpectedGeneration mismatch: {genMismatch} of {meshedCount} meshed slots (checked {checkSlots}) have descriptor.SlotGeneration != ExpectedGeneration on GPU. Those chunks are culled in FrustumCull. Ensure ExpectedGenerationBuffer is updated only at AllocateChunk/FreeChunk and descriptor uploads preserve SlotGeneration.");
                            else
                            {
                                float chunkWorldSize = _worldState.ChunkSize * VoxelConstants.VoxelSize;
                                if (camera != null && chunkWorldSize > 0.001f)
                                {
                                    var planes = GeometryUtility.CalculateFrustumPlanes(camera);
                                    int cpuVisible = 0;
                                    int sample = 0;
                                    for (int i = 0; i < _worldState.MaxChunks && sample < 500; i++)
                                    {
                                        var desc = _worldState.GetDescriptor(i);
                                        if (desc.VertexCount == 0 || (desc.Flags & ChunkDescriptorFlags.Empty) != 0) continue;
                                        sample++;
                                        float cx = desc.Coord.X * chunkWorldSize + chunkWorldSize * 0.5f;
                                        float cy = desc.Coord.Y * chunkWorldSize + chunkWorldSize * 0.5f;
                                        float cz = desc.Coord.Z * chunkWorldSize + chunkWorldSize * 0.5f;
                                        var bounds = new Bounds(new Vector3(cx, cy, cz), new Vector3(chunkWorldSize, chunkWorldSize, chunkWorldSize));
                                        if (GeometryUtility.TestPlanesAABB(planes, bounds))
                                            cpuVisible++;
                                    }
                                    float camX = camera.transform.position.x, camY = camera.transform.position.y, camZ = camera.transform.position.z;
                                    float visX = d.Coord.X * chunkWorldSize + chunkWorldSize * 0.5f;
                                    float visY = d.Coord.Y * chunkWorldSize + chunkWorldSize * 0.5f;
                                    float visZ = d.Coord.Z * chunkWorldSize + chunkWorldSize * 0.5f;
                                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                                    float fov = camera.fieldOfView;
                                    float near = camera.nearClipPlane;
                                    float far = camera.farClipPlane;
                                    Debug.Log($"[GpuDrivenRenderer] CPU frustum: only {cpuVisible} of {sample} chunks in view. Camera pos=({camX.ToString("F1", inv)},{camY.ToString("F1", inv)},{camZ.ToString("F1", inv)}) FOV={fov} near={near} far={far}; visible chunk center=({visX.ToString("F1", inv)},{visY.ToString("F1", inv)},{visZ.ToString("F1", inv)}). If FOV is very small or near is large, widen FOV (e.g. 60-90) and lower near clip so more chunks pass culling.");
                                }
                                else
                                    Debug.Log($"[GpuDrivenRenderer] No ExpectedGeneration mismatch (checked {checkSlots}). Frustum or ChunkWorldSize_ may be wrong — verify GpuCuller receives correct camera and chunkWorldSize.");
                            }
                        }
                    }
                }
                return;
            }

            if (debugLog && Time.time >= _debugLogNext)
            {
                _debugLogNext = Time.time + 1f;
                bool ok = instancedMaterial != null && instancedMaterial.shader != null &&
                    instancedMaterial.shader.name.Contains("Instanced");
                if (!ok)
                    Debug.LogWarning("[GpuDrivenRenderer] Instanced Material must use shader 'TerraVoxel/VoxelTriplanarURP_Instanced'. Current: " + (instancedMaterial?.shader?.name ?? "null"));
                string coordInfo = "";
                if (visibleCount > 0 && _worldState.VisibleChunkIndices != null)
                {
                    uint[] firstSlot = new uint[1];
                    _worldState.VisibleChunkIndices.GetData(firstSlot, 0, 0, 1);
                    var d = _worldState.GetDescriptor((int)firstSlot[0]);
                    coordInfo = $"; first visible slot={firstSlot[0]} coord=({d.Coord.X},{d.Coord.Y},{d.Coord.Z}) (if coord=0,0,0 and camera elsewhere, terrain appears as distant clump)";
                }
                Debug.Log($"[GpuDrivenRenderer] Visible: {visibleCount}, ShadowCasting: {shadowCasting}{coordInfo}" + (visibleCount <= 0 ? " (nothing to draw – check Cull + meshing)" : ""));
            }

            int layer = LayerMask.NameToLayer(layerName);
            if (layer < 0) layer = 0;

            SetBoundsFromCamera(camera);
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
            _materialInstance.SetInt("_MaxVerticesPerInstance", _worldState.MaxVerticesPerChunk);
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
            if (debugLogDrawArgs && Time.time >= _debugLogNext)
            {
                _debugLogNext = Time.time + 1f;
                uint[] args = new uint[5];
                _worldState.DrawArgsBuffer.GetData(args);
                Debug.Log($"[GpuDrivenRenderer] DrawArgs: {args[0]}, {args[1]}, {args[2]}, {args[3]} (vertexCount/instance, instanceCount, startVertex, startInstance)");
            }
        }

        /// <summary>Record draw into a CommandBuffer (for URP RenderFeature compatibility mode). Call after Cull; returns true if draw was recorded.</summary>
        public bool RecordDrawToCommandBuffer(CommandBuffer cmd)
        {
            if (!RecordDrawPrepare()) return false;
            cmd.DrawProceduralIndirect(Matrix4x4.identity, _materialInstance, -1, MeshTopology.Triangles, _worldState.DrawArgsBuffer, 0, _properties);
            return true;
        }

        /// <summary>Record draw into a RasterCommandBuffer (for URP RenderGraph pass). Call after Cull; returns true if draw was recorded.</summary>
        public bool RecordDrawToCommandBuffer(RasterCommandBuffer cmd)
        {
            if (!RecordDrawPrepare()) return false;
            cmd.DrawProceduralIndirect(Matrix4x4.identity, _materialInstance, -1, MeshTopology.Triangles, _worldState.DrawArgsBuffer, 0, _properties);
            return true;
        }

        bool RecordDrawPrepare()
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
