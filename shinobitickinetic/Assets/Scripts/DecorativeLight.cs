using UnityEngine;

/// <summary>
/// これを付けたライトは、「影の判定」の対象から外れる。
/// プレイヤーの影渡り・敵の発見に、一切影響しない、見た目専用の光(月明かり、環境光など)に付ける。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Light))]
public class DecorativeLight : MonoBehaviour
{
}
