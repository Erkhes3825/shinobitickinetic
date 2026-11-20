using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// カメラ基準の3人称移動(WASD / ゲームパッド左スティック + ジャンプ)。
/// CharacterControllerと一緒にプレイヤーにアタッチする。
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class PlayerController : MonoBehaviour
{
    [Header("参照")]
    [SerializeField] Transform cameraTransform;   // Main Camera を設定

    [Header("移動")]
    [SerializeField] float moveSpeed = 6f;
    [SerializeField] float rotationSpeed = 12f;

    [Header("ジャンプと重力")]
    [SerializeField] float jumpHeight = 1.5f;
    [SerializeField] float gravity = -25f;

    CharacterController cc;
    float verticalVelocity;

    InputAction moveAction = new InputAction("Move", InputActionType.Value);
    InputAction jumpAction = new InputAction("Jump", InputActionType.Button);

    void Awake()
    {
        cc = GetComponent<CharacterController>();
        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        moveAction.AddCompositeBinding("2DVector")
            .With("Up", "<Keyboard>/w")
            .With("Down", "<Keyboard>/s")
            .With("Left", "<Keyboard>/a")
            .With("Right", "<Keyboard>/d");
        moveAction.AddBinding("<Gamepad>/leftStick");

        jumpAction.AddBinding("<Keyboard>/space");
        jumpAction.AddBinding("<Gamepad>/buttonSouth");
    }

    void OnEnable()
    {
        moveAction.Enable();
        jumpAction.Enable();
    }

    void OnDisable()
    {
        moveAction.Disable();
        jumpAction.Disable();
    }

    void Start()
    {
        // マウスでカメラを回すためにカーソルを固定(Escで解除)
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void Update()
    {
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        // --- 水平移動(カメラの向き基準) ---
        Vector2 input = moveAction.ReadValue<Vector2>();

        Vector3 camForward = Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up).normalized;
        Vector3 camRight   = Vector3.ProjectOnPlane(cameraTransform.right, Vector3.up).normalized;
        Vector3 moveDir = camForward * input.y + camRight * input.x;
        moveDir = Vector3.ClampMagnitude(moveDir, 1f);

        if (moveDir.sqrMagnitude > 0.001f)
        {
            Quaternion target = Quaternion.LookRotation(moveDir);
            transform.rotation = Quaternion.Slerp(transform.rotation, target,
                                                  rotationSpeed * Time.deltaTime);
        }

        // --- 垂直方向(重力・ジャンプ) ---
        if (cc.isGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f;   // 接地を保つための小さな押し付け

        if (cc.isGrounded && jumpAction.WasPressedThisFrame())
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);

        verticalVelocity += gravity * Time.deltaTime;

        Vector3 velocity = moveDir * moveSpeed + Vector3.up * verticalVelocity;
        cc.Move(velocity * Time.deltaTime);
    }

    /// <summary>ワープ後などに落下速度をリセットしたいとき用。</summary>
    public void ResetVerticalVelocity() => verticalVelocity = 0f;
}
