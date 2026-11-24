using System.Collections.Generic;
using System.IO;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>
/// ステージ生成ツール。
/// メニューの「Tools > Shinobi > Build Stage 01」から実行すると、
/// GameScene を元に新しいシーン Stage01 を作り、夜の忍び込みステージを組み立てる。
/// - 南の路地(スタート) → 中庭 → 物陰の多い路地 → 門を抜けた先のゴール
/// - 壊せる街灯(BreakableLight)を光源&影のポイントとして配置
/// - 巡回する敵(EnemyAI)を3体、視界・懐中電灯・NavMesh付きで配置
/// - 最後に NavMesh をベイクして保存し、Build Settings に登録する
/// </summary>
public static class StageBuilder
{
    const string SourceScene = "Assets/Scenes/GameScene.unity";
    const string StageScene = "Assets/Scenes/Stage01.unity";
    const string LampPrefabPath = "Assets/Prefab/StreetLamp.prefab";

    const int EnvLayer = 6;    // Environment
    const int EnemyLayer = 8;  // Enemy

    // 元シーンにある仮のオブジェクト(この名前のルートは消してから組み直す)
    static readonly string[] PlaceholderNames =
    {
        "Ground", "Cube", "Cube (1)", "Cube (2)", "Goal", "Enemy Dummy",
    };
    static readonly string PlaceholderPrefixes = "StreetLamp";

    [MenuItem("Tools/Shinobi/Build Stage 01")]
    public static void BuildStage01()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        if (!File.Exists(SourceScene))
        {
            EditorUtility.DisplayDialog("Stage Builder", $"元になるシーンが見つかりません:\n{SourceScene}", "OK");
            return;
        }

        // --- 1. GameScene を Stage01 として複製して開く(元シーンはそのまま残す) ---
        var scene = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Single);
        if (File.Exists(StageScene)) AssetDatabase.DeleteAsset(StageScene);
        if (!EditorSceneManager.SaveScene(scene, StageScene))
        {
            Debug.LogError("[StageBuilder] Stage01 の保存に失敗しました。");
            return;
        }

        var materials = StageMaterials.Create();

        // --- 2. 仮のジオメトリを消す ---
        foreach (var root in scene.GetRootGameObjects())
        {
            bool isPlaceholder = System.Array.IndexOf(PlaceholderNames, root.name) >= 0
                                 || root.name.StartsWith(PlaceholderPrefixes);
            if (isPlaceholder) Object.DestroyImmediate(root);
        }

        // --- 3. ステージ本体 ---
        var stage = new GameObject("Stage");
        var geometry = new GameObject("Geometry").transform;
        geometry.SetParent(stage.transform, false);

        var player = Object.FindFirstObjectByType<ShinobiPlayerController>();
        if (player == null)
            Debug.LogWarning("[StageBuilder] ShinobiPlayerController が見つかりません。敵の視界ターゲットが未設定になります。");

        BuildGround(geometry, materials);
        BuildStreet(geometry, stage.transform, materials);
        BuildCourtyard(geometry, stage.transform, materials);
        BuildAlley(geometry, stage.transform, materials);
        BuildGoalCompound(geometry, stage.transform, materials);

        BuildGuards(stage.transform, player, materials);
        BuildGoal(stage.transform, materials);

        // --- 4. 夜の雰囲気 ---
        ApplyAtmosphere();

        // --- 5. NavMesh をベイク ---
        var surface = stage.AddComponent<NavMeshSurface>();
        surface.collectObjects = CollectObjects.All;
        surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
        surface.layerMask = 1 << EnvLayer;
        surface.BuildNavMesh();

        // ベイク後に敵をNavMeshへ乗せる(位置のスナップ)
        foreach (var guard in Object.FindObjectsByType<EnemyAI>(FindObjectsSortMode.None))
            SnapToNavMesh(guard.gameObject);

        // --- 6. 保存 & Build Settings へ ---
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, StageScene);
        AssetDatabase.SaveAssets();
        AddToBuildSettings(StageScene);

        Selection.activeGameObject = stage;
        SceneView.lastActiveSceneView?.FrameSelected();

        int lamps = Object.FindObjectsByType<BreakableLight>(FindObjectsSortMode.None).Length;
        int guards = Object.FindObjectsByType<EnemyAI>(FindObjectsSortMode.None).Length;
        Debug.Log($"[StageBuilder] Stage01 を生成しました (街灯 {lamps} / 敵 {guards} / NavMesh ベイク済み)。" +
                  " そのまま Play で確認できます。");
    }

    // ------------------------------------------------------------------
    // 地形
    // ------------------------------------------------------------------
    static void BuildGround(Transform parent, StageMaterials.Mats m)
    {
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.SetParent(parent, false);
        ground.transform.position = Vector3.zero;
        ground.transform.localScale = new Vector3(8f, 1f, 8f);   // 80m x 80m
        ground.layer = EnvLayer;
        ground.GetComponent<Renderer>().sharedMaterial = m.Ground;

        // 中央の街道(見た目だけ。一段だけ盛る)
        Box(parent, "Main Road", new Vector3(0f, 0.02f, -3f), new Vector3(11f, 0.04f, 58f), m.Road);
        Box(parent, "Compound Ground", new Vector3(0f, 0.025f, 25.5f), new Vector3(33f, 0.03f, 10f), m.Road);
    }

    static void BuildStreet(Transform geo, Transform stage, StageMaterials.Mats m)
    {
        // 開始地点の細い路地 (z: -32 .. -14)
        Box(geo, "Street West Wall", new Vector3(-7.5f, 2.5f, -24f), new Vector3(1f, 5f, 20f), m.Wall);
        Box(geo, "Street East Wall", new Vector3(7.5f, 2.5f, -24f), new Vector3(1f, 5f, 20f), m.Wall);
        Box(geo, "Street South Wall", new Vector3(0f, 2.5f, -34f), new Vector3(16f, 5f, 1f), m.Wall);

        // 影を作る物陰
        Crate(geo, new Vector3(-4.6f, -25f), m);
        Crate(geo, new Vector3(-4.6f, -25f), m, true);
        Crate(geo, new Vector3(4.5f, -22f), m);
        Crate(geo, new Vector3(3.6f, -28f), m);

        Lamp(stage, -5.7f, -27f);
        Lamp(stage, 5.7f, -20f);
    }

    static void BuildCourtyard(Transform geo, Transform stage, StageMaterials.Mats m)
    {
        // 中庭 (x: -12 .. 12, z: -14 .. 6) を囲う建物
        Box(geo, "West Building", new Vector3(-15f, 3f, -4f), new Vector3(6f, 6f, 20f), m.Wall);
        Box(geo, "East Building", new Vector3(15f, 3f, -4f), new Vector3(6f, 6f, 20f), m.Wall);
        Box(geo, "North West Block", new Vector3(-14f, 2.5f, 8f), new Vector3(8f, 5f, 4f), m.Wall);
        Box(geo, "North East Block", new Vector3(14f, 2.5f, 8f), new Vector3(8f, 5f, 4f), m.Wall);

        // 中庭の低い遮蔽物と木箱
        Box(geo, "Low Wall", new Vector3(0f, 0.9f, -2f), new Vector3(7f, 1.8f, 0.6f), m.Wall);
        Crate(geo, new Vector3(-6f, -6f), m);
        Crate(geo, new Vector3(-6f, -6f), m, true);
        Crate(geo, new Vector3(5.5f, 1f), m);
        Crate(geo, new Vector3(-2.5f, -10f), m);
        Crate(geo, new Vector3(9f, -11f), m);

        Lamp(stage, -10f, -4f);
        Lamp(stage, 10f, 3f);
    }

    static void BuildAlley(Transform geo, Transform stage, StageMaterials.Mats m)
    {
        // 物陰の多い路地 (x: -10 .. 10, z: 10 .. 20)
        Box(geo, "Alley West Building", new Vector3(-14f, 3f, 15f), new Vector3(8f, 6f, 10f), m.Wall);
        Box(geo, "Alley East Building", new Vector3(14f, 3f, 15f), new Vector3(8f, 6f, 10f), m.Wall);

        Crate(geo, new Vector3(-7f, 11f), m);
        Crate(geo, new Vector3(-7f, 11f), m, true);
        Crate(geo, new Vector3(6.5f, 19f), m);
        Crate(geo, new Vector3(8f, 11.5f), m);

        Lamp(stage, -7f, 12f);
        Lamp(stage, 7f, 17f);
    }

    static void BuildGoalCompound(Transform geo, Transform stage, StageMaterials.Mats m)
    {
        // 門 (z = 20 / 中央 x -5 .. 5 が通路)
        Box(geo, "Gate Wall West", new Vector3(-11.5f, 2.5f, 20f), new Vector3(13f, 5f, 1f), m.Wall);
        Box(geo, "Gate Wall East", new Vector3(11.5f, 2.5f, 20f), new Vector3(13f, 5f, 1f), m.Wall);

        // ゴールの境内
        Box(geo, "Compound West Wall", new Vector3(-17f, 2.5f, 25.5f), new Vector3(2f, 5f, 11f), m.Wall);
        Box(geo, "Compound East Wall", new Vector3(17f, 2.5f, 25.5f), new Vector3(2f, 5f, 11f), m.Wall);
        Box(geo, "Compound Back Wall", new Vector3(0f, 2.5f, 31f), new Vector3(35f, 5f, 1f), m.Wall);

        // 鳥居と石段
        Box(geo, "Torii Pillar L", new Vector3(-2.2f, 2.2f, 28.5f), new Vector3(0.45f, 4.4f, 0.45f), m.Wall);
        Box(geo, "Torii Pillar R", new Vector3(2.2f, 2.2f, 28.5f), new Vector3(0.45f, 4.4f, 0.45f), m.Wall);
        Box(geo, "Torii Lintel", new Vector3(0f, 4.5f, 28.5f), new Vector3(5.6f, 0.5f, 0.5f), m.Wall);
        Box(geo, "Shrine Step", new Vector3(0f, 0.15f, 27f), new Vector3(6f, 0.3f, 3f), m.Wall);

        Crate(geo, new Vector3(-6f, 25f), m);
        Crate(geo, new Vector3(6f, 29f), m);
        Crate(geo, new Vector3(-12f, 28f), m);

        Lamp(stage, 0f, 23f);
        Lamp(stage, -12f, 27f);
        Lamp(stage, 12f, 27f);
    }

    // ------------------------------------------------------------------
    // 敵
    // ------------------------------------------------------------------
    static void BuildGuards(Transform stage, ShinobiPlayerController player, StageMaterials.Mats m)
    {
        var guards = new GameObject("Guards").transform;
        guards.SetParent(stage, false);
        var waypointRoot = new GameObject("Waypoints").transform;
        waypointRoot.SetParent(stage, false);

        BuildGuard(guards, waypointRoot, player, m, "Guard A (Courtyard)", new Vector3(0f, 1f, -6f), new Vector2[]
        {
            new Vector2(0f, -11f), new Vector2(8f, -4f), new Vector2(0f, 3f), new Vector2(-8f, -4f),
        });

        BuildGuard(guards, waypointRoot, player, m, "Guard B (Alley)", new Vector3(0f, 1f, 12f), new Vector2[]
        {
            new Vector2(-6f, 12f), new Vector2(6f, 13f), new Vector2(6f, 18f), new Vector2(-6f, 17f),
        });

        BuildGuard(guards, waypointRoot, player, m, "Guard C (Compound)", new Vector3(0f, 1f, 23f), new Vector2[]
        {
            new Vector2(0f, 23f), new Vector2(10f, 27f), new Vector2(-10f, 27f),
        });
    }

    static void BuildGuard(Transform parent, Transform waypointRoot, ShinobiPlayerController player,
                           StageMaterials.Mats m, string name, Vector3 spawn, Vector2[] patrol)
    {
        var guard = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        guard.name = name;
        guard.transform.SetParent(parent, false);
        guard.transform.position = spawn;
        guard.layer = EnemyLayer;
        guard.GetComponent<Renderer>().sharedMaterial = m.Guard;
        Object.DestroyImmediate(guard.GetComponent<CapsuleCollider>());
        var body = guard.AddComponent<CapsuleCollider>();
        body.height = 2f;
        body.radius = 0.5f;
        body.center = Vector3.zero;

        // 目 & 懐中電灯
        var eyes = new GameObject("Eyes").transform;
        eyes.SetParent(guard.transform, false);
        eyes.localPosition = new Vector3(0f, 0.55f, 0.15f);

        var flashlight = new GameObject("Flashlight");
        flashlight.transform.SetParent(eyes, false);
        var light = flashlight.AddComponent<Light>();
        light.type = LightType.Spot;
        light.color = new Color(1f, 0.93f, 0.78f);
        light.intensity = 5f;
        light.range = 14f;
        light.spotAngle = 70f;
        light.innerSpotAngle = 42f;
        light.shadows = LightShadows.Soft;
        flashlight.AddComponent<UniversalAdditionalLightData>();

        // EnemyAI は RequireComponent(NavMeshAgent) なので、追加すると NavMeshAgent も付く
        var ai = guard.AddComponent<EnemyAI>();

        var agent = guard.GetComponent<UnityEngine.AI.NavMeshAgent>();
        agent.speed = 2f;
        agent.acceleration = 8f;
        agent.angularSpeed = 240f;
        agent.radius = 0.5f;
        agent.height = 2f;
        agent.baseOffset = 1f;
        agent.stoppingDistance = 0.4f;

        var wpRoot = new GameObject(name + " Waypoints").transform;
        wpRoot.SetParent(waypointRoot, false);
        var points = new List<Transform>(patrol.Length);
        for (int i = 0; i < patrol.Length; i++)
        {
            var wp = new GameObject($"WP{i}").transform;
            wp.SetParent(wpRoot, false);
            wp.position = new Vector3(patrol[i].x, 0f, patrol[i].y);
            points.Add(wp);
        }

        var so = new SerializedObject(ai);
        var playerProp = so.FindProperty("player");
        if (player != null) playerProp.objectReferenceValue = player.transform;
        so.FindProperty("eyes").objectReferenceValue = eyes;
        var wps = so.FindProperty("waypoints");
        wps.arraySize = points.Count;
        for (int i = 0; i < points.Count; i++)
            wps.GetArrayElementAtIndex(i).objectReferenceValue = points[i];
        so.FindProperty("occluderMask").intValue = 1 << EnvLayer;
        so.FindProperty("statusRenderer").objectReferenceValue = guard.GetComponent<Renderer>();
        so.FindProperty("viewDistance").floatValue = 12f;
        so.FindProperty("viewAngle").floatValue = 70f;
        so.FindProperty("shadowDetectRange").floatValue = 2f;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static void SnapToNavMesh(GameObject agentObject)
    {
        var agent = agentObject.GetComponent<UnityEngine.AI.NavMeshAgent>();
        if (agent == null) return;
        if (UnityEngine.AI.NavMesh.SamplePosition(agentObject.transform.position, out UnityEngine.AI.NavMeshHit hit, 3f, UnityEngine.AI.NavMesh.AllAreas))
            agentObject.transform.position = hit.position + Vector3.up * agent.baseOffset;
    }

    // ------------------------------------------------------------------
    // ゴール
    // ------------------------------------------------------------------
    static void BuildGoal(Transform stage, StageMaterials.Mats m)
    {
        var goal = Box(stage, "Goal", new Vector3(0f, 1.3f, 28.5f), new Vector3(3.2f, 2.4f, 0.6f), m.Goal);
        var collider = goal.GetComponent<BoxCollider>();
        collider.isTrigger = true;
        goal.AddComponent<GoalZone>();

        // ゴール地点が分かるよう、ほのかに光らせる
        var glow = new GameObject("Goal Glow");
        glow.transform.SetParent(goal.transform, false);
        var light = glow.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = new Color(0.5f, 1f, 0.65f);
        light.intensity = 2.5f;
        light.range = 7f;
        glow.AddComponent<UniversalAdditionalLightData>();
        glow.AddComponent<DecorativeLight>();
    }

    // ------------------------------------------------------------------
    // 環境
    // ------------------------------------------------------------------
    static void ApplyAtmosphere()
    {
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = new Color(0.03f, 0.04f, 0.07f);
        RenderSettings.fogStartDistance = 18f;
        RenderSettings.fogEndDistance = 78f;

        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.08f, 0.09f, 0.14f);
        RenderSettings.skybox = null;
    }

    static void AddToBuildSettings(string scenePath)
    {
        var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        if (scenes.Exists(s => s.path == scenePath)) return;
        scenes.Add(new EditorBuildSettingsScene(scenePath, true));
        EditorBuildSettings.scenes = scenes.ToArray();
    }

    // ------------------------------------------------------------------
    // ヘルパー
    // ------------------------------------------------------------------
    static GameObject Box(Transform parent, string name, Vector3 center, Vector3 size, Material material)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.localScale = size;
        go.layer = EnvLayer;
        go.GetComponent<Renderer>().sharedMaterial = material;
        return go;
    }

    static void Crate(Transform parent, Vector3 xz, StageMaterials.Mats m, bool stacked = false)
    {
        const float size = 1.2f;
        float y = stacked ? size * 0.5f + size : size * 0.5f;
        Box(parent, stacked ? "Crate (stacked)" : "Crate",
            new Vector3(xz.x, y, xz.y), Vector3.one * size, m.Crate);
    }

    static void Lamp(Transform parent, float x, float z)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(LampPrefabPath);
        if (prefab == null)
        {
            Debug.LogWarning($"[StageBuilder] 街灯プレハブが見つかりません: {LampPrefabPath}");
            return;
        }
        var lamp = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
        lamp.transform.position = new Vector3(x, 0f, z);
        lamp.transform.rotation = Quaternion.Euler(0f, Random.Range(0, 4) * 90f, 0f);
    }
}

/// <summary>ステージ用のマテリアルを Assets/Materials/Stage に用意する。</summary>
public static class StageMaterials
{
    public struct Mats
    {
        public Material Ground, Road, Wall, Crate, Guard, Goal;
    }

    const string Folder = "Assets/Materials/Stage";

    public static Mats Create()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Materials"))
            AssetDatabase.CreateFolder("Assets", "Materials");
        if (!AssetDatabase.IsValidFolder(Folder))
            AssetDatabase.CreateFolder("Assets/Materials", "Stage");

        return new Mats
        {
            Ground = Get("StageGround", new Color(0.13f, 0.14f, 0.16f), 0.12f),
            Road = Get("StageRoad", new Color(0.09f, 0.10f, 0.12f), 0.05f),
            Wall = Get("StageWall", new Color(0.26f, 0.25f, 0.24f), 0.08f),
            Crate = Get("StageCrate", new Color(0.38f, 0.27f, 0.16f), 0.1f),
            Guard = Get("StageGuard", new Color(0.42f, 0.14f, 0.16f), 0.2f),
            Goal = Get("StageGoal", new Color(0.35f, 0.95f, 0.55f), 0.4f, true),
        };
    }

    static Material Get(string name, Color color, float smoothness, bool emissive = false)
    {
        string path = $"{Folder}/{name}.mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) return existing;

        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var mat = new Material(shader) { name = name };
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
        if (emissive)
        {
            mat.EnableKeyword("_EMISSION");
            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", color * 1.4f);
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }
}
