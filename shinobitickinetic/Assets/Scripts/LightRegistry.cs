using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// シーン内の「ゲームに関わるライト」の一覧を共有キャッシュする。
/// 敵やプレイヤーが個別に検索しないよう、0.5秒ごとにまとめて更新する。
/// DecorativeLight が付いたライト(見た目専用)は、一覧に含めない。
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
                Light[] found = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
                var gameplayLights = new List<Light>(found.Length);
                foreach (var l in found)
                {
                    if (l.GetComponent<DecorativeLight>() == null)
                        gameplayLights.Add(l);
                }
                cache = gameplayLights.ToArray();
                nextRefresh = Time.time + RefreshInterval;
            }
            return cache;
        }
    }
}