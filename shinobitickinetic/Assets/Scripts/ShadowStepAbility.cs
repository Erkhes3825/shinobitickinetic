using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 影渡り: カメラ中心が指す地点が影の中なら、そこへワープする。
/// プレイヤー(CharacterController付き)にアタッチして使う。
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class ShadowStepAbility : MonoBehaviour
{
    [Header("参照")]
    [SerializeField] Camera aimCamera;
    [SerializeField] Transform marker;            // 着地予定地を示す仮オブジェクト(球など)
    [SerializeField] Renderer markerRenderer;

    [Header("判定")]
    [SerializeField] LayerMask groundMask = ~0;   // ワープ先にできる面
    [SerializeField] LayerMask occluderMask = ~0; // 影を作る遮蔽物
    [SerializeField] float maxRange = 25f;
    [SerializeField, Range(0f, 1f)] float minGroundNormalY = 0.7f;   // 床とみなす面の傾き(1=真上向き。壁の側面は除外)

    [Header("コスト")]
    [SerializeField] float cooldown = 0.6f;

    [Header("入力")]
    [SerializeField]
    InputAction stepAction = new InputAction("ShadowStep", InputActionType.Button, "<Keyboard>/f");

    CharacterController cc;
    ShinobiPlayerController playerController;
    float nextStepTime;
    Vector3 targetPoint;
    bool targetValid;

    void Awake()
    {
        cc = GetComponent<CharacterController>();
        playerController = GetComponent<ShinobiPlayerController>();
        if (aimCamera == null) aimCamera = Camera.main;
    }

    void OnEnable() { stepAction.Enable(); stepAction.performed += OnStep; }
    void OnDisable() { stepAction.performed -= OnStep; stepAction.Disable(); }

    void Update()
    {
        UpdateAim();
    }

    void UpdateAim()
    {
        targetValid = false;

        // 自分(Playerレイヤー)は、照準の判定に含めない。Defaultレイヤーのままの場合は、壁まで除外するので何もしない
        int selfLayer = gameObject.layer;
        LayerMask mask = selfLayer == 0 ? groundMask : (LayerMask)(groundMask.value & ~(1 << selfLayer));

        Ray ray = aimCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        if (Physics.Raycast(ray, out RaycastHit hit, maxRange, mask,
                            QueryTriggerInteraction.Ignore))
        {
            // 面から少し浮かせた位置で影判定(自己遮蔽を避ける)
            Vector3 probe = hit.point + hit.normal * 0.1f;
            bool inShadow = ShadowUtility.IsInShadow(probe, LightRegistry.All, occluderMask);

            // 立てるだけの広さがあるかも簡易チェック
            bool hasRoom = !Physics.CheckCapsule(
                probe + Vector3.up * cc.radius,
                probe + Vector3.up * (cc.height - cc.radius),
                cc.radius * 0.9f, occluderMask, QueryTriggerInteraction.Ignore);

            targetPoint = hit.point + hit.normal * 0.05f;
            bool isFloor = hit.normal.y >= minGroundNormalY;   // 壁の側面には、ワープしない
            targetValid = inShadow && hasRoom && isFloor;

            if (marker != null)
            {
                marker.gameObject.SetActive(true);
                marker.position = targetPoint;
            }
        }
        else if (marker != null)
        {
            marker.gameObject.SetActive(false);
        }

        if (markerRenderer != null)
            markerRenderer.material.color = targetValid ? Color.cyan : Color.red;
    }

    void OnStep(InputAction.CallbackContext _)
    {
        if (!targetValid || Time.time < nextStepTime) return;
        nextStepTime = Time.time + cooldown;
        Teleport(targetPoint);
    }

    void Teleport(Vector3 feetPosition)
    {
        // transform.position は足元ではなく、Capsuleの中心にある。
        // 足元が目標の地点に来るように、中心の位置を計算する(体が地面に埋まらないように)
        Vector3 pivotOffset = Vector3.up * (cc.height * 0.5f) - cc.center;
        Vector3 pivotPosition = feetPosition + pivotOffset + Vector3.up * cc.skinWidth;

        // CharacterControllerは無効化しないと位置を直接書き換えられない
        cc.enabled = false;
        transform.position = pivotPosition;
        cc.enabled = true;

        if (playerController != null) playerController.ResetVerticalVelocity();

        // TODO: 消える/現れるVFX、SE、短い無敵時間など
    }
}