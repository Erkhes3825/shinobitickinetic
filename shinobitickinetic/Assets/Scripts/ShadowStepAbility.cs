using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 影渡り: カメラ中心が指す地点が影の中なら、そこへワープする。
/// プレイヤー(CharacterController付き)にアタッチして使う。
///
/// 判定に使うレイヤーは Inspector で指定できる。空(0)のときは
/// シーンのレイヤー設定に依存せず自動で決める(地形が Default でも
/// Environment でも動くようにするため)。
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class ShadowStepAbility : MonoBehaviour
{
    [Header("参照")]
    [SerializeField] Camera aimCamera;
    [SerializeField] Transform marker;            // 着地予定地を示す仮オブジェクト(球など)
    [SerializeField] Renderer markerRenderer;

    [Header("判定")]
    [SerializeField] LayerMask groundMask;        // ワープ先になれる面。空(0)なら自動(地形の全レイヤー)
    [SerializeField] LayerMask occluderMask;      // 影の遮蔽物。空(0)なら groundMask と同じ
    [SerializeField] float maxRange = 25f;        // カメラ中心から、ワープ先を探す距離
    [SerializeField, Range(0f, 1f)] float minGroundNormalY = 0.7f;   // 床とみなす面の傾き(1=真上向き。壁の側面は除外)
    [SerializeField] float shadowProbeHeight = 1f;                   // 影かどうかを調べる高さ(足元から)。敵の視界判定と同じ胸の高さ
    [SerializeField, Range(0.5f, 1f)] float clearanceScale = 0.9f;   // 着地点に体が収まるかの判定の厳しさ
    [SerializeField] bool requireClearPath = true;                   // 壁や扉ごしにワープしない

    [Header("コスト")]
    [SerializeField] float cooldown = 0.6f;

    [Header("入力")]
    [SerializeField]
    InputAction stepAction = new InputAction("ShadowStep", InputActionType.Button, "<Keyboard>/f");

    // RaycastNonAlloc 用の使い回しバッファ(GC負荷を避ける)
    static readonly RaycastHit[] AimHits = new RaycastHit[32];
    static readonly RaycastHit[] PathHits = new RaycastHit[32];
    static readonly Collider[] FitHits = new Collider[16];

    static readonly int BaseColorID = Shader.PropertyToID("_BaseColor");
    static readonly int ColorID = Shader.PropertyToID("_Color");

    CharacterController cc;
    ShinobiPlayerController playerController;
    LayerMask aimMask;      // groundMask の実効値(ワープ先になれる面)
    LayerMask solidMask;    // 地形全体(プレイヤー・敵・UIは除く)。壁ごし/埋まり判定用
    LayerMask fitMask;      // solidMask + 敵(着地点に体が収まるかの判定)
    float nextStepTime;
    Vector3 targetPoint;
    bool targetValid;
    MaterialPropertyBlock mpb;

    void Awake()
    {
        cc = GetComponent<CharacterController>();
        playerController = GetComponent<ShinobiPlayerController>();
        if (aimCamera == null) aimCamera = Camera.main;

        // 空(0)なら自動で決める(Inspector で指定した場合はそちらを優先)
        if (groundMask.value == 0) groundMask = BuildSolidMask();
        if (occluderMask.value == 0) occluderMask = groundMask;

        aimMask = groundMask;
        solidMask = BuildSolidMask();   // 壁ごし/埋まり判定は groundMask の設定に依らず地形全体で見る
        fitMask = solidMask.value | EnemyLayerMask().value;

        mpb = new MaterialPropertyBlock();
        HideMarker();
    }

    // 地形が Default でも Environment でも、シーンの設定に関係なく当たるようにする
    static LayerMask BuildSolidMask()
    {
        int mask = Physics.DefaultRaycastLayers;   // Ignore Raycast 以外の全レイヤー
        mask &= ~(1 << 3);                         // Player
        int ui = LayerMask.NameToLayer("UI");
        if (ui >= 0) mask &= ~(1 << ui);
        mask &= ~EnemyLayerMask().value;
        return mask;
    }

    static LayerMask EnemyLayerMask()
    {
        int enemy = LayerMask.NameToLayer("Enemy");
        return enemy >= 0 ? (1 << enemy) : 0;
    }

    void OnEnable()
    {
        stepAction.Enable();
        stepAction.performed += OnStep;
    }

    void OnDisable()
    {
        stepAction.performed -= OnStep;
        stepAction.Disable();
        targetValid = false;
        HideMarker();
    }

    void Update()
    {
        UpdateAim();
    }

    void UpdateAim()
    {
        targetValid = false;

        // カメラが無い/ゲームが終わっている時は何も指さない
        if (aimCamera == null || (GameManager.Instance != null && !GameManager.Instance.IsPlaying))
        {
            HideMarker();
            return;
        }

        Ray ray = aimCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        if (!TryGetAimHit(ray, out RaycastHit hit))
        {
            HideMarker();
            return;
        }

        Vector3 feet = hit.point + hit.normal * 0.05f;
        targetPoint = feet;

        bool isFloor = hit.normal.y >= minGroundNormalY;                      // 壁の側面には、ワープしない
        bool inRange = Vector3.Distance(transform.position, feet) <= maxRange + 0.5f;
        bool inShadow = IsInShadowAt(feet);
        bool canFit = HasRoomAt(feet);
        bool pathClear = !requireClearPath || HasClearPathTo(feet);

        targetValid = isFloor && inRange && inShadow && canFit && pathClear;

        ShowMarker(feet, targetValid);
    }

    /// <summary>カメラ中心のレイに当たった、自分以外の一番手前のコライダーを返す。</summary>
    bool TryGetAimHit(Ray ray, out RaycastHit result)
    {
        result = default;

        int count = Physics.RaycastNonAlloc(ray, AimHits, maxRange, aimMask, QueryTriggerInteraction.Ignore);
        float nearest = float.MaxValue;
        bool found = false;

        for (int i = 0; i < count; i++)
        {
            RaycastHit h = AimHits[i];
            if (h.collider == null) continue;
            if (IsSelf(h.collider.transform)) continue;
            if (IsMarker(h.collider.transform)) continue;
            if (h.distance >= nearest) continue;

            nearest = h.distance;
            result = h;
            found = true;
        }

        return found;
    }

    /// <summary>その地点は影の中か。敵の視界判定(ShadowUtility)と同じ規則で調べる。</summary>
    bool IsInShadowAt(Vector3 feet)
    {
        Vector3 probe = feet + Vector3.up * shadowProbeHeight;
        return ShadowUtility.IsInShadow(probe, LightRegistry.All, occluderMask);
    }

    /// <summary>その地点に、プレイヤーの体がめり込まずに立てるか。</summary>
    bool HasRoomAt(Vector3 feet)
    {
        Vector3 bottom = feet + Vector3.up * (cc.radius + 0.05f);
        Vector3 top = feet + Vector3.up * (cc.height - cc.radius);
        float radius = cc.radius * clearanceScale;

        // 自分の体(プレイヤー自身)は障害物として数えない
        int count = Physics.OverlapCapsuleNonAlloc(bottom, top, radius, FitHits, fitMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            Collider c = FitHits[i];
            if (c == null || c.transform == null) continue;
            if (IsSelf(c.transform)) continue;
            if (IsMarker(c.transform)) continue;
            return false;   // 壁や敵の体が重なっている
        }

        return true;
    }

    /// <summary>今いる場所から着地点まで、壁などに遮られていないか(壁ごしのワープ防止)。</summary>
    bool HasClearPathTo(Vector3 feet)
    {
        Vector3 start = transform.position + Vector3.up * 0.1f;
        Vector3 end = feet + Vector3.up * (cc.height * 0.5f - cc.center.y);

        Vector3 delta = end - start;
        float distance = delta.magnitude;
        if (distance < 0.05f) return true;

        int count = Physics.RaycastNonAlloc(new Ray(start, delta / distance), PathHits,
                                            distance, solidMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            RaycastHit h = PathHits[i];
            if (h.collider == null) continue;
            if (IsSelf(h.collider.transform)) continue;
            if (IsMarker(h.collider.transform)) continue;
            return false;   // 途中に障害物がある
        }

        return true;
    }

    /// <summary>自分(プレイヤー)のコライダーかどうか。</summary>
    bool IsSelf(Transform t)
    {
        return t == transform || t.IsChildOf(transform);
    }

    /// <summary>着地点マーカーのコライダーかどうか。</summary>
    bool IsMarker(Transform t)
    {
        return marker != null && (t == marker || t.IsChildOf(marker));
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
        Vector3 pivotOffset = Vector3.up * (cc.height * 0.5f - cc.center.y);
        Vector3 pivotPosition = feetPosition + pivotOffset + Vector3.up * (cc.skinWidth + 0.01f);

        // CharacterControllerは無効化しないと位置を直接書き換えられない
        cc.enabled = false;
        transform.position = pivotPosition;
        cc.enabled = true;

        if (playerController != null) playerController.ResetVerticalVelocity();

        targetValid = false;
        HideMarker();

        // TODO: 消える/現れるVFX、SE、短い無敵時間など
    }

    // ---------- マーカー表示 ----------
    void ShowMarker(Vector3 position, bool valid)
    {
        if (marker == null) return;

        if (!marker.gameObject.activeSelf) marker.gameObject.SetActive(true);
        marker.position = position + Vector3.up * 0.02f;

        if (markerRenderer == null) return;
        Color color = valid ? Color.cyan : Color.red;
        markerRenderer.GetPropertyBlock(mpb);
        mpb.SetColor(BaseColorID, color);   // URP Lit
        mpb.SetColor(ColorID, color);       // 互換用
        markerRenderer.SetPropertyBlock(mpb);
    }

    void HideMarker()
    {
        if (marker != null && marker.gameObject.activeSelf)
            marker.gameObject.SetActive(false);
    }

    // Sceneビューで、ワープできる距離を確認するためのギズモ
    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 0.9f, 1f, 0.6f);
        Gizmos.DrawWireSphere(transform.position, maxRange);
    }
}
