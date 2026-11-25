using UnityEngine;

/// <summary>
/// Mixamo などのヒューマノイドモデルを動かすための小さなドライバー。
/// StageBuilder が生成する PlayerAnimator.controller のパラメータを書く:
///   Speed(float)         0 = 待機 / 1 = 走り (移動速度 / speedForFullBlend)
///   Grounded(bool)       接地しているか
///   Dashing(bool)        瞬身ダッシュ中か
///   WalkPlayback(float)  歩きクリップの再生倍率
///   RunPlayback(float)   走りクリップの再生倍率
///
/// 再生倍率は推測値ではなく実行中に合わせ込む:
/// 「接地している足がキャラクターの後ろへ流れる速さ」を毎フレーム測り、
/// 実際の移動速度と一致するまで各クリップの等倍速度を学習する。
/// これで歩きも走りも足が地面を蹴る速さになり、滑って見えない。
/// パラメータが無いコントローラでも警告を出さずに動く。
/// </summary>
[RequireComponent(typeof(Animator))]
public class ShinobiLocomotion : MonoBehaviour
{
    [Header("ブレンド設定")]
    [SerializeField] private float speedForFullBlend = 8.0f;   // moveSpeed と揃える
    [SerializeField] private float dashSpeedThreshold = 14.0f; // 瞬身ダッシュ(40)を検出
    [SerializeField] private float speedDamping = 0.1f;

    [Header("足の滑り対策(初期値。実行中に自動で学習する)")]
    [SerializeField] private float walkGroundSpeed = 1.6f;   // 歩きクリップが等倍で進む速度
    [SerializeField] private float runGroundSpeed = 5.0f;    // 走りクリップが等倍で進む速度
    [SerializeField] private float minPlaybackSpeed = 0.2f;
    [SerializeField] private float maxPlaybackSpeed = 3.5f;

    [Header("接地合わせ")]
    [SerializeField] private bool matchFeetToGround = true;
    [SerializeField] private float clipSpeedAdaptRate = 3.0f;   // 1秒あたりの合わせ込みの強さ

    [Header("Animator パラメータ名・ステート名")]
    [SerializeField] private string speedParameter = "Speed";
    [SerializeField] private string groundedParameter = "Grounded";
    [SerializeField] private string dashParameter = "Dashing";
    [SerializeField] private string walkPlaybackParameter = "WalkPlayback";
    [SerializeField] private string runPlaybackParameter = "RunPlayback";
    [SerializeField] private string walkStateName = "Walk";
    [SerializeField] private string runStateName = "Run";

    [Header("デバッグ")]
    [SerializeField] private bool logFootSync = true;

    private Animator animator;
    private CharacterController controller;
    private Transform leftFoot;
    private Transform rightFoot;

    private int speedHash;
    private int groundedHash;
    private int dashHash;
    private int walkPlaybackHash;
    private int runPlaybackHash;
    private int walkStateHash;
    private int runStateHash;
    private bool hasSpeed;
    private bool hasGrounded;
    private bool hasDash;
    private bool hasWalkPlayback;
    private bool hasRunPlayback;

    private float walkClipSpeed;   // 学習中の「等倍で進む速度」
    private float runClipSpeed;
    private float lastWalkPlayback = 1f;
    private float lastRunPlayback = 1f;

    private bool hasPrevFeet;
    private Vector3 prevLeftFoot;
    private Vector3 prevRightFoot;
    private float nextLogTime;

    /// <summary>
    /// StageBuilder がクリップから実測した「等倍で進む速度」を初期値として受け取る。
    /// 0 以下(計測できなかった)ときは既定値のまま。
    /// </summary>
    public void SetGroundSpeeds(float walkMetersPerSecond, float runMetersPerSecond)
    {
        if (walkMetersPerSecond > 0.1f)
        {
            walkGroundSpeed = walkMetersPerSecond;
            walkClipSpeed = walkMetersPerSecond;
        }

        if (runMetersPerSecond > 0.1f)
        {
            runGroundSpeed = runMetersPerSecond;
            runClipSpeed = runMetersPerSecond;
        }
    }

    private void Awake()
    {
        animator = GetComponent<Animator>();
        controller = GetComponentInParent<CharacterController>();

        if (walkClipSpeed <= 0f) walkClipSpeed = walkGroundSpeed;
        if (runClipSpeed <= 0f) runClipSpeed = runGroundSpeed;

        speedHash = Animator.StringToHash(speedParameter);
        groundedHash = Animator.StringToHash(groundedParameter);
        dashHash = Animator.StringToHash(dashParameter);
        walkPlaybackHash = Animator.StringToHash(walkPlaybackParameter);
        runPlaybackHash = Animator.StringToHash(runPlaybackParameter);
        walkStateHash = Animator.StringToHash(walkStateName);
        runStateHash = Animator.StringToHash(runStateName);

        if (animator == null) return;

        foreach (var parameter in animator.parameters)
        {
            if (parameter.nameHash == speedHash) hasSpeed = true;
            else if (parameter.nameHash == groundedHash) hasGrounded = true;
            else if (parameter.nameHash == dashHash) hasDash = true;
            else if (parameter.nameHash == walkPlaybackHash) hasWalkPlayback = true;
            else if (parameter.nameHash == runPlaybackHash) hasRunPlayback = true;
        }

        leftFoot = FindFoot("left");
        rightFoot = FindFoot("right");
    }

    private void Update()
    {
        if (animator == null || controller == null) return;

        Vector3 velocity = controller.velocity;
        float speed = new Vector3(velocity.x, 0f, velocity.z).magnitude;

        AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
        bool inWalk = state.shortNameHash == walkStateHash;
        bool inRun = state.shortNameHash == runStateHash;

        float footBack = StanceFootBackwardSpeed();

        // いまの再生倍率で「等倍なら何m/s か」を逆算して学習する。
        // 接地足が後ろへ流れる速さ = 実際に進んでいる速さ になった時点で滑りはゼロ。
        if (matchFeetToGround && speed > 0.5f && footBack > 0.05f)
        {
            float playback = inWalk ? lastWalkPlayback : inRun ? lastRunPlayback : 0f;
            if (playback > 0.01f)
            {
                float observedClipSpeed = footBack / playback;
                float k = 1f - Mathf.Exp(-clipSpeedAdaptRate * Time.deltaTime);
                if (inWalk) walkClipSpeed = Mathf.Lerp(walkClipSpeed, observedClipSpeed, k);
                else if (inRun) runClipSpeed = Mathf.Lerp(runClipSpeed, observedClipSpeed, k);
            }
        }

        if (hasSpeed)
        {
            float blend = speedForFullBlend > 0.01f ? Mathf.Clamp01(speed / speedForFullBlend) : 0f;
            animator.SetFloat(speedHash, blend, speedDamping, Time.deltaTime);
        }

        lastWalkPlayback = PlaybackFor(speed, walkClipSpeed);
        lastRunPlayback = PlaybackFor(speed, runClipSpeed);
        if (hasWalkPlayback) animator.SetFloat(walkPlaybackHash, lastWalkPlayback);
        if (hasRunPlayback) animator.SetFloat(runPlaybackHash, lastRunPlayback);

        // CharacterController の isGrounded は走行中に一瞬 false になりやすいので、
        // 下向きに小さく落ちている間は接地とみなして空中ステートへの誤切り替えを防ぐ
        bool grounded = controller.isGrounded || (velocity.y < 0f && velocity.y > -6f);
        if (hasGrounded) animator.SetBool(groundedHash, grounded);
        if (hasDash) animator.SetBool(dashHash, speed >= dashSpeedThreshold);

        if (logFootSync) LogFootSync(speed, footBack, grounded, inWalk, inRun);
    }

    // 低い方(接地している側)の足が、キャラクターの後ろ向きに動く速さ [m/s]
    private float StanceFootBackwardSpeed()
    {
        if (leftFoot == null || rightFoot == null) return 0f;

        Vector3 left = transform.InverseTransformPoint(leftFoot.position);
        Vector3 right = transform.InverseTransformPoint(rightFoot.position);

        if (!hasPrevFeet)
        {
            prevLeftFoot = left;
            prevRightFoot = right;
            hasPrevFeet = true;
            return 0f;
        }

        bool leftIsStance = left.y < right.y;
        float deltaZ = leftIsStance ? left.z - prevLeftFoot.z : right.z - prevRightFoot.z;

        prevLeftFoot = left;
        prevRightFoot = right;

        float dt = Mathf.Max(Time.deltaTime, 0.0001f);
        return -deltaZ / dt;   // 後ろ向き(負のZ)ほど正の値
    }

    private float PlaybackFor(float speed, float clipGroundSpeed)
    {
        if (clipGroundSpeed < 0.01f) return 1f;
        return Mathf.Clamp(speed / clipGroundSpeed, minPlaybackSpeed, maxPlaybackSpeed);
    }

    private Transform FindFoot(string side)
    {
        string wanted = side + "foot";
        foreach (var child in GetComponentsInChildren<Transform>())
        {
            string name = child.name.ToLowerInvariant()
                .Replace(":", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty);
            if (name.EndsWith(wanted)) return child;   // mixamorig:LeftFoot など
        }

        if (animator == null) return null;
        return side == "left"
            ? animator.GetBoneTransform(HumanBodyBones.LeftFoot)
            : animator.GetBoneTransform(HumanBodyBones.RightFoot);
    }

    // 滑りが直らないときの手掛かり: 足が動いていないのか、倍率が合っていないのかが分かる
    private void LogFootSync(float speed, float footBack, bool grounded, bool inWalk, bool inRun)
    {
        if (Time.unscaledTime < nextLogTime) return;
        nextLogTime = Time.unscaledTime + 2f;
        if (speed < 0.3f) return;

        Debug.Log($"[ShinobiLocomotion] speed={speed:F2}m/s state={(inWalk ? "Walk" : inRun ? "Run" : "Idle/Air")} " +
                  $"footBack={footBack:F2}m/s walkClip={walkClipSpeed:F2} runClip={runClipSpeed:F2} " +
                  $"playback={(inWalk ? lastWalkPlayback : lastRunPlayback):F2} grounded={grounded}");
    }
}
