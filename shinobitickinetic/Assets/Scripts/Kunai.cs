using UnityEngine;

/// <summary>
/// 投げられるクナイ。何かに当たったら、その場所に刺さって止まり、
/// 壊せるライトなら Break() を呼ぶ。
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class Kunai : MonoBehaviour
{
    [Header("寿命")]
    [SerializeField] float lifetime = 8f;          // 飛んでいる間の寿命(何にも当たらなかった場合)
    [SerializeField] float stuckLifetime = 10f;    // 刺さってから消えるまで

    [Header("刺さり方")]
    [SerializeField] float tipOffset = 0.25f;      // クナイの中心から、先端までの長さ(長さ0.5のCubeなら0.25)
    [SerializeField] float embedDepth = 0.15f;     // 先端が、壁にめり込む深さ

    [Header("デバッグ")]
    [SerializeField] bool logHits = true;          // 当たった物と距離を、Consoleに表示する

    Rigidbody rb;
    bool hasHit;
    Vector3 launchPos;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();

        // 真っ直ぐ飛ばすための物理設定(Inspectorの設定ミスがあっても、ここで上書きする)
        rb.useGravity = false;                 // 重力で落ちない
        rb.linearDamping = 0f;                 // 空気抵抗で減速しない(Unity 2022以前は rb.drag)
        rb.angularDamping = 0f;                // 回転の減衰なし(Unity 2022以前は rb.angularDrag)
        rb.constraints = RigidbodyConstraints.FreezeRotation;   // 当たっても回転で曲がらない

        // 高速で飛ぶので、すり抜け防止
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
    }

    public void Launch(Vector3 velocity)
    {
        launchPos = transform.position;
        rb.angularVelocity = Vector3.zero;
        rb.linearVelocity = velocity;   // Unity 2022以前は rb.velocity
        transform.rotation = Quaternion.LookRotation(velocity);
        Invoke(nameof(DestroySelf), lifetime);
    }

    void OnCollisionEnter(Collision collision)
    {
        if (hasHit) return;
        hasHit = true;

        ContactPoint contact = collision.GetContact(0);

        if (logHits)
        {
            float dist = Vector3.Distance(launchPos, contact.point);
            Debug.Log($"[Kunai] 命中: {collision.collider.name}(距離 {dist:F1}m)");
        }

        var breakable = collision.collider.GetComponentInParent<BreakableLight>();
        if (breakable != null) breakable.Break();

        // 止めて、当たった場所に、先端がめり込むように置く
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.isKinematic = true;
        transform.position = contact.point - transform.forward * (tipOffset - embedDepth);

        // 飛んでいる間の寿命タイマーを止めて、刺さった後の寿命に切り替える
        CancelInvoke(nameof(DestroySelf));
        Invoke(nameof(DestroySelf), stuckLifetime);
    }

    void DestroySelf()
    {
        Destroy(gameObject);
    }
}