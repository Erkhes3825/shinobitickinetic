using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 忍者プレイヤーの統合コントローラ。
/// 移動 / ジャンプ / ロックオン / 瞬身ダッシュ / 変わり身の術 / カーソル固定を担当する。
/// (影渡りは ShadowStepAbility、クナイは KunaiThrower が別に担当)
/// 入力は Input System を使用する。
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class ShinobiPlayerController : MonoBehaviour
{
    [Header("基本移動設定")]
    [SerializeField] private float moveSpeed = 8.0f;
    [SerializeField] private float rotationSpeed = 15.0f;
    [SerializeField] private float gravity = -20.0f;
    [SerializeField] private float jumpHeight = 2.0f;

    [Header("ロックオン設定")]
    [SerializeField] private float lockOnRange = 25.0f;
    [SerializeField] private LayerMask enemyLayer;   // 敵のレイヤー(例: Enemy)を設定
    private Transform currentTarget;

    [Header("瞬身ダッシュ設定")]
    [SerializeField] private float dashSpeed = 40.0f;
    [SerializeField] private float dashStoppingDistance = 2.0f;
    [SerializeField] private float dashMaxDuration = 1.5f;   // 壁に阻まれても必ず終わる安全装置
    private bool isDashing = false;

    [Header("変わり身の術設定")]
    [SerializeField] private GameObject logPrefab;   // 丸太のプレハブ(未割り当てでも動作します)
    [SerializeField] private float substitutionDistance = 3.0f;
    [SerializeField] private float slowMotionDuration = 1.0f;

    [Header("カメラ・カーソル")]
    [SerializeField] private Transform cameraTransform;   // 空なら Main Camera を自動取得
    [SerializeField] private bool lockCursor = true;      // マウスでカメラを回すため固定する

    private CharacterController controller;
    private float verticalVelocity;
    private Coroutine slowMotionCoroutine;

    // --- 入力(キーボード / マウス / ゲームパッド) ---
    private readonly InputAction moveAction = new InputAction("Move", InputActionType.Value);
    private readonly InputAction jumpAction = new InputAction("Jump", InputActionType.Button);
    private readonly InputAction lockOnAction = new InputAction("LockOn", InputActionType.Button);
    private readonly InputAction dashAction = new InputAction("Dash", InputActionType.Button);
    private readonly InputAction substituteAction = new InputAction("Substitute", InputActionType.Button);

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
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

        lockOnAction.AddBinding("<Keyboard>/tab");
        lockOnAction.AddBinding("<Mouse>/middleButton");
        lockOnAction.AddBinding("<Gamepad>/rightStickPress");

        dashAction.AddBinding("<Keyboard>/leftShift");
        dashAction.AddBinding("<Keyboard>/e");
        dashAction.AddBinding("<Gamepad>/rightShoulder");

        substituteAction.AddBinding("<Keyboard>/q");
        substituteAction.AddBinding("<Gamepad>/leftShoulder");
    }

    private void OnEnable()
    {
        moveAction.Enable();
        jumpAction.Enable();
        lockOnAction.Enable();
        dashAction.Enable();
        substituteAction.Enable();
    }

    private void OnDisable()
    {
        moveAction.Disable();
        jumpAction.Disable();
        lockOnAction.Disable();
        dashAction.Disable();
        substituteAction.Disable();

        isDashing = false;

        // スローモーション中に無効化/シーン再読み込みされても、時間が遅いままにならないようにする
        if (slowMotionCoroutine != null)
        {
            Time.timeScale = 1.0f;
            slowMotionCoroutine = null;
        }
    }

    private void Start()
    {
        if (lockCursor)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    private void Update()
    {
        HandleCursor();
        HandleLockOnInput();

        if (isDashing) return; // ダッシュ中は通常の移動入力をスキップ

        HandleSubstitutionInput();
        HandleDashInput();
        HandleMovement();
    }

    // --- 0. カーソル固定(Escで解除、ゲーム画面クリックで再固定) ---
    private void HandleCursor()
    {
        if (!lockCursor) return;

        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else if (Cursor.lockState != CursorLockMode.Locked
                 && Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    // --- 1. ロックオン機能 ---
    private void HandleLockOnInput()
    {
        if (!lockOnAction.WasPressedThisFrame()) return;

        if (currentTarget == null)
        {
            FindTarget();
        }
        else
        {
            currentTarget = null; // 解除
            Debug.Log("ロックオン解除");
        }
    }

    private void FindTarget()
    {
        if (enemyLayer.value == 0)
        {
            Debug.LogWarning("Enemy Layer が未設定です。敵のレイヤーを、Inspectorの Enemy Layer に設定してください。");
            return;
        }

        Collider[] enemies = Physics.OverlapSphere(transform.position, lockOnRange, enemyLayer);
        float closestDistance = Mathf.Infinity;
        Transform closestEnemy = null;

        foreach (var enemy in enemies)
        {
            float distance = Vector3.Distance(transform.position, enemy.transform.position);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestEnemy = enemy.transform;
            }
        }

        if (closestEnemy != null)
        {
            currentTarget = closestEnemy;
            Debug.Log($"ロックオン完了: {currentTarget.name}");
        }
    }

    // --- 2. 瞬身ダッシュ(チャクラダッシュ) ---
    private void HandleDashInput()
    {
        if (dashAction.WasPressedThisFrame() && currentTarget != null && !isDashing)
        {
            StartCoroutine(PerformInstantDash());
        }
    }

    private IEnumerator PerformInstantDash()
    {
        isDashing = true;
        verticalVelocity = 0f;
        float elapsed = 0f;

        while (currentTarget != null && elapsed < dashMaxDuration)
        {
            Vector3 direction = currentTarget.position - transform.position;
            direction.y = 0; // 高さは並行移動
            float distance = direction.magnitude;

            if (distance <= dashStoppingDistance) break;

            // 敵へ向かって回転&高速移動
            transform.rotation = Quaternion.Slerp(transform.rotation,
                Quaternion.LookRotation(direction), Time.deltaTime * rotationSpeed);
            controller.Move(direction.normalized * dashSpeed * Time.deltaTime);

            elapsed += Time.deltaTime;
            yield return null;
        }

        isDashing = false;
    }

    // --- 3. 変わり身の術 ---
    private void HandleSubstitutionInput()
    {
        if (substituteAction.WasPressedThisFrame())
        {
            PerformSubstitution();
        }
    }

    private void PerformSubstitution()
    {
        // 1. 現在位置に丸太を生成(あれば)
        if (logPrefab != null)
        {
            Instantiate(logPrefab, transform.position, transform.rotation);
        }

        // 2. 移動先を計算(ロックオン中は敵の背後、なければ自分の後ろ)
        Vector3 destination;
        Quaternion rotation = transform.rotation;

        if (currentTarget != null)
        {
            destination = currentTarget.position - currentTarget.forward * substitutionDistance;
            Vector3 look = currentTarget.position - destination;
            look.y = 0f;
            if (look.sqrMagnitude > 0.001f) rotation = Quaternion.LookRotation(look);
        }
        else
        {
            destination = transform.position - transform.forward * substitutionDistance;
        }

        controller.enabled = false;
        transform.SetPositionAndRotation(destination, rotation);
        controller.enabled = true;
        verticalVelocity = 0f;

        // 3. 一時的なスローモーション演出(連打しても重ならないようにする)
        if (slowMotionCoroutine != null) StopCoroutine(slowMotionCoroutine);
        slowMotionCoroutine = StartCoroutine(SlowMotionRoutine());
        Debug.Log("変わり身の術 発動!");
    }

    private IEnumerator SlowMotionRoutine()
    {
        Time.timeScale = 0.2f;
        yield return new WaitForSecondsRealtime(slowMotionDuration);
        Time.timeScale = 1.0f;
        slowMotionCoroutine = null;
    }

    // --- 4. 通常移動 & 重力処理 ---
    private void HandleMovement()
    {
        bool isGrounded = controller.isGrounded;
        if (isGrounded && verticalVelocity < 0f)
        {
            verticalVelocity = -2f;
        }

        Vector2 input = moveAction.ReadValue<Vector2>();
        Vector3 inputDir = Vector3.ClampMagnitude(new Vector3(input.x, 0f, input.y), 1f);

        Vector3 horizontalVelocity = Vector3.zero;

        if (inputDir.magnitude >= 0.1f)
        {
            // カメラの向き基準で移動ベクトルを作成
            float targetAngle = Mathf.Atan2(inputDir.x, inputDir.z) * Mathf.Rad2Deg + GetCameraYaw();
            Vector3 moveDir = Quaternion.Euler(0f, targetAngle, 0f) * Vector3.forward;

            // ロックオン中でなければ進行方向を向く
            if (currentTarget == null)
            {
                Quaternion targetRotation = Quaternion.Euler(0f, targetAngle, 0f);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation,
                                                      Time.deltaTime * rotationSpeed);
            }

            horizontalVelocity = moveDir.normalized * (moveSpeed * inputDir.magnitude);
        }

        // ロックオン中の敵の方向を向く
        if (currentTarget != null)
        {
            Vector3 lookDir = currentTarget.position - transform.position;
            lookDir.y = 0;
            if (lookDir != Vector3.zero)
            {
                transform.rotation = Quaternion.Slerp(transform.rotation,
                    Quaternion.LookRotation(lookDir), Time.deltaTime * rotationSpeed);
            }
        }

        // ジャンプ
        if (isGrounded && jumpAction.WasPressedThisFrame())
        {
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }

        // 重力適用(水平と垂直を1回の Move にまとめて、接地判定を安定させる)
        verticalVelocity += gravity * Time.deltaTime;
        Vector3 velocity = horizontalVelocity + Vector3.up * verticalVelocity;
        controller.Move(velocity * Time.deltaTime);
    }

    private float GetCameraYaw()
    {
        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;
        return cameraTransform != null ? cameraTransform.eulerAngles.y : 0f;
    }

    /// <summary>ワープ後などに落下速度をリセットしたいとき用(ShadowStepAbilityから呼ばれる)。</summary>
    public void ResetVerticalVelocity() => verticalVelocity = 0f;

    private void OnDrawGizmosSelected()
    {
        // 開発用にロックオン範囲を赤い線で視覚化
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, lockOnRange);
    }
}