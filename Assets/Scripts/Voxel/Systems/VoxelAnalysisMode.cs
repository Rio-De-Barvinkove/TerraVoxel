using System.Collections.Generic;
using TerraVoxel.Voxel.Streaming;
using UnityEngine;

namespace TerraVoxel.Voxel.Systems
{
    public class VoxelAnalysisMode : MonoBehaviour
    {
        [Header("Toggle")]
        [SerializeField] KeyCode toggleKey = KeyCode.F2;
        [SerializeField] bool lockCursorInAnalysis = true;
        [SerializeField] bool lockCursorOnExit = true;
        [SerializeField] bool allowInReleaseBuilds = true;

        [Header("References")]
        [SerializeField] ChunkManager chunkManager;
        [SerializeField] Transform analysisRoot;
        [SerializeField] Transform analysisCamera;

        [Header("Analysis Settings")]
        [SerializeField] int analysisLoadRadius = 6;
        [SerializeField] int analysisMaxSpawns = 8;
        [SerializeField] bool analysisAddColliders = false;

        [Header("Fly")]
        [SerializeField] float flySpeed = 10f;
        [SerializeField] float flyBoost = 3f;
        [SerializeField] float lookSensitivity = 2f;
        [SerializeField] float maxPitch = 85f;

        [Header("Shadows")]
        [SerializeField] bool disableShadowsInAnalysis = true;
        [SerializeField] KeyCode toggleShadowsKey = KeyCode.F4;

        [Header("Disable While Analysis")]
        [SerializeField] List<MonoBehaviour> disableBehaviours = new List<MonoBehaviour>();
        [SerializeField] CharacterController disableCharacterController;
        [SerializeField] bool freezeStreaming = true;

        bool _analysis;
        CursorLockMode _prevLockState;
        bool _prevCursorVisible;
        int _savedRadius;
        int _savedMaxSpawns;
        bool _savedAddColliders;
        float _yaw;
        float _pitch;
        bool _shadowsForced;
        ShadowQuality _prevShadowQuality;
        float _prevShadowDistance;

        void Awake()
        {
            if (chunkManager == null) chunkManager = FindObjectOfType<ChunkManager>();
            if (analysisRoot == null && chunkManager != null) analysisRoot = chunkManager.PlayerTransform;
            if (analysisCamera == null && Camera.main != null) analysisCamera = Camera.main.transform;
            if (analysisRoot != null) _yaw = analysisRoot.eulerAngles.y;
            if (analysisCamera != null) _pitch = analysisCamera.localEulerAngles.x;

            if (disableCharacterController == null && analysisRoot != null)
                disableCharacterController = analysisRoot.GetComponent<CharacterController>();
        }

        void Update()
        {
            if (!IsAllowed()) return;
            if (Input.GetKeyDown(toggleKey))
                Toggle();

            if (_analysis)
            {
                if (Input.GetKeyDown(toggleShadowsKey))
                    ToggleShadows();
                UpdateFly();
            }
        }

        void Toggle()
        {
            _analysis = !_analysis;

            if (chunkManager != null)
            {
                if (_analysis)
                {
                    _savedRadius = chunkManager.LoadRadius;
                    _savedMaxSpawns = chunkManager.MaxSpawnsPerFrame;
                    _savedAddColliders = chunkManager.AddColliders;
                    chunkManager.SetRuntimeSettings(analysisLoadRadius, analysisMaxSpawns, analysisAddColliders);
                }
                else
                {
                    chunkManager.SetRuntimeSettings(_savedRadius, _savedMaxSpawns, _savedAddColliders);
                }
            }

            if (freezeStreaming && chunkManager != null)
                chunkManager.SetStreamingPaused(_analysis);

            if (disableCharacterController != null)
                disableCharacterController.enabled = !_analysis;

            foreach (var b in disableBehaviours)
                if (b != null) b.enabled = !_analysis;

            if (lockCursorInAnalysis)
            {
                if (_analysis)
                {
                    _prevLockState = Cursor.lockState;
                    _prevCursorVisible = Cursor.visible;
                    SetCursorLocked(true);
                }
                else
                {
                    if (lockCursorOnExit)
                        SetCursorLocked(true);
                    else
                        RestoreCursorState();
                }
            }

            if (_analysis && disableShadowsInAnalysis)
                ApplyShadows(false);
            else if (!_analysis)
                ApplyShadows(true);
        }

        bool IsAllowed()
        {
            return Application.isEditor || Debug.isDebugBuild || allowInReleaseBuilds;
        }

        void UpdateFly()
        {
            if (analysisRoot == null) return;

            float mx = Input.GetAxis("Mouse X") * lookSensitivity;
            float my = Input.GetAxis("Mouse Y") * lookSensitivity;

            _yaw += mx;
            _pitch -= my;
            _pitch = Mathf.Clamp(_pitch, -maxPitch, maxPitch);

            analysisRoot.rotation = Quaternion.Euler(0f, _yaw, 0f);
            if (analysisCamera != null)
                analysisCamera.localRotation = Quaternion.Euler(_pitch, 0f, 0f);

            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");
            float up = 0f;
            if (Input.GetKey(KeyCode.E)) up += 1f;
            if (Input.GetKey(KeyCode.Q)) up -= 1f;

            Vector3 input = new Vector3(h, up, v).normalized;
            float speed = flySpeed * (Input.GetKey(KeyCode.LeftShift) ? flyBoost : 1f);
            Vector3 move = analysisRoot.TransformDirection(input) * speed * Time.deltaTime;
            analysisRoot.position += move;
        }

        void SetCursorLocked(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        void RestoreCursorState()
        {
            Cursor.lockState = _prevLockState;
            Cursor.visible = _prevCursorVisible;
        }

        void ToggleShadows()
        {
            ApplyShadows(_shadowsForced);
        }

        void ApplyShadows(bool enable)
        {
            if (enable)
            {
                if (_shadowsForced)
                {
                    QualitySettings.shadows = _prevShadowQuality;
                    QualitySettings.shadowDistance = _prevShadowDistance;
                    _shadowsForced = false;
                }
                return;
            }

            if (!_shadowsForced)
            {
                _prevShadowQuality = QualitySettings.shadows;
                _prevShadowDistance = QualitySettings.shadowDistance;
            }

            QualitySettings.shadows = ShadowQuality.Disable;
            QualitySettings.shadowDistance = 0f;
            _shadowsForced = true;
        }
    }
}

