using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

/// <summary>
/// 懐中電灯を持つ巡回敵。
/// 「視界内 + 遮蔽物なし + プレイヤーが光の中」で警戒度が上がり、
/// 影の中にいれば至近距離まで気づかれない。
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class EnemyAI : MonoBehaviour
{
    enum State { Patrol, Suspicious, Chase, Search }

    [Header("参照")]
    [SerializeField] Transform player;
    [SerializeField] Transform eyes;              // 目の位置。懐中電灯(Spot Light)もここに付ける
    [SerializeField] Transform[] waypoints;
    [SerializeField] Renderer statusRenderer;     // 状態を色で表示する(任意)

    [Header("視界")]
    [SerializeField] float viewDistance = 12f;
    [SerializeField] float viewAngle = 70f;       // 全角。Spot Lightの角度と合わせる
    [SerializeField] float shadowDetectRange = 2f; // 影の中でもこの距離以内なら気づく
    [SerializeField] LayerMask occluderMask;      // Environment を設定

    [Header("警戒度")]
    [SerializeField] float gainPerSecond = 1.0f;  // 近いほど最大2倍まで加速
    [SerializeField] float decayPerSecond = 0.4f;
    [SerializeField] float suspiciousThreshold = 0.3f;

    [Header("移動")]
    [SerializeField] float patrolSpeed = 2f;
    [SerializeField] float chaseSpeed = 5f;
    [SerializeField] float waitAtWaypoint = 1.5f;
    [SerializeField] float loseSightTime = 3f;
    [SerializeField] float searchWaitTime = 4f;
    [SerializeField] float catchDistance = 1.2f;

    NavMeshAgent agent;
    State state = State.Patrol;
    float awareness;          // 0〜1
    float timer;
    float lostTimer;
    int waypointIndex;
    Vector3 lastKnownPos;

    void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        if (eyes == null) eyes = transform;
    }

    void Start()
    {
        EnterPatrol();
    }

    void OnEnable()  { BreakableLight.Broken += OnLightBroken; }
    void OnDisable() { BreakableLight.Broken -= OnLightBroken; }

    // ライトが壊れた音を聞いたら、その場所を調べに行く
    void OnLightBroken(Vector3 pos, float radius)
    {
        if (state == State.Chase) return;                       // 追跡中は無視
        if (Vector3.Distance(transform.position, pos) > radius) return;

        // 壊れた位置がNavMesh外でも歩ける近くの点へ補正する
        if (!NavMesh.SamplePosition(pos, out NavMeshHit navHit, 4f, NavMesh.AllAreas)) return;

        lastKnownPos = navHit.position;
        EnterSearch();
    }

    void Update()
    {
        bool seen = CanSeePlayer(out float dist);
        UpdateAwareness(seen, dist);

        switch (state)
        {
            case State.Patrol:     TickPatrol();           break;
            case State.Suspicious: TickSuspicious();       break;
            case State.Chase:      TickChase(seen);        break;
            case State.Search:     TickSearch();           break;
        }

        UpdateStatusColor();
    }

    // ---------- 視界判定 ----------
    bool CanSeePlayer(out float dist)
    {
        dist = float.MaxValue;
        if (player == null) return false;

        Vector3 eyePos = eyes.position;
        Vector3 target = player.position + Vector3.up * 1.0f; // 胸の高さ
        Vector3 to = target - eyePos;
        dist = to.magnitude;

        if (dist > viewDistance) return false;
        if (Vector3.Angle(eyes.forward, to) > viewAngle * 0.5f) return false;

        // 壁などで視線が遮られていないか
        if (Physics.Raycast(eyePos, to / dist, dist, occluderMask, QueryTriggerInteraction.Ignore))
            return false;

        // 影の中なら、至近距離でない限り見えない
        bool inShadow = ShadowUtility.IsInShadow(target, LightRegistry.All, occluderMask);
        if (inShadow && dist > shadowDetectRange) return false;

        return true;
    }

    void UpdateAwareness(bool seen, float dist)
    {
        if (seen)
        {
            float closeness = 1f - Mathf.Clamp01(dist / viewDistance);
            awareness += gainPerSecond * (1f + closeness) * Time.deltaTime;
            lastKnownPos = player.position;
        }
        else if (state != State.Chase)
        {
            awareness -= decayPerSecond * Time.deltaTime;
        }
        awareness = Mathf.Clamp01(awareness);
    }

    // ---------- 各状態 ----------
    void EnterPatrol()
    {
        state = State.Patrol;
        agent.isStopped = false;
        agent.speed = patrolSpeed;
        timer = 0f;
        GoToWaypoint();
    }

    void TickPatrol()
    {
        if (awareness >= suspiciousThreshold) { EnterSuspicious(); return; }
        if (waypoints == null || waypoints.Length == 0) return;

        if (!agent.pathPending && agent.remainingDistance <= agent.stoppingDistance + 0.1f)
        {
            timer += Time.deltaTime;
            if (timer >= waitAtWaypoint)
            {
                timer = 0f;
                waypointIndex = (waypointIndex + 1) % waypoints.Length;
                GoToWaypoint();
            }
        }
    }

    void GoToWaypoint()
    {
        if (waypoints == null || waypoints.Length == 0) return;
        agent.SetDestination(waypoints[waypointIndex].position);
    }

    void EnterSuspicious()
    {
        state = State.Suspicious;
        agent.isStopped = true;   // 立ち止まって怪しい方向を見る
    }

    void TickSuspicious()
    {
        if (awareness >= 1f) { EnterChase(); return; }
        if (awareness <= 0f) { EnterPatrol(); return; }
        FaceTowards(lastKnownPos);
    }

    void EnterChase()
    {
        state = State.Chase;
        agent.isStopped = false;
        agent.speed = chaseSpeed;
        lostTimer = 0f;
    }

    void TickChase(bool seen)
    {
        awareness = 1f;
        if (seen)
        {
            lostTimer = 0f;
            agent.SetDestination(player.position);
        }
        else
        {
            lostTimer += Time.deltaTime;
            agent.SetDestination(lastKnownPos);
            if (lostTimer >= loseSightTime) { EnterSearch(); return; }
        }

        if (Vector3.Distance(transform.position, player.position) <= catchDistance)
            Caught();
    }

    void EnterSearch()
    {
        state = State.Search;
        awareness = 0.6f;
        timer = 0f;
        agent.isStopped = false;
        agent.speed = patrolSpeed * 1.5f;
        agent.SetDestination(lastKnownPos);
    }

    void TickSearch()
    {
        if (awareness >= 1f) { EnterChase(); return; }

        if (!agent.pathPending && agent.remainingDistance <= agent.stoppingDistance + 0.3f)
        {
            timer += Time.deltaTime;
            if (timer >= searchWaitTime) { awareness = 0f; EnterPatrol(); }
        }
    }

    void FaceTowards(Vector3 worldPos)
    {
        Vector3 dir = worldPos - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.01f) return;
        transform.rotation = Quaternion.Slerp(transform.rotation,
            Quaternion.LookRotation(dir), 6f * Time.deltaTime);
    }

    void Caught()
    {
        Debug.Log("見つかった!");
        // 仮のゲームオーバー処理: シーンを再読み込み(Build Settingsにシーン登録が必要)
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    void UpdateStatusColor()
    {
        if (statusRenderer == null) return;
        Color c = state switch
        {
            State.Patrol     => Color.white,
            State.Suspicious => Color.yellow,
            State.Chase      => Color.red,
            _                => new Color(1f, 0.55f, 0f), // Search: オレンジ
        };
        statusRenderer.material.color = c;
    }

    // Sceneビューで視界を確認するためのギズモ
    void OnDrawGizmosSelected()
    {
        Transform e = eyes != null ? eyes : transform;
        Gizmos.color = Color.yellow;
        Vector3 left  = Quaternion.Euler(0, -viewAngle * 0.5f, 0) * e.forward;
        Vector3 right = Quaternion.Euler(0,  viewAngle * 0.5f, 0) * e.forward;
        Gizmos.DrawRay(e.position, left * viewDistance);
        Gizmos.DrawRay(e.position, right * viewDistance);
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, shadowDetectRange);
    }
}
