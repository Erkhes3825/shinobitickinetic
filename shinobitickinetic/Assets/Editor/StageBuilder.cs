using System.Collections.Generic;
using System.IO;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.Animations;
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

    const int EnvLayer = 6;      // Environment
    const int EnemyLayer = 8;    // Enemy
    const int BreakableLayer = 9; // Breakable(壊せるライトなど)

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
        else
        {
            ConfigureShadowStep(player);
            ConfigureKunaiThrower(player);
            BuildPlayerVisual(player, materials);
        }

        BuildGround(geometry, materials);
        BuildStreet(geometry, stage.transform, materials);
        BuildCourtyard(geometry, stage.transform, materials);
        BuildAlley(geometry, stage.transform, materials);
        BuildGoalCompound(geometry, stage.transform, materials);

        BuildGuards(stage.transform, player, materials);
        BuildGoal(stage.transform, materials);

        // --- 4. 夜の雰囲気とカメラ構図 ---
        ApplyAtmosphere();
        ConfigureAimCamera();

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

        Lamp(stage, m, -5.7f, -27f, 6.5f, 9f);
        Lamp(stage, m, 5.7f, -20f, 6.5f, 9f);
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

        Lamp(stage, m, -10f, -4f, 8f, 12f);
        Lamp(stage, m, 10f, 3f, 8f, 12f);
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

        Lamp(stage, m, -7f, 12f, 6.5f, 10f);
        Lamp(stage, m, 7f, 17f, 6.5f, 10f);
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

        Lamp(stage, m, 0f, 23f, 7f, 10f);
        Lamp(stage, m, -12f, 27f, 7f, 11f);
        Lamp(stage, m, 12f, 27f, 7f, 11f);
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
        }, 9f);   // 最初の敵: 視界を短くして、屋外でも避けやすい

        BuildGuard(guards, waypointRoot, player, m, "Guard B (Alley)", new Vector3(0f, 1f, 12f), new Vector2[]
        {
            new Vector2(-6f, 12f), new Vector2(6f, 13f), new Vector2(6f, 18f), new Vector2(-6f, 17f),
        }, 11f);  // 中盤: 標準

        BuildGuard(guards, waypointRoot, player, m, "Guard C (Compound)", new Vector3(0f, 1f, 23f), new Vector2[]
        {
            new Vector2(0f, 23f), new Vector2(10f, 27f), new Vector2(-10f, 27f),
        }, 13f);  // 最後の敵: 一番長い視界
    }

    static void BuildGuard(Transform parent, Transform waypointRoot, ShinobiPlayerController player,
                           StageMaterials.Mats m, string name, Vector3 spawn, Vector2[] patrol,
                           float viewDistance)
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
        light.range = viewDistance + 1f;   // 見える距離より少し先まで照らす
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
        so.FindProperty("viewDistance").floatValue = viewDistance;
        so.FindProperty("viewAngle").floatValue = 70f;
        so.FindProperty("shadowDetectRange").floatValue = 2f;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // Stage01 の地形はすべて Environment レイヤーなので、影渡りのマスクを生成時に明示しておく。
    // (GameScene に保存されているマスク値を Stage01 がそのまま引き継がないようにするため)
    static void ConfigureShadowStep(ShinobiPlayerController player)
    {
        var shadowStep = player.GetComponent<ShadowStepAbility>();
        if (shadowStep == null) return;

        var so = new SerializedObject(shadowStep);
        var ground = so.FindProperty("groundMask");
        var occluder = so.FindProperty("occluderMask");
        if (ground != null) ground.intValue = 1 << EnvLayer;
        if (occluder != null) occluder.intValue = 1 << EnvLayer;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // クナイの狙い/当たり判定は Environment + Breakable。
    // (GameScene 側の値に依存せず、街灯の電球に当たるようにする)
    static void ConfigureKunaiThrower(ShinobiPlayerController player)
    {
        var thrower = player.GetComponent<KunaiThrower>();
        if (thrower == null) return;

        var so = new SerializedObject(thrower);
        var aim = so.FindProperty("aimMask");
        if (aim != null) aim.intValue = (1 << EnvLayer) | (1 << BreakableLayer);
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // ------------------------------------------------------------------
    // プレイヤーの見た目 (Mixamo 等のヒューマノイド / 無ければプリミティブ)
    // ------------------------------------------------------------------

    // Mixamo から落とした FBX はここへ入れる(サブフォルダ可)。
    //   キャラクター : With Skin でダウンロードしたスキン付きの FBX
    //   モーション   : Without Skin でダウンロードした FBX。ファイル名に
    //                  Idle / Walk / Run / Jump を含めると自動で割り当てる
    // PlayerVisual.prefab を置いた場合はそちらを優先する。
    const string PlayerModelFolder = "Assets/Character/Player";
    const string PlayerVisualPrefab = PlayerModelFolder + "/PlayerVisual.prefab";
    const float PlayerHeight = 1.78f;   // プリミティブの忍者と同じ身長

    // ShinobiLocomotion と共有するパラメータ名(クリップごとの再生倍率)
    const string WalkPlaybackParameter = "WalkPlayback";
    const string RunPlaybackParameter = "RunPlayback";

    struct LocomotionClips
    {
        public AnimationClip Idle, Walk, Run, Air;
    }

    static void BuildPlayerVisual(ShinobiPlayerController player, StageMaterials.Mats m)
    {
        ConfigureMixamoImports();   // Humanoid・ループ設定を揃える(既に正しければ何もしない)

        var model = FindPlayerModel();
        if (model == null)
        {
            BuildShinobiBody(player, m);
            Debug.Log($"[StageBuilder] プレイヤーのモデルが無いのでプリミティブの忍者で組みました。" +
                      $" Mixamo の FBX を {PlayerModelFolder} に入れて再生成すると差し替わります。");
            return;
        }

        BuildMixamoBody(player, model);
        Debug.Log($"[StageBuilder] プレイヤーのモデルを使用: {AssetDatabase.GetAssetPath(model)}");
    }

    // PlayerVisual.prefab があればそれ。無ければフォルダ内で「メッシュを持つ FBX」を
    // キャラクターとみなす(スキン無しのモーション FBX にはメッシュが入らない)。
    static GameObject FindPlayerModel()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerVisualPrefab);
        if (prefab != null) return prefab;
        if (!AssetDatabase.IsValidFolder(PlayerModelFolder)) return null;

        GameObject best = null;
        long bestSize = -1;
        foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { PlayerModelFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase)) continue;
            if (!HasMesh(path)) continue;

            long size = new FileInfo(path).Length;
            if (size <= bestSize) continue;
            best = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            bestSize = size;
        }
        return best;
    }

    static bool HasMesh(string modelPath)
    {
        foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(modelPath))
            if (asset is Mesh || asset is SkinnedMeshRenderer) return true;
        return false;
    }

    static void BuildMixamoBody(ShinobiPlayerController player, GameObject modelAsset)
    {
        var root = player.transform;

        // 元のカプセルの見た目だけ消す(CharacterController / CapsuleCollider は残す)
        foreach (var r in root.GetComponentsInChildren<MeshRenderer>())
            r.enabled = false;

        var model = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset, root);
        model.name = "Shinobi Body";
        model.transform.localPosition = new Vector3(0f, FeetOffset(root), 0f);   // Mixamo は原点=足元
        model.transform.localRotation = Quaternion.identity;                    // Mixamo は +Z 向き
        model.transform.localScale = Vector3.one;
        SetLayerRecursively(model, player.gameObject.layer);

        // 体のコライダーは外す(当たり判定は元のカプセルのまま)
        foreach (var collider in model.GetComponentsInChildren<Collider>(true))
            Object.DestroyImmediate(collider);

        WarnIfUntextured(model);
        FitModel(model.transform, root.position.y + FeetOffset(root), PlayerHeight);

        var clips = CollectLocomotionClips();
        var animator = model.GetComponent<Animator>();
        if (animator == null) animator = model.GetComponentInChildren<Animator>();

        if (animator == null && (clips.Idle != null || clips.Walk != null || clips.Run != null))
            animator = model.AddComponent<Animator>();

        if (animator == null)
        {
            Debug.LogWarning("[StageBuilder] プレイヤーモデルに Animator が無いので T ポーズのまま表示されます。");
            return;
        }

        if (animator.runtimeAnimatorController == null &&
            (clips.Idle != null || clips.Walk != null || clips.Run != null))
        {
            animator.runtimeAnimatorController = BuildLocomotionController(clips);
        }

        animator.applyRootMotion = false;   // 移動は ShinobiPlayerController が行う

        // Animator が子オブジェクトに付いているモデルでも、その Animator と同じ場所に
        // ドライバーを置く(別オブジェクトに付けると空の Animator が増えて動かなくなる)
        var driver = animator.GetComponent<ShinobiLocomotion>();
        if (driver == null) driver = animator.gameObject.AddComponent<ShinobiLocomotion>();

        // クリップが等倍で進む速度は推測せずにアニメーションから実測する
        float walkSpeed = MeasureClipGroundSpeed(animator, clips.Walk);
        float runSpeed = MeasureClipGroundSpeed(animator, clips.Run);
        driver.SetGroundSpeeds(walkSpeed, runSpeed);

        Debug.Log($"[StageBuilder] ロコモーション  idle={Describe(clips.Idle)} " +
                  $"walk={Describe(clips.Walk, walkSpeed)} run={Describe(clips.Run, runSpeed)} " +
                  $"air={Describe(clips.Air)}");
    }

    static string Describe(AnimationClip clip, float speed = -1f)
    {
        if (clip == null) return "なし";
        if (speed < 0f) return $"{clip.name}(humanoid={clip.humanMotion})";
        return $"{clip.name}(humanoid={clip.humanMotion}, 等倍 {speed:F2}m/s)";
    }

    // クリップが等倍で地面を進む速度を実測する。
    // ルートモーションが焼かれていればそれを、無ければ「接地中の足が後ろへ流れる速さ」を使う。
    static float MeasureClipGroundSpeed(Animator animator, AnimationClip clip)
    {
        if (animator == null || clip == null || clip.length < 0.05f) return 0f;
        if (clip.humanMotion == false) return 0f;   // Humanoid として解釈できないクリップ

        // ルートモーション付きのクリップは、その移動速度がそのまま地面の速度
        float rootSpeed = new Vector2(clip.averageSpeed.x, clip.averageSpeed.z).magnitude;
        if (rootSpeed > 0.1f) return rootSpeed;

        var root = animator.transform;
        var left = FindFoot(root, "left");
        var right = FindFoot(root, "right");
        if (left == null || right == null) return 0f;
        if (AnimationMode.InAnimationMode()) return 0f;

        const int samples = 30;
        float dt = clip.length / samples;
        var speeds = new List<float>();

        AnimationMode.StartAnimationMode();
        try
        {
            AnimationMode.SampleAnimationClip(animator.gameObject, clip, 0f);
            Vector3 prevLeft = ToRoot(root, left.position);
            Vector3 prevRight = ToRoot(root, right.position);

            for (int i = 1; i <= samples; i++)
            {
                AnimationMode.SampleAnimationClip(animator.gameObject, clip, Mathf.Min(i * dt, clip.length));
                Vector3 l = ToRoot(root, left.position);
                Vector3 r = ToRoot(root, right.position);

                // 低い方(接地している側)の足は、キャラクターの前進ぶんだけ後ろへ流れる
                bool leftStance = l.y < r.y;
                float backward = -((leftStance ? l.z - prevLeft.z : r.z - prevRight.z)) / dt;
                if (backward > 0.1f) speeds.Add(backward);   // 振り出し中の足は負になるので除く

                prevLeft = l;
                prevRight = r;
            }
        }
        finally
        {
            AnimationMode.StopAnimationMode();
        }

        if (speeds.Count == 0) return 0f;

        speeds.Sort();
        float measured = speeds[speeds.Count / 2];   // 中央値(接地中の値だけが残る)
        return measured > 0.2f && measured < 15f ? measured : 0f;
    }

    static Vector3 ToRoot(Transform root, Vector3 worldPosition) => root.InverseTransformPoint(worldPosition);

    static Transform FindFoot(Transform root, string side)
    {
        string wanted = side + "foot";
        foreach (var child in root.GetComponentsInChildren<Transform>())
        {
            string name = child.name.ToLowerInvariant().Replace(":", "").Replace("_", "").Replace(" ", "");
            if (name.EndsWith(wanted)) return child;   // mixamorig:LeftFoot など
        }
        return null;
    }

    // Mixamo の FBX は既定だと Generic で入り、アバターが共有できない。
    // Humanoid とループ設定をここで揃えておく(既に正しければ再インポートしない)。
    static void ConfigureMixamoImports()
    {
        if (!AssetDatabase.IsValidFolder(PlayerModelFolder)) return;

        foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { PlayerModelFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null) continue;

            string file = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            bool loop = file.Contains("idle") || file.Contains("walk") || file.Contains("run") || file.Contains("jog");
            bool dirty = false;

            if (importer.animationType != ModelImporterAnimationType.Human)
            {
                importer.animationType = ModelImporterAnimationType.Human;
                dirty = true;
            }

            // マテリアルと埋め込みテクスチャは Unity のインポーターに展開させる
            // (Materials/ にマテリアル、<モデル名>.fbm に埋め込みテクスチャが出る)
            if (importer.materialLocation != ModelImporterMaterialLocation.External)
            {
                importer.materialLocation = ModelImporterMaterialLocation.External;
                dirty = true;
            }

            var clips = importer.clipAnimations.Length > 0 ? importer.clipAnimations : importer.defaultClipAnimations;
            bool clipsChanged = false;
            if (clips != null)
            {
                foreach (var clip in clips)
                {
                    if (clip.loopTime == loop) continue;
                    clip.loopTime = loop;
                    clipsChanged = true;
                }
            }

            if (clipsChanged && clips.Length > 0) importer.clipAnimations = clips;
            if (dirty || clipsChanged) importer.SaveAndReimport();
        }
    }

    // ファイル名(とクリップ名)から Idle / Walk / Run / Jump・Fall を拾う。
    static LocomotionClips CollectLocomotionClips()
    {
        var result = new LocomotionClips();
        if (!AssetDatabase.IsValidFolder(PlayerModelFolder)) return result;

        foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { PlayerModelFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string file = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();

            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                var clip = asset as AnimationClip;
                if (clip == null || clip.length < 0.05f) continue;

                string key = file + " " + clip.name.ToLowerInvariant();
                if (result.Idle == null && key.Contains("idle")) result.Idle = clip;
                else if (result.Walk == null && key.Contains("walk")) result.Walk = clip;
                else if (result.Run == null && (key.Contains("run") || key.Contains("jog"))) result.Run = clip;
                else if (result.Air == null && (key.Contains("jump") || key.Contains("fall"))) result.Air = clip;
            }
        }
        return result;
    }

    // Speed で Idle → Walk → Run を切り替えるコントローラを作る。
    // Walk / Run はそれぞれ専用の再生倍率パラメータを持ち、ShinobiLocomotion が
    // 「実際の移動速度 ÷ そのクリップが等倍で進む速度」を書き込むので、
    // 歩きも走りも足が地面を蹴る速度に一致する(足の滑り対策)。
    // Grounded で空中のステートへ、Dashing は ShinobiLocomotion が書き込むだけ。
    static RuntimeAnimatorController BuildLocomotionController(LocomotionClips clips)
    {
        string controllerPath = PlayerModelFolder + "/PlayerAnimator.controller";
        if (AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath) != null)
            AssetDatabase.DeleteAsset(controllerPath);

        var controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
        controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
        controller.AddParameter("Grounded", AnimatorControllerParameterType.Bool);
        controller.AddParameter("Dashing", AnimatorControllerParameterType.Bool);
        controller.AddParameter(WalkPlaybackParameter, AnimatorControllerParameterType.Float);
        controller.AddParameter(RunPlaybackParameter, AnimatorControllerParameterType.Float);

        var machine = controller.layers[0].stateMachine;

        AnimatorState idle = clips.Idle != null ? AddState(machine, "Idle", clips.Idle) : null;
        AnimatorState walk = clips.Walk != null ? AddState(machine, "Walk", clips.Walk, WalkPlaybackParameter) : null;
        AnimatorState run = clips.Run != null ? AddState(machine, "Run", clips.Run, RunPlaybackParameter) : null;

        var ground = new List<AnimatorState>();
        if (idle != null) ground.Add(idle);
        if (walk != null) ground.Add(walk);
        if (run != null) ground.Add(run);

        if (ground.Count == 0)
        {
            AssetDatabase.SaveAssets();   // クリップが無ければ空のコントローラ
            return controller;
        }

        machine.defaultState = idle ?? walk ?? run;

        // 待機 ↔ 移動
        if (idle != null)
        {
            var moving = walk ?? run;
            if (moving != null)
            {
                Connect(idle, moving, 0.15f, AnimatorConditionMode.Greater, 0.05f, "Speed");
                Connect(moving, idle, 0.2f, AnimatorConditionMode.Less, 0.05f, "Speed");
            }
        }

        // 歩き ↔ 走り
        if (walk != null && run != null)
        {
            Connect(walk, run, 0.15f, AnimatorConditionMode.Greater, 0.6f, "Speed");
            Connect(run, walk, 0.15f, AnimatorConditionMode.Less, 0.55f, "Speed");
        }

        // 空中
        if (clips.Air != null)
        {
            var air = AddState(machine, "Air", clips.Air);
            foreach (var state in ground)
                Connect(state, air, 0.12f, AnimatorConditionMode.IfNot, 0f, "Grounded");

            // 着地してから戻る(Grounded を条件に入れないと Air に入った瞬間に戻ってしまう)
            if (run != null)
                ConnectWhileGrounded(air, run, 0.12f, AnimatorConditionMode.Greater, 0.6f, "Speed");
            if (idle != null)
                ConnectWhileGrounded(air, idle, 0.15f, AnimatorConditionMode.Less, 0.05f, "Speed");

            var fallback = walk ?? (idle == null ? run : null);
            if (fallback != null) ConnectWhileGrounded(air, fallback, 0.12f);
        }

        AssetDatabase.SaveAssets();
        return controller;
    }

    static AnimatorState AddState(AnimatorStateMachine machine, string name, Motion motion, string playbackParameter = null)
    {
        var state = machine.AddState(name);
        state.motion = motion;
        if (!string.IsNullOrEmpty(playbackParameter))
        {
            state.speedParameterActive = true;
            state.speedParameter = playbackParameter;
        }
        return state;
    }

    static void Connect(AnimatorState from, AnimatorState to, float duration,
                        AnimatorConditionMode mode, float threshold, string parameter)
    {
        var transition = from.AddTransition(to);
        transition.hasExitTime = false;
        transition.duration = duration;
        transition.AddCondition(mode, threshold, parameter);
    }

    // 空中から接地ステートへ戻る用(Grounded が true のときだけ)
    static void ConnectWhileGrounded(AnimatorState from, AnimatorState to, float duration,
                                     AnimatorConditionMode mode, float threshold, string parameter)
    {
        var transition = from.AddTransition(to);
        transition.hasExitTime = false;
        transition.duration = duration;
        transition.AddCondition(AnimatorConditionMode.If, 0f, "Grounded");
        transition.AddCondition(mode, threshold, parameter);
    }

    // 条件は Grounded だけ(速度が中間のときの受け皿)
    static void ConnectWhileGrounded(AnimatorState from, AnimatorState to, float duration)
    {
        var transition = from.AddTransition(to);
        transition.hasExitTime = false;
        transition.duration = duration;
        transition.AddCondition(AnimatorConditionMode.If, 0f, "Grounded");
    }

    // モデルのスケールを身長に合わせ、足元をプレイヤーの足元へ接地させる。
    // (pivot が腰にあるモデルでも浮き/埋まりが出ないようにする)
    static void FitModel(Transform model, float worldFeetY, float targetHeight)
    {
        var renderers = model.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return;

        Bounds bounds = GetBounds(renderers);
        float height = bounds.size.y;
        if (height > 0.05f && (height < targetHeight * 0.9f || height > targetHeight * 1.1f))
            model.localScale *= targetHeight / height;

        bounds = GetBounds(renderers);   // スケール後の再計算
        model.localPosition += Vector3.up * (worldFeetY - bounds.min.y);
    }

    static Bounds GetBounds(Renderer[] renderers)
    {
        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);
        return bounds;
    }

    // マテリアルとテクスチャは Unity のモデルインポーターが展開する設定にしてあるので、
    // ここでは「本当にテクスチャが付いているか」だけを確認する(付いていなければ警告)。
    static void WarnIfUntextured(GameObject model)
    {
        foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
        {
            foreach (var material in renderer.sharedMaterials)
            {
                if (material == null) continue;

                bool hasTexture =
                    (material.HasProperty("_BaseMap") && material.GetTexture("_BaseMap") != null) ||
                    (material.HasProperty("_MainTex") && material.GetTexture("_MainTex") != null);
                if (hasTexture) continue;

                Debug.LogWarning($"[StageBuilder] {renderer.name} のマテリアル '{material.name}' にテクスチャがありません。" +
                                 $" {PlayerModelFolder} のモデルを選び、Inspector の Materials タブで" +
                                 $" Extract Materials / Extract Textures を確認してください。");
            }
        }
    }

    static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (var child in go.transform)
            SetLayerRecursively(((Transform)child).gameObject, layer);
    }

    // プレイヤーの仮カプセル(Unity標準メッシュ)を隠して、忍者の体をプリミティブで組む。
    // モデルアセットが無いので、頭巾・目の穴・襟巻き・帯・背中の刀までコードで作る。
    // 体のパーツにはコライダーを付けない(当たり判定は元のカプセルのまま)。
    static void BuildShinobiBody(ShinobiPlayerController player, StageMaterials.Mats m)
    {
        var root = player.transform;

        // 元のカプセルの見た目だけ消す(CharacterController / CapsuleCollider は残す)
        foreach (var r in root.GetComponentsInChildren<MeshRenderer>())
            r.enabled = false;

        var body = new GameObject("Shinobi Body").transform;
        body.SetParent(root, false);
        body.localPosition = new Vector3(0f, FeetOffset(root), 0f);   // 原点=カプセル中心なので、足元まで下げる

        // --- 脚(足元 y=0、身長は約1.78m) ---
        Part(body, "Thigh L", PrimitiveType.Cube, new Vector3(-0.13f, 0.72f, 0f), new Vector3(0.16f, 0.46f, 0.18f), m.NinjaCloth);
        Part(body, "Thigh R", PrimitiveType.Cube, new Vector3(0.13f, 0.72f, 0f), new Vector3(0.16f, 0.46f, 0.18f), m.NinjaCloth);
        Part(body, "Shin L", PrimitiveType.Cube, new Vector3(-0.13f, 0.34f, 0.01f), new Vector3(0.14f, 0.40f, 0.16f), m.NinjaWrap);
        Part(body, "Shin R", PrimitiveType.Cube, new Vector3(0.13f, 0.34f, 0.01f), new Vector3(0.14f, 0.40f, 0.16f), m.NinjaWrap);
        Part(body, "Foot L", PrimitiveType.Cube, new Vector3(-0.13f, 0.06f, 0.07f), new Vector3(0.14f, 0.12f, 0.30f), m.NinjaCloth);
        Part(body, "Foot R", PrimitiveType.Cube, new Vector3(0.13f, 0.06f, 0.07f), new Vector3(0.14f, 0.12f, 0.30f), m.NinjaCloth);

        // --- 腰と胴 ---
        Part(body, "Hips", PrimitiveType.Cube, new Vector3(0f, 0.97f, 0f), new Vector3(0.38f, 0.24f, 0.26f), m.NinjaCloth);
        Part(body, "Waist", PrimitiveType.Cube, new Vector3(0f, 1.12f, 0f), new Vector3(0.34f, 0.16f, 0.23f), m.NinjaCloth);
        Part(body, "Belt", PrimitiveType.Cube, new Vector3(0f, 1.02f, 0f), new Vector3(0.41f, 0.09f, 0.29f), m.NinjaWrap);
        Part(body, "Belt knot", PrimitiveType.Cube, new Vector3(0f, 1.01f, 0.15f), new Vector3(0.09f, 0.09f, 0.05f), m.NinjaScarf);
        Part(body, "Chest", PrimitiveType.Cube, new Vector3(0f, 1.32f, 0f), new Vector3(0.44f, 0.32f, 0.27f), m.NinjaCloth);
        Part(body, "Chest strap", PrimitiveType.Cube, new Vector3(0f, 1.33f, 0.14f), new Vector3(0.10f, 0.38f, 0.02f), m.NinjaWrap, new Vector3(0f, 0f, 28f));

        // --- 腕(肩 → 二の腕 → 前腕 → 手) ---
        Part(body, "Shoulder L", PrimitiveType.Sphere, new Vector3(-0.25f, 1.42f, 0f), new Vector3(0.18f, 0.16f, 0.20f), m.NinjaCloth);
        Part(body, "Shoulder R", PrimitiveType.Sphere, new Vector3(0.25f, 1.42f, 0f), new Vector3(0.18f, 0.16f, 0.20f), m.NinjaCloth);
        Part(body, "Upper arm L", PrimitiveType.Cube, new Vector3(-0.29f, 1.22f, 0f), new Vector3(0.11f, 0.34f, 0.13f), m.NinjaCloth, new Vector3(0f, 0f, 8f));
        Part(body, "Upper arm R", PrimitiveType.Cube, new Vector3(0.29f, 1.22f, 0f), new Vector3(0.11f, 0.34f, 0.13f), m.NinjaCloth, new Vector3(0f, 0f, -8f));
        Part(body, "Forearm L", PrimitiveType.Cube, new Vector3(-0.33f, 0.98f, 0.02f), new Vector3(0.10f, 0.30f, 0.12f), m.NinjaWrap);
        Part(body, "Forearm R", PrimitiveType.Cube, new Vector3(0.33f, 0.98f, 0.02f), new Vector3(0.10f, 0.30f, 0.12f), m.NinjaWrap);
        Part(body, "Hand L", PrimitiveType.Cube, new Vector3(-0.35f, 0.80f, 0.03f), new Vector3(0.09f, 0.10f, 0.11f), m.NinjaCloth);
        Part(body, "Hand R", PrimitiveType.Cube, new Vector3(0.35f, 0.80f, 0.03f), new Vector3(0.09f, 0.10f, 0.11f), m.NinjaCloth);

        // --- 頭(頭巾 + 目の穴) ---
        Part(body, "Neck", PrimitiveType.Cylinder, new Vector3(0f, 1.51f, 0f), new Vector3(0.12f, 0.05f, 0.12f), m.NinjaWrap);
        Part(body, "Hood", PrimitiveType.Sphere, new Vector3(0f, 1.66f, -0.01f), new Vector3(0.21f, 0.24f, 0.22f), m.NinjaCloth);
        Part(body, "Face", PrimitiveType.Cube, new Vector3(0f, 1.65f, 0.10f), new Vector3(0.19f, 0.07f, 0.06f), m.NinjaSkin);

        // --- 襟巻き(首元 + 後ろへなびく尻尾) ---
        Part(body, "Scarf", PrimitiveType.Cube, new Vector3(0f, 1.48f, 0f), new Vector3(0.28f, 0.10f, 0.24f), m.NinjaScarf);
        Part(body, "Scarf tail 1", PrimitiveType.Cube, new Vector3(-0.02f, 1.28f, -0.19f), new Vector3(0.13f, 0.42f, 0.05f), m.NinjaScarf, new Vector3(-14f, 0f, 4f));
        Part(body, "Scarf tail 2", PrimitiveType.Cube, new Vector3(-0.04f, 1.02f, -0.27f), new Vector3(0.11f, 0.30f, 0.04f), m.NinjaScarf, new Vector3(-22f, 0f, 6f));

        // --- 背中の刀(鞘 + 柄 + 鍔) ---
        Part(body, "Sheath", PrimitiveType.Cube, new Vector3(0.06f, 1.24f, -0.20f), new Vector3(0.055f, 0.95f, 0.07f), m.NinjaSteel, new Vector3(0f, 0f, 32f));
        Part(body, "Tsuba", PrimitiveType.Cylinder, new Vector3(0.315f, 1.645f, -0.20f), new Vector3(0.14f, 0.012f, 0.14f), m.NinjaSteel, new Vector3(0f, 0f, 32f));
        Part(body, "Hilt", PrimitiveType.Cube, new Vector3(0.35f, 1.72f, -0.20f), new Vector3(0.05f, 0.30f, 0.06f), m.NinjaWrap, new Vector3(0f, 0f, 32f));
    }

    // プレイヤーの原点から足元までの距離。原点はカプセルの中心なので通常は -1。
    static float FeetOffset(Transform player)
    {
        var cc = player.GetComponent<CharacterController>();
        if (cc != null) return cc.center.y - cc.height * 0.5f;

        var capsule = player.GetComponent<CapsuleCollider>();
        if (capsule != null) return capsule.center.y - capsule.height * 0.5f;

        return -1f;
    }

    // 体のパーツを1つ作る。プリミティブのコライダーは外すので、見た目だけになる。
    static void Part(Transform parent, string name, PrimitiveType type, Vector3 pos, Vector3 scale,
                     Material material, Vector3? euler = null)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());

        var t = go.transform;
        t.SetParent(parent, false);
        t.localPosition = pos;
        t.localScale = scale;
        if (euler.HasValue) t.localRotation = Quaternion.Euler(euler.Value);

        go.GetComponent<Renderer>().sharedMaterial = material;
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

        // GameScene から引き継いだ太陽は、月明かりとして見た目だけ残す。
        // DecorativeLight が付いたライトは LightRegistry に入らないので、ShadowUtility の
        // 「屋外はどこでも明るい」判定には使われず、街灯の影のポケットは影のまま。
        foreach (var light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
        {
            if (light.type != LightType.Directional) continue;
            if (light.GetComponent<DecorativeLight>() == null)
                light.gameObject.AddComponent<DecorativeLight>();
        }
    }

    // 3人称カメラはプレイヤーを画面中央に構図するので、照準(画面中央)が自分の体で埋まり、
    // 遠くの街灯などを狙いづらい。ScreenPosition.y を正にするとプレイヤーは画面の下へ下がり、
    // 照準(画面中央)は体の少し上を通る。逆に負にすると照準は足元の地面を向く
    // (実測: -0.3 では約19°下を向いて、地面ばかり見える視界になった)。
    static void ConfigureAimCamera()
    {
        foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
        {
            if (mb == null || mb.GetType().Name != "CinemachineRotationComposer") continue;

            var so = new SerializedObject(mb);
            var screenPos = so.FindProperty("Composition.ScreenPosition");
            var centerOnActivate = so.FindProperty("CenterOnActivate");
            if (screenPos != null) screenPos.vector2Value = new Vector2(0f, 0.2f);   // +で体が画面下へ(0=中央, ±0.5=画面端)
            if (centerOnActivate != null) centerOnActivate.boolValue = false;        // 起動時に中央へ戻されないように
            so.ApplyModifiedPropertiesWithoutUndo();
        }
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

    // 街灯の光。intensity/range は「光の輪」が重ならない位に小さくして、
    // 輪と輪の間を影のルートとして歩けるようにする。
    static void Lamp(Transform parent, StageMaterials.Mats m, float x, float z, float intensity, float range)
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

        var bulb = lamp.transform.Find("Bulb");
        var light = lamp.GetComponentInChildren<Light>();
        if (light != null)
        {
            // 光は電球と同じ高さに置く(プレハブでは Point Light が Bulb の子で、
            // ルートから y=2.4)。地面にあった場合もここで電球の位置に揃う。
            if (bulb != null) light.transform.position = bulb.position;

            light.intensity = intensity;
            light.range = range;
        }

        // クナイで割れるように、電球に当たり判定を付ける。
        // (プレハブの Bulb は見た目だけで、コライダーが無かった)
        if (bulb != null && bulb.GetComponent<Collider>() == null)
        {
            var bulbCollider = bulb.gameObject.AddComponent<SphereCollider>();
            bulbCollider.radius = 0.8f;   // Bulb は scale 0.4 なので、実半径は約 0.32m
            bulb.gameObject.layer = BreakableLayer;
        }

        // 柱は StreetLamp.prefab 側に入っている(手置きの街灯も同じ見た目)。
        // 古いプレハブなどで欠けている場合だけ、地面から電球の下端まで補う。
        // Environment レイヤーなので、ナビメッシュの障害物・クナイの命中・
        // ShadowUtility の遮蔽物のどれにもなる。
        if (lamp.transform.Find("Post") == null)
        {
            float bulbY = bulb != null ? bulb.localPosition.y : 2.4f;
            float bulbRadius = bulb != null ? bulb.localScale.y * 0.5f : 0.2f;
            float postTop = Mathf.Max(0.1f, bulbY - bulbRadius);

            var post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);   // CapsuleCollider 付き
            post.name = "Post";
            post.layer = EnvLayer;
            post.transform.SetParent(lamp.transform, false);
            post.transform.localPosition = new Vector3(0f, postTop * 0.5f, 0f);
            post.transform.localScale = new Vector3(0.18f, postTop * 0.5f, 0.18f);   // 直径 0.18m
            post.GetComponent<Renderer>().sharedMaterial = m.LampPost;
        }
    }
}

/// <summary>ステージ用のマテリアルを Assets/Materials/Stage に用意する。</summary>
public static class StageMaterials
{
    public struct Mats
    {
        public Material Ground, Road, Wall, Crate, Guard, Goal, LampPost;
        public Material NinjaCloth, NinjaWrap, NinjaSkin, NinjaScarf, NinjaSteel;
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
            LampPost = Get("StageLampPost", new Color(0.14f, 0.14f, 0.16f), 0.55f),

            // プレイヤー(忍者)。地面(0.13)よりわずかに明るい藍色にして、暗がりでも輪郭が読めるようにする。
            NinjaCloth = Get("NinjaCloth", new Color(0.16f, 0.17f, 0.22f), 0.12f),
            NinjaWrap = Get("NinjaWrap", new Color(0.26f, 0.27f, 0.32f), 0.10f),
            NinjaSkin = Get("NinjaSkin", new Color(0.52f, 0.39f, 0.32f), 0.25f),
            NinjaScarf = Get("NinjaScarf", new Color(0.42f, 0.07f, 0.09f), 0.18f),
            NinjaSteel = Get("NinjaSteel", new Color(0.30f, 0.31f, 0.35f), 0.60f),
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
