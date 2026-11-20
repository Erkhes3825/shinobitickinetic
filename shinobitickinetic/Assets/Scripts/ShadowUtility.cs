using UnityEngine;

/// <summary>
/// 指定した地点が「影の中」かどうかを判定する。
/// 全ての有効なライトについて、地点→ライトへRaycastし、
/// 1つでも遮られずに届くライトがあれば「明るい」と判断する。
/// </summary>
public static class ShadowUtility
{
    public static bool IsInShadow(Vector3 point, Light[] lights, LayerMask occluders)
    {
        foreach (var light in lights)
        {
            if (light == null || !light.enabled || !light.gameObject.activeInHierarchy) continue;
            if (light.intensity <= 0.01f) continue;

            if (IsLitBy(point, light, occluders)) return false;
        }
        return true;
    }

    static bool IsLitBy(Vector3 point, Light light, LayerMask occluders)
    {
        Vector3 dirToLight;
        float distance;

        switch (light.type)
        {
            case LightType.Directional:
                dirToLight = -light.transform.forward;
                distance = 200f;
                break;

            case LightType.Point:
            case LightType.Spot:
                Vector3 toLight = light.transform.position - point;
                distance = toLight.magnitude;
                if (distance > light.range) return false;   // 光が届かない
                dirToLight = toLight / distance;

                if (light.type == LightType.Spot)
                {
                    float angle = Vector3.Angle(light.transform.forward, -dirToLight);
                    if (angle > light.spotAngle * 0.5f) return false; // 円錐の外
                }
                break;

            default:
                return false; // Area等は今回は無視
        }

        // 遮蔽物に当たれば影、当たらなければ光が届いている
        bool blocked = Physics.Raycast(point, dirToLight, distance, occluders,
                                       QueryTriggerInteraction.Ignore);
        return !blocked;
    }
}
