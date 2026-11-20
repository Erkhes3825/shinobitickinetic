using UnityEngine;

/// <summary>
/// シーン内のライト一覧を共有キャッシュする。
/// 敵やプレイヤーが個別に検索しないよう、0.5秒ごとにまとめて更新する。
/// </summary>
public static class LightRegistry
{
    const float RefreshInterval = 0.5f;

    static Light[] cache = new Light[0];
    static float nextRefresh;

    // Domain Reload無効時でも、再生ごとに状態をリセットする
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetState()
    {
        cache = new Light[0];
        nextRefresh = 0f;
    }

    public static Light[] All
    {
        get
        {
            if (Time.time >= nextRefresh)
            {
                cache = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
                nextRefresh = Time.time + RefreshInterval;
            }
            return cache;
        }
    }
}
