using UnityEngine;

/// <summary>
/// ゴール地点。プレイヤーが触れたら、クリア。
/// Cube などに付けて使う(Collider は自動で Is Trigger になる)。
/// </summary>
[RequireComponent(typeof(Collider))]
public class GoalZone : MonoBehaviour
{
    // コンポーネントを付けた時に、自動で Is Trigger にする
    void Reset()
    {
        GetComponent<Collider>().isTrigger = true;
    }

    void OnTriggerEnter(Collider other)
    {
        if (other.GetComponentInParent<ShinobiPlayerController>() == null) return;

        if (GameManager.Instance != null)
            GameManager.Instance.Win();
        else
            Debug.LogWarning("[GoalZone] GameManager が見つかりません。");
    }

    // Sceneビューで、ゴールの範囲が分かるように、緑の箱を表示する
    void OnDrawGizmos()
    {
        Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.35f);
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawCube(Vector3.zero, Vector3.one);
    }
}
