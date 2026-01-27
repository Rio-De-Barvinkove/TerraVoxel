using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class PlayerSimpleController : MonoBehaviour
{
    [Header("Move")]
    public float moveSpeed = 6f;
    public float jumpHeight = 1.2f;
    public float gravity = -20f;
    public bool lockOnFocus = true;

    [Header("Look")]
    public Transform cameraPivot;
    public float lookSensitivity = 2f;
    public float maxPitch = 80f;

    CharacterController _cc;
    Vector3 _velocity;
    float _pitch;

    void Awake()
    {
        _cc = GetComponent<CharacterController>();
        if (cameraPivot == null && Camera.main != null)
            cameraPivot = Camera.main.transform;
        LockCursor(lockOnFocus);
    }

    void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus && lockOnFocus) LockCursor(true);
    }

    void Update()
    {
        HandleLook();
        HandleMove();

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            bool locked = Cursor.lockState == CursorLockMode.Locked;
            LockCursor(!locked);
        }

        if (Cursor.lockState != CursorLockMode.Locked && Input.GetMouseButtonDown(0))
        {
            LockCursor(true);
        }
    }

    void HandleLook()
    {
        float mx = Input.GetAxis("Mouse X") * lookSensitivity;
        float my = Input.GetAxis("Mouse Y") * lookSensitivity;

        transform.Rotate(Vector3.up * mx);

        _pitch -= my;
        _pitch = Mathf.Clamp(_pitch, -maxPitch, maxPitch);

        if (cameraPivot != null)
        {
            cameraPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }
    }

    void HandleMove()
    {
        bool grounded = _cc.isGrounded;
        if (grounded && _velocity.y < 0) _velocity.y = -2f;

        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");
        Vector3 input = new Vector3(h, 0f, v).normalized;
        Vector3 worldMove = transform.TransformDirection(input) * moveSpeed;

        _cc.Move(worldMove * Time.deltaTime);

        if (grounded && Input.GetButtonDown("Jump"))
        {
            _velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }

        _velocity.y += gravity * Time.deltaTime;
        _cc.Move(_velocity * Time.deltaTime);
    }

    void LockCursor(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }
}
