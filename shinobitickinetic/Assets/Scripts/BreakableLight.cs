using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// 壊せる照明(街灯・ランタンなど)。ルートオブジェクトに付ける。
/// Break()が呼ばれると明滅して消灯し、周囲の敵に「音」を通知する。
/// </summary>
public class BreakableLight : MonoBehaviour
{
    /// <summary>ライトが壊れたときの通知(位置, 音の届く半径)。敵が購読する。</summary>
    public static event Action<Vector3, float> Broken;

    [Header("対象(空なら子から自動取得)")]
    [SerializeField] Light[] lights;
    [SerializeField] Renderer[] glowRenderers;   // 電球の見た目(発光を消す)
    [SerializeField] ParticleSystem sparks;      // 火花(任意)

    [Header("設定")]
    [SerializeField] float noiseRadius = 15f;    // 敵に聞こえる距離
    [SerializeField] float flickerTime = 0.25f;

    public bool IsBroken { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Broken = null;

    void Awake()
    {
        if (lights == null || lights.Length == 0)
            lights = GetComponentsInChildren<Light>();
    }

    [ContextMenu("テスト: 壊す")]   // 再生中、Inspectorのコンポーネント右クリックから実行できる
    public void Break()
    {
        if (IsBroken) return;
        IsBroken = true;

        if (sparks != null) sparks.Play();
        Broken?.Invoke(transform.position, noiseRadius);
        StartCoroutine(FlickerOff());
    }

    IEnumerator FlickerOff()
    {
        float t = 0f;
        while (t < flickerTime)
        {
            bool on = Mathf.FloorToInt(t / 0.05f) % 2 == 0; // 0.05秒ごとにON/OFF
            foreach (var l in lights) if (l != null) l.enabled = on;
            t += Time.deltaTime;
            yield return null;
        }

        foreach (var l in lights) if (l != null) l.enabled = false;

        foreach (var r in glowRenderers)
        {
            if (r == null) continue;
            r.material.DisableKeyword("_EMISSION");
            r.material.SetColor("_EmissionColor", Color.black);
        }
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.6f);
        Gizmos.DrawWireSphere(transform.position, noiseRadius);
    }
}
