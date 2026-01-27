using System.Collections.Generic;
using TerraVoxel.Voxel.Core;
using TerraVoxel.Voxel.Streaming;
using UnityEngine;
using UnityEngine.Rendering;

namespace TerraVoxel.Voxel.Save
{
    public class VoxelModDebugInput : MonoBehaviour
    {
        enum BrushShape
        {
            Cube,
            Sphere,
            Cylinder,
            Rectangle
        }

        enum InteractionMode
        {
            Dig,
            Build
        }

        [Header("References")]
        [SerializeField] ChunkModManager modManager;
        [SerializeField] ChunkManager chunkManager;
        [SerializeField] Camera rayCamera;
        [SerializeField] Transform rangeOrigin;

        [Header("Toggle")]
        [SerializeField] KeyCode toggleKey = KeyCode.B;
        [SerializeField] KeyCode modeToggleKey = KeyCode.V;

        [Header("Range")]
        [SerializeField] bool useChunkDistance = true;
        [SerializeField] float maxDistanceChunks = 2f;
        [SerializeField] float maxDistance = 8f;
        [SerializeField] LayerMask hitMask = ~0;

        [Header("Brush")]
        [SerializeField] BrushShape brushShape = BrushShape.Cube;
        [SerializeField] int digBrushSize = 1;
        [SerializeField] int buildBrushSize = 1;
        [SerializeField] int brushMin = 1;
        [SerializeField] int brushMax = 10;
        [SerializeField] float rectangleHeightScale = 0.5f;
        [SerializeField] ushort buildMaterial = (ushort)VoxelMaterial.Dirt;

        [Header("Input")]
        [SerializeField] float scrollStep = 1f;

        [Header("Preview")]
        [SerializeField] bool showPreview = true;
        [SerializeField] Color breakColor = new Color(1f, 0.35f, 0.2f, 1f);
        [SerializeField] Color buildColor = new Color(0.3f, 0.8f, 1f, 1f);
        [SerializeField] float outlinePadding = 0.001f;

        bool _interactionEnabled;
        InteractionMode _mode = InteractionMode.Dig;
        GameObject _previewGo;
        Mesh _previewMesh;
        MeshFilter _previewFilter;
        MeshRenderer _previewRenderer;
        readonly List<Vector3> _lineVertices = new List<Vector3>(4096);
        readonly List<int> _lineIndices = new List<int>(4096);
        readonly List<Vector3Int> _voxels = new List<Vector3Int>(4096);

        void Awake()
        {
            if (modManager == null) modManager = GetComponent<ChunkModManager>();
            if (chunkManager == null) chunkManager = GetComponent<ChunkManager>();
            if (rayCamera == null) rayCamera = Camera.main;
            if (rangeOrigin == null)
                rangeOrigin = rayCamera != null ? rayCamera.transform : (chunkManager != null ? chunkManager.PlayerTransform : null);

            EnsurePreview();
        }

        void OnDestroy()
        {
            if (_previewGo != null)
                Destroy(_previewGo);
        }

        void Update()
        {
            if (modManager == null) return;

            if (Input.GetKeyDown(toggleKey))
                _interactionEnabled = !_interactionEnabled;

            if (!_interactionEnabled)
            {
                ClearPreview();
                return;
            }

            if (Input.GetKeyDown(modeToggleKey))
                _mode = _mode == InteractionMode.Dig ? InteractionMode.Build : InteractionMode.Dig;

            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 0.01f)
            {
                int step = scroll > 0f ? 1 : -1;
                int nextSize = Mathf.Clamp(GetActiveBrushSize() + step * Mathf.RoundToInt(scrollStep), brushMin, brushMax);
                SetActiveBrushSize(nextSize);
            }

            if (Input.GetKeyDown(KeyCode.Alpha1)) brushShape = BrushShape.Cube;
            if (Input.GetKeyDown(KeyCode.Alpha2)) brushShape = BrushShape.Sphere;
            if (Input.GetKeyDown(KeyCode.Alpha3)) brushShape = BrushShape.Cylinder;
            if (Input.GetKeyDown(KeyCode.Alpha4) || Input.GetKeyDown(KeyCode.Alpha5)) brushShape = BrushShape.Rectangle;

            bool buildPreview = _mode == InteractionMode.Build;

            if (showPreview)
                UpdatePreview(buildPreview);
            else
                ClearPreview();

            if (Input.GetMouseButtonDown(0))
                ApplyEdit(breakBlock: _mode == InteractionMode.Dig);
        }

        void ApplyEdit(bool breakBlock)
        {
            if (!TryGetHit(out var hit)) return;

            Vector3Int centerVoxel = GetTargetVoxel(hit, breakBlock);
            _voxels.Clear();
            CollectVoxels(centerVoxel, brushShape, GetActiveBrushSize(), _voxels);

            if (_voxels.Count == 0) return;

            ushort material = breakBlock ? (ushort)VoxelMaterial.Air : buildMaterial;
            modManager.SetVoxelsWorld(_voxels, material, includeNeighbors: true);
        }

        void UpdatePreview(bool buildPreview)
        {
            if (!TryGetHit(out var hit))
            {
                ClearPreview();
                return;
            }

            Vector3Int centerVoxel = GetTargetVoxel(hit, breakBlock: !buildPreview);
            _voxels.Clear();
            CollectVoxels(centerVoxel, brushShape, GetActiveBrushSize(), _voxels);

            if (_voxels.Count == 0)
            {
                ClearPreview();
                return;
            }

            EnsurePreview();
            ResetPreviewTransform();
            BuildPreviewLines(buildPreview);
            ApplyPreviewMesh(buildPreview ? buildColor : breakColor);
        }

        void BuildPreviewLines(bool buildPreview)
        {
            _lineVertices.Clear();
            _lineIndices.Clear();

            float size = VoxelConstants.VoxelSize;
            float pad = outlinePadding;

            for (int i = 0; i < _voxels.Count; i++)
            {
                Vector3Int v = _voxels[i];
                if (!buildPreview && !IsSolidVoxel(v.x, v.y, v.z))
                    continue;

                Vector3 min = new Vector3(v.x * size, v.y * size, v.z * size);
                Vector3 max = min + Vector3.one * size;
                min -= Vector3.one * pad;
                max += Vector3.one * pad;

                if (buildPreview)
                {
                    AddBoxLines(min, max);
                    continue;
                }

                AddVisibleFaceLines(v, min, max);
            }
        }

        void AddVisibleFaceLines(Vector3Int v, Vector3 min, Vector3 max)
        {
            if (!IsSolidVoxel(v.x - 1, v.y, v.z)) AddFaceLines(min, max, FaceDirection.NegX);
            if (!IsSolidVoxel(v.x + 1, v.y, v.z)) AddFaceLines(min, max, FaceDirection.PosX);
            if (!IsSolidVoxel(v.x, v.y - 1, v.z)) AddFaceLines(min, max, FaceDirection.NegY);
            if (!IsSolidVoxel(v.x, v.y + 1, v.z)) AddFaceLines(min, max, FaceDirection.PosY);
            if (!IsSolidVoxel(v.x, v.y, v.z - 1)) AddFaceLines(min, max, FaceDirection.NegZ);
            if (!IsSolidVoxel(v.x, v.y, v.z + 1)) AddFaceLines(min, max, FaceDirection.PosZ);
        }

        enum FaceDirection
        {
            NegX,
            PosX,
            NegY,
            PosY,
            NegZ,
            PosZ
        }

        void AddFaceLines(Vector3 min, Vector3 max, FaceDirection face)
        {
            Vector3 v0;
            Vector3 v1;
            Vector3 v2;
            Vector3 v3;
            switch (face)
            {
                case FaceDirection.NegX:
                    v0 = new Vector3(min.x, min.y, min.z);
                    v1 = new Vector3(min.x, min.y, max.z);
                    v2 = new Vector3(min.x, max.y, max.z);
                    v3 = new Vector3(min.x, max.y, min.z);
                    break;
                case FaceDirection.PosX:
                    v0 = new Vector3(max.x, min.y, min.z);
                    v1 = new Vector3(max.x, min.y, max.z);
                    v2 = new Vector3(max.x, max.y, max.z);
                    v3 = new Vector3(max.x, max.y, min.z);
                    break;
                case FaceDirection.NegY:
                    v0 = new Vector3(min.x, min.y, min.z);
                    v1 = new Vector3(max.x, min.y, min.z);
                    v2 = new Vector3(max.x, min.y, max.z);
                    v3 = new Vector3(min.x, min.y, max.z);
                    break;
                case FaceDirection.PosY:
                    v0 = new Vector3(min.x, max.y, min.z);
                    v1 = new Vector3(max.x, max.y, min.z);
                    v2 = new Vector3(max.x, max.y, max.z);
                    v3 = new Vector3(min.x, max.y, max.z);
                    break;
                case FaceDirection.NegZ:
                    v0 = new Vector3(min.x, min.y, min.z);
                    v1 = new Vector3(max.x, min.y, min.z);
                    v2 = new Vector3(max.x, max.y, min.z);
                    v3 = new Vector3(min.x, max.y, min.z);
                    break;
                case FaceDirection.PosZ:
                    v0 = new Vector3(min.x, min.y, max.z);
                    v1 = new Vector3(max.x, min.y, max.z);
                    v2 = new Vector3(max.x, max.y, max.z);
                    v3 = new Vector3(min.x, max.y, max.z);
                    break;
                default:
                    return;
            }

            AddLine(v0, v1);
            AddLine(v1, v2);
            AddLine(v2, v3);
            AddLine(v3, v0);
        }

        void AddBoxLines(Vector3 min, Vector3 max)
        {
            Vector3 a = new Vector3(min.x, min.y, min.z);
            Vector3 b = new Vector3(max.x, min.y, min.z);
            Vector3 c = new Vector3(max.x, min.y, max.z);
            Vector3 d = new Vector3(min.x, min.y, max.z);
            Vector3 e = new Vector3(min.x, max.y, min.z);
            Vector3 f = new Vector3(max.x, max.y, min.z);
            Vector3 g = new Vector3(max.x, max.y, max.z);
            Vector3 h = new Vector3(min.x, max.y, max.z);

            AddLine(a, b);
            AddLine(b, c);
            AddLine(c, d);
            AddLine(d, a);
            AddLine(e, f);
            AddLine(f, g);
            AddLine(g, h);
            AddLine(h, e);
            AddLine(a, e);
            AddLine(b, f);
            AddLine(c, g);
            AddLine(d, h);
        }

        void AddLine(Vector3 a, Vector3 b)
        {
            int index = _lineVertices.Count;
            _lineVertices.Add(a);
            _lineVertices.Add(b);
            _lineIndices.Add(index);
            _lineIndices.Add(index + 1);
        }

        void ApplyPreviewMesh(Color color)
        {
            if (_previewMesh == null) return;
            _previewMesh.Clear();
            _previewMesh.SetVertices(_lineVertices);
            _previewMesh.SetIndices(_lineIndices, MeshTopology.Lines, 0);
            if (_previewRenderer != null && _previewRenderer.sharedMaterial != null)
                _previewRenderer.sharedMaterial.color = color;
        }

        void EnsurePreview()
        {
            if (_previewMesh != null) return;

            _previewGo = new GameObject("VoxelSelectionPreview");
            _previewGo.transform.SetParent(null, false);
            ResetPreviewTransform();
            _previewFilter = _previewGo.AddComponent<MeshFilter>();
            _previewRenderer = _previewGo.AddComponent<MeshRenderer>();
            _previewMesh = new Mesh { name = "VoxelSelectionPreview" };
            _previewMesh.indexFormat = IndexFormat.UInt32;
            _previewFilter.sharedMesh = _previewMesh;

            var shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            _previewRenderer.sharedMaterial = new Material(shader);
        }

        void ResetPreviewTransform()
        {
            if (_previewGo == null) return;
            _previewGo.transform.position = Vector3.zero;
            _previewGo.transform.rotation = Quaternion.identity;
            _previewGo.transform.localScale = Vector3.one;
        }

        void ClearPreview()
        {
            if (_previewMesh == null) return;
            _previewMesh.Clear();
        }

        bool TryGetHit(out RaycastHit hit)
        {
            hit = default;
            if (rayCamera == null) rayCamera = Camera.main;
            if (rayCamera == null) return false;

            float maxDist = GetMaxDistance();
            Ray ray = rayCamera.ScreenPointToRay(Input.mousePosition);
            if (!Physics.Raycast(ray, out hit, maxDist, hitMask, QueryTriggerInteraction.Ignore))
                return false;

            if (rangeOrigin != null)
            {
                float maxSqr = maxDist * maxDist;
                if ((hit.point - rangeOrigin.position).sqrMagnitude > maxSqr)
                    return false;
            }

            return true;
        }

        float GetMaxDistance()
        {
            if (useChunkDistance)
            {
                int chunkSize = chunkManager != null && chunkManager.ChunkSize > 0 ? chunkManager.ChunkSize : VoxelConstants.ChunkSize;
                return Mathf.Max(0.1f, maxDistanceChunks * chunkSize * VoxelConstants.VoxelSize);
            }
            return Mathf.Max(0.1f, maxDistance);
        }

        Vector3Int GetTargetVoxel(RaycastHit hit, bool breakBlock)
        {
            float offset = VoxelConstants.VoxelSize * 0.5f + 0.001f;
            Vector3 targetPos = breakBlock
                ? hit.point - hit.normal * offset
                : hit.point + hit.normal * offset;

            return WorldToVoxel(targetPos);
        }

        void CollectVoxels(Vector3Int center, BrushShape shape, int size, List<Vector3Int> output)
        {
            if (size <= 0) return;

            int min;
            int max;
            GetRange(size, out min, out max);

            switch (shape)
            {
                case BrushShape.Cube:
                    CollectBox(center, min, max, min, max, min, max, output);
                    break;
                case BrushShape.Sphere:
                    CollectSphere(center, min, max, size, output);
                    break;
                case BrushShape.Cylinder:
                    CollectCylinder(center, min, max, size, output);
                    break;
                case BrushShape.Rectangle:
                    int height = Mathf.Max(1, Mathf.RoundToInt(size * rectangleHeightScale));
                    GetRange(height, out int minY, out int maxY);
                    CollectBox(center, min, max, minY, maxY, min, max, output);
                    break;
            }
        }

        void CollectBox(Vector3Int center, int minX, int maxX, int minY, int maxY, int minZ, int maxZ, List<Vector3Int> output)
        {
            for (int y = minY; y <= maxY; y++)
                for (int z = minZ; z <= maxZ; z++)
                    for (int x = minX; x <= maxX; x++)
                        output.Add(new Vector3Int(center.x + x, center.y + y, center.z + z));
        }

        void CollectSphere(Vector3Int center, int min, int max, int size, List<Vector3Int> output)
        {
            float radius = size / 2f;
            float radiusSq = radius * radius;

            for (int y = min; y <= max; y++)
                for (int z = min; z <= max; z++)
                    for (int x = min; x <= max; x++)
                    {
                        float dx = x;
                        float dy = y;
                        float dz = z;
                        if (dx * dx + dy * dy + dz * dz <= radiusSq)
                            output.Add(new Vector3Int(center.x + x, center.y + y, center.z + z));
                    }
        }

        void CollectCylinder(Vector3Int center, int min, int max, int size, List<Vector3Int> output)
        {
            float radius = size / 2f;
            float radiusSq = radius * radius;

            for (int y = min; y <= max; y++)
                for (int z = min; z <= max; z++)
                    for (int x = min; x <= max; x++)
                    {
                        float dx = x;
                        float dz = z;
                        if (dx * dx + dz * dz <= radiusSq)
                            output.Add(new Vector3Int(center.x + x, center.y + y, center.z + z));
                    }
        }

        void GetRange(int size, out int min, out int max)
        {
            min = -((size - 1) / 2);
            max = min + size - 1;
        }

        int GetActiveBrushSize()
        {
            return _mode == InteractionMode.Build ? buildBrushSize : digBrushSize;
        }

        void SetActiveBrushSize(int size)
        {
            if (_mode == InteractionMode.Build)
                buildBrushSize = size;
            else
                digBrushSize = size;
        }

        bool IsSolidVoxel(int wx, int wy, int wz)
        {
            if (!TryGetVoxelMaterial(wx, wy, wz, out ushort material))
                return false;
            return material != (ushort)VoxelMaterial.Air;
        }

        bool TryGetVoxelMaterial(int wx, int wy, int wz, out ushort material)
        {
            material = (ushort)VoxelMaterial.Air;
            if (chunkManager == null) return false;

            int chunkSize = chunkManager.ChunkSize > 0 ? chunkManager.ChunkSize : VoxelConstants.ChunkSize;
            ChunkCoord coord = WorldToChunk(wx, wy, wz, chunkSize);
            int lx = wx - coord.X * chunkSize;
            int ly = wy - coord.Y * chunkSize;
            int lz = wz - coord.Z * chunkSize;
            if (lx < 0 || ly < 0 || lz < 0 || lx >= chunkSize || ly >= chunkSize || lz >= chunkSize)
                return false;

            if (!chunkManager.TryGetChunk(coord, out var chunk) || chunk == null || !chunk.Data.IsCreated)
                return false;

            int index = lx + chunkSize * (ly + chunkSize * lz);
            if (index < 0 || index >= chunk.Data.Materials.Length)
                return false;

            material = chunk.Data.Materials[index];
            return true;
        }

        Vector3Int WorldToVoxel(Vector3 worldPos)
        {
            double inv = 1d / VoxelConstants.VoxelSize;
            int wx = VoxelMath.FloorToIntClamped(worldPos.x * inv);
            int wy = VoxelMath.FloorToIntClamped(worldPos.y * inv);
            int wz = VoxelMath.FloorToIntClamped(worldPos.z * inv);
            return new Vector3Int(wx, wy, wz);
        }

        ChunkCoord WorldToChunk(int wx, int wy, int wz, int chunkSize)
        {
            int cx = VoxelMath.FloorToIntClamped((double)wx / chunkSize);
            int cy = VoxelMath.FloorToIntClamped((double)wy / chunkSize);
            int cz = VoxelMath.FloorToIntClamped((double)wz / chunkSize);
            return new ChunkCoord(cx, cy, cz);
        }
    }
}

