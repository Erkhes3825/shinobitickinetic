using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

public enum GameState { Playing, Won, Lost }

/// <summary>
/// ゲームの勝ち負けを管理する。
/// - Win() / Lose() で、ゲームを止めて、結果を表示する
/// - 結果表示中に R キー(ゲームパッドは Start)で、やり直し
/// シーンに置かなくても、再生時に自動で作られる。
/// </summary>
public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    public GameState State { get; private set; } = GameState.Playing;
    public bool IsPlaying => State == GameState.Playing;

    [SerializeField] bool showControls = true;   // 画面左上に、操作の説明を表示する

    string message = "";
    GUIStyle bigStyle, smallStyle, centerStyle;

    // Domain Reload無効時でも、再生ごとに状態をリセットする
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Instance = null;

    // シーンに GameManager が無ければ、自動で作る(置き忘れ対策)
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoCreate()
    {
        if (FindFirstObjectByType<GameManager>() == null)
            new GameObject("GameManager").AddComponent<GameManager>();
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);   // シーンを再読み込みしても、残す
    }

    void OnEnable() { SceneManager.sceneLoaded += OnSceneLoaded; }
    void OnDisable() { SceneManager.sceneLoaded -= OnSceneLoaded; }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // シーンを読み込み直したら、最初の状態に戻す
        State = GameState.Playing;
        message = "";
        Time.timeScale = 1f;
    }

    public void Win(string msg = "CLEAR!") => End(GameState.Won, msg);
    public void Lose(string msg = "CAUGHT!") => End(GameState.Lost, msg);

    void End(GameState result, string msg)
    {
        if (!IsPlaying) return;   // 二重に呼ばれても、最初の1回だけ
        State = result;
        message = msg;
        Time.timeScale = 0f;      // ゲームを止める
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        Debug.Log($"[GameManager] {result}: {msg}");
    }

    void Update()
    {
        if (IsPlaying) return;

        // 変わり身の術のスローモーション等が、時間を元に戻さないよう、止め続ける
        Time.timeScale = 0f;

        bool restart = (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
                    || (Gamepad.current != null && Gamepad.current.startButton.wasPressedThisFrame);
        if (restart) Restart();
    }

    public void Restart()
    {
        Time.timeScale = 1f;

        int index = SceneManager.GetActiveScene().buildIndex;
        if (index < 0)
        {
            Debug.LogError("[GameManager] 今のシーンが Build Settings に登録されていません。" +
                           "File > Build Profiles(Build Settings)で「Add Open Scenes」を押してください。");
            return;
        }
        SceneManager.LoadScene(index);
    }

    // ---- 画面表示(IMGUI。Canvasの設定が不要) ----
    void EnsureStyles()
    {
        if (bigStyle != null) return;

        bigStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 72,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        centerStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 24,
            alignment = TextAnchor.MiddleCenter
        };
        centerStyle.normal.textColor = Color.white;
        smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 15 };
        smallStyle.normal.textColor = Color.white;
    }

    void OnGUI()
    {
        EnsureStyles();

        if (IsPlaying)
        {
            if (showControls)
            {
                GUI.Label(new Rect(16, 10, 1200, 60),
                    "WASD Move | Space Jump | Tab Lock-on | Shift/E Dash | Q Substitute | F Shadow Step | LMB Kunai",
                    smallStyle);
            }
            return;
        }

        // 結果の表示(画面を暗くして、メッセージを中央に)
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = Color.white;

        bigStyle.normal.textColor = State == GameState.Won ? new Color(0.4f, 1f, 0.5f) : new Color(1f, 0.35f, 0.35f);
        float y = Screen.height * 0.35f;
        GUI.Label(new Rect(0, y, Screen.width, 100), message, bigStyle);
        GUI.Label(new Rect(0, y + 110, Screen.width, 40), "Press R to restart", centerStyle);
    }
}