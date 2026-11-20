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
    [SerializeField] LayerMask aimMask = ~0;       // Environment + Breakable を設定

    [Header("入力")]
    [SerializeField]
    InputAction throwAction = new InputAction("Throw", InputActionType.Button, "<Mouse>/leftButton");

    Collider playerCollider;
    float nextThrowTime;

    void Awake()
    {
        playerCollider = GetComponent<Collider>();   // CharacterControllerもColliderを継承している
        if (aimCamera == null) aimCamera = Camera.main;

        // ゲームパッドのトリガーも使えるようにする(Awakeで1回だけ追加)
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

    void OnThrow(InputAction.CallbackContext _)
    {
        if (Time.time < nextThrowTime || kunaiPrefab == null || aimCamera == null) return;
        if (Cursor.lockState != CursorLockMode.Locked) return;   // カーソル解除中は投げない
        nextThrowTime = Time.time + cooldown;

        // 画面中央が指す点を求める
        Ray ray = aimCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        Vector3 target = Physics.Raycast(ray, out RaycastHit hit, maxAimDistance, aimMask,
                                         QueryTriggerInteraction.Ignore)
            ? hit.point
            : ray.origin + ray.direction * maxAimDistance;

        // 発射の基準点(プレイヤーの胸あたり)から、狙いの点へ向かう方向
        Vector3 origin = transform.position + Vector3.up * spawnHeight;
        Vector3 toTarget = target - origin;
        Vector3 dir = toTarget.normalized;

        // 狙いが背後や真横になってしまう場合(壁に近すぎる等)は、カメラの向きで投げる
        if (toTarget.sqrMagnitude < 0.01f || Vector3.Dot(dir, ray.direction) < 0.3f)
            dir = ray.direction;

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
        // 自分に当たらないように(コライダーが子オブジェクトにあっても対応)
        if (playerCollider != null)
        {
            foreach (var kCol in k.GetComponentsInChildren<Collider>())
                Physics.IgnoreCollision(kCol, playerCollider);
        }

        k.Launch(dir * throwSpeed);
    }

    // 選択中に、クナイの出現位置の目安(黄色い球)を表示する
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Vector3 p = transform.position + Vector3.up * spawnHeight + transform.forward * spawnForward;
        Gizmos.DrawWireSphere(p, 0.1f);
    }
}