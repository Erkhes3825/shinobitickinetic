using UnityEngine;

/// <summary>
/// 投げられるクナイ。物理エンジンに頼らず、毎フレーム「進む先」を調べながら、
/// 真っ直ぐ飛ぶ。何かに当たったら、その場所に刺さって止まる。
/// 壊せるライトなら Break() を呼ぶ。
/// 自分の体・プレイヤーとは、そもそも当たり判定をしないので、弾かれない。
/// </summary>
public class Kunai : MonoBehaviour
{
    [Header("寿命")]
    [SerializeField] float lifetime = 8f;          // 飛んでいる間の寿命(何にも当たらなかった場合)
    [SerializeField] float stuckLifetime = 10f;    // 刺さってから消えるまで

    [Header("刺さり方")]
    [SerializeField] float tipOffset = 0.25f;      // クナイの中心から、先端までの長さ(長さ0.5のCubeなら0.25)
    [SerializeField] float embedDepth = 0.15f;     // 先端が、壁にめり込む深さ
    [SerializeField] float hitRadius = 0.05f;      // 当たり判定の太さ(大きいほど、細い物にも当たりやすい)

    [Header("デバッグ")]
    [SerializeField] bool logHits = true;          // 当たった物と距離を、Consoleに表示する

    Vector3 direction;
    float speed;
    LayerMask hitMask;
    bool flying;
    Vector3 launchPos;

    void Awake()
    {
        // 物理には任せない。Prefabに Rigidbody / Collider が残っていても、無効にする
        var rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = true;
            rb.useGravity = false;
        }
        foreach (var c in GetComponentsInChildren<Collider>())
            c.enabled = false;
    }

    /// <param name="velocity">飛ぶ向きと速さ</param>
    /// <param name="mask">当たる対象のレイヤー(自分の体は含めない)</param>
    public void Launch(Vector3 velocity, LayerMask mask)
    {
        speed = velocity.magnitude;
        direction = velocity.normalized;
        hitMask = mask;
        launchPos = transform.position;
        transform.rotation = Quaternion.LookRotation(direction);
        flying = true;
        Invoke(nameof(DestroySelf), lifetime);
    }

    void Update()
    {
        if (!flying) return;

        float step = speed * Time.deltaTime;
        Vector3 tip = transform.position + direction * tipOffset;

        // 先端から、今フレームで進む距離の間に、何かあるか調べる
        if (Physics.SphereCast(tip, hitRadius, direction, out RaycastHit hit, step,
                               hitMask, QueryTriggerInteraction.Ignore))
        {
            Stick(hit);
            return;
        }

        transform.position += direction * step;
    }

    void Stick(RaycastHit hit)
    {
        flying = false;

        if (logHits)
        {
            float dist = Vector3.Distance(launchPos, hit.point);
            Debug.Log($"[Kunai] 命中: {hit.collider.name}(距離 {dist:F1}m)");
        }

        var breakable = hit.collider.GetComponentInParent<BreakableLight>();
        if (breakable != null) breakable.Break();

        // 当たった場所に、先端がめり込むように置く
        transform.position = hit.point - direction * (tipOffset - embedDepth);

        // 飛んでいる間の寿命タイマーを止めて、刺さった後の寿命に切り替える
        CancelInvoke(nameof(DestroySelf));
        Invoke(nameof(DestroySelf), stuckLifetime);
    }

    void DestroySelf()
    {
        Destroy(gameObject);
    }
}