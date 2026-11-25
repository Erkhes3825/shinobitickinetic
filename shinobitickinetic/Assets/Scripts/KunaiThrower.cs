using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 画面中央(照準)に向けてクナイを投げる。プレイヤーにアタッチする。
/// 出現位置は「プレイヤーの胸の高さ + 照準方向へ少し前」で計算するので、
/// Muzzle(発射口)用のオブジェクトは不要。
/// </summary>
public class KunaiThrower : MonoBehaviour
{
    [Header("参照")]
    [SerializeField] Camera aimCamera;
    [SerializeField] Kunai kunaiPrefab;

    [Header("出現位置")]
    [SerializeField] float spawnHeight = 0.5f;     // プレイヤーの原点からの高さ(Capsuleは原点が中心なので0.5で胸あたり)
    [SerializeField] float spawnForward = 0.8f;    // 照準方向へ、どれだけ前に出すか
    [SerializeField] bool faceAimWhenThrowing = true;  // 投げるとき、照準の方向を向く

    [Header("設定")]
    [SerializeField] float throwSpeed = 50f;
    [SerializeField] float cooldown = 0.4f;
    [SerializeField] float maxAimDistance = 60f;
    [SerializeField] bool limitDownAngle = true;   // 下向きに、投げすぎないようにする
    [SerializeField] float maxDownAngle = 10f;     // 下向きの上限(度)。手前の地面を狙っても、これ以上は下を向かない
    [SerializeField] LayerMask aimMask = ~0;       // 照準と、クナイが当たる対象。Environment + Breakable(敵に当てるなら Enemy も)

    [Header("デバッグ")]
    [SerializeField] bool logThrowSource = true;   // 何のキー/ボタンで投げたかを、Consoleに表示する

    // 入力はコードで定義する(Inspectorに保存されて、割り当てが勝手に変わるのを防ぐため)
    readonly InputAction throwAction = new InputAction("Throw", InputActionType.Button);

    float nextThrowTime;

    void Awake()
    {
        if (aimCamera == null) aimCamera = Camera.main;

        // 投げるボタン: マウス左クリック と ゲームパッドの右トリガー のみ
        throwAction.AddBinding("<Mouse>/leftButton");
        throwAction.AddBinding("<Gamepad>/rightTrigger");
    }

    void OnEnable()
    {
        throwAction.Enable();
        throwAction.performed += OnThrow;
    }

    void OnDisable()
    {
        throwAction.performed -= OnThrow;
        throwAction.Disable();
    }

    void OnThrow(InputAction.CallbackContext ctx)
    {
        if (logThrowSource && ctx.control != null)
            Debug.Log($"[KunaiThrower] 発射トリガー: {ctx.control.path}");

        if (Time.time < nextThrowTime || kunaiPrefab == null || aimCamera == null) return;
        if (Cursor.lockState != CursorLockMode.Locked) return;   // カーソル解除中は投げない
        nextThrowTime = Time.time + cooldown;

        // 自分(Playerレイヤー)は、狙いにも命中にも含めない(Aim Maskの設定ミス対策)。
        // ただし Default レイヤーのままの場合は、壁まで除外してしまうので、何もしない
        int selfLayer = gameObject.layer;
        LayerMask mask = selfLayer == 0 ? aimMask : (LayerMask)(aimMask.value & ~(1 << selfLayer));

        // 画面中央が指す点を求める
        Ray ray = aimCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        bool aimHit = Physics.Raycast(ray, out RaycastHit hit, maxAimDistance, mask, QueryTriggerInteraction.Ignore);
        Vector3 target = aimHit ? hit.point : ray.origin + ray.direction * maxAimDistance;

        // 発射の基準点(プレイヤーの胸あたり)から、狙いの点へ向かう方向
        Vector3 origin = transform.position + Vector3.up * spawnHeight;
        Vector3 toTarget = target - origin;
        Vector3 dir = toTarget.normalized;

        // 狙いが背後や真横になってしまう場合(壁に近すぎる等)は、カメラの向きで投げる
        if (toTarget.sqrMagnitude < 0.01f || Vector3.Dot(dir, ray.direction) < 0.3f)
            dir = ray.direction;

        // 下向きすぎる場合は、手前の地面に刺さらないよう、角度を制限する
        float downAngleBefore = Mathf.Asin(Mathf.Clamp(-dir.y, -1f, 1f)) * Mathf.Rad2Deg;
        if (limitDownAngle && downAngleBefore > maxDownAngle)
        {
            Vector3 flatDir = new Vector3(dir.x, 0f, dir.z);
            if (flatDir.sqrMagnitude > 0.0001f)
            {
                float a = maxDownAngle * Mathf.Deg2Rad;
                dir = (flatDir.normalized * Mathf.Cos(a) + Vector3.down * Mathf.Sin(a)).normalized;
            }
        }

        if (logThrowSource)
        {
            float downAngleAfter = Mathf.Asin(Mathf.Clamp(-dir.y, -1f, 1f)) * Mathf.Rad2Deg;
            string aimName = aimHit ? hit.collider.name : "何もなし";
            Debug.Log($"[KunaiThrower] 照準の先: {aimName} / 下向き {downAngleBefore:F1}° → {downAngleAfter:F1}°");
        }

        // 照準方向へ少し前に出す。ただし、目標が近いときは、目標を越えないようにする
        float forward = Mathf.Min(spawnForward, toTarget.magnitude * 0.5f);
        Vector3 spawnPos = origin + dir * forward;

        // プレイヤーを、投げる方向(水平方向)へ向ける
        if (faceAimWhenThrowing)
        {
            Vector3 flat = new Vector3(dir.x, 0f, dir.z);
            if (flat.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.LookRotation(flat);
        }

        Kunai k = Instantiate(kunaiPrefab, spawnPos, Quaternion.LookRotation(dir));
        // 当たる対象は mask のレイヤーだけ(自分の体には、当たらない)
        k.Launch(dir * throwSpeed, mask);
    }

    // 選択中に、クナイの出現位置の目安(黄色い球)を表示する
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Vector3 p = transform.position + Vector3.up * spawnHeight + transform.forward * spawnForward;
        Gizmos.DrawWireSphere(p, 0.1f);
    }
}