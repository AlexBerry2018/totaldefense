using UnityEngine;
using UnityEngine.InputSystem;

[DefaultExecutionOrder(-100)]
public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    [Header("Экономика")]
    public float money = 500f;
    public int baseHealth = 1000;
    public int cannonCost = 25;
    public int mortarCost = 200;

    [Header("Волны")]
    public float firstWaveDelay = 10f;
    public float wavePause = 20f;
    public float spawnDuration = 10f;
    [Range(0f, 1f)] public float fastEnemyShare = 0.25f;

    [Header("Графика")]
    public Material baseMaterial;

    [Header("Замеры")]
    public int stressBatch = 5000;

    static readonly int[] WaveSizes = { 1000, 2000, 3000, 5000, 8000, 12000, 18000, 25000, 35000, 50000 };

    static readonly int[] WaveBounty = { 1000, 2000, 3000, 3500, 3500, 3500, 3500, 4000, 4000, 4000 };

    static readonly float[] WaveHealth = { 1f, 1f, 1.2f, 1.5f, 2f, 2.5f, 3f, 4f, 5f, 6f };

    public GridMap Grid { get; private set; }
    public Vector3 BasePosition { get; private set; }

    Mesh cubeMesh, capsuleMesh, sphereMesh, cylinderMesh;
    Material enemyMat, fastEnemyMat, cannonMat, mortarMat, projectileMat;
    Camera cam;

    int wave;
    int toSpawn;
    float spawnAccumulator;
    float waveTimer;
    int selectedTower = 1;
    bool paused;
    bool gameOver;
    string endMessage;
    float smoothedDt = 1f / 60f;
    GUIStyle hudStyle;

    void Awake()
    {
        Instance = this;
        Enemy.All.Clear();
        Tower.All.Clear();
        Projectile.Count = 0;
        Time.timeScale = 1f;

        Grid = new GridMap();
        Grid.Rebuild();
        BasePosition = GridMap.CellToWorld(GridMap.BaseCell) + Vector3.up * 0.5f;
        waveTimer = firstWaveDelay;

        BuildScene();
    }

    void BuildScene()
    {
        cubeMesh = GrabMesh(PrimitiveType.Cube);
        capsuleMesh = GrabMesh(PrimitiveType.Capsule);
        sphereMesh = GrabMesh(PrimitiveType.Sphere);
        cylinderMesh = GrabMesh(PrimitiveType.Cylinder);

        Material template = baseMaterial;
        if (template == null)
        {
            GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            template = tmp.GetComponent<Renderer>().sharedMaterial;
            Destroy(tmp);
            Debug.LogWarning("GameManager: не назначен Base Material, в сборке объекты будут розовыми.");
        }

        enemyMat = MakeMaterial(template, new Color(0.85f, 0.2f, 0.2f));
        fastEnemyMat = MakeMaterial(template, new Color(1f, 0.6f, 0.1f));
        cannonMat = MakeMaterial(template, new Color(0.3f, 0.5f, 0.9f));
        mortarMat = MakeMaterial(template, new Color(0.6f, 0.3f, 0.8f));
        projectileMat = MakeMaterial(template, new Color(1f, 1f, 0.4f));

        float worldSize = GridMap.Size * GridMap.CellSize;

        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = new Vector3(worldSize / 10f, 1f, worldSize / 10f);
        ground.GetComponent<Renderer>().sharedMaterial = MakeMaterial(template, new Color(0.18f, 0.22f, 0.18f));
        Destroy(ground.GetComponent<Collider>());

        CreateVisual("Base", cubeMesh, MakeMaterial(template, new Color(0.2f, 0.9f, 0.4f)),
            BasePosition + Vector3.up * 0.5f, new Vector3(3f, 2f, 3f));

        cam = Camera.main;
        if (cam == null)
        {
            GameObject camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            cam = camGo.AddComponent<Camera>();
        }
        cam.transform.SetPositionAndRotation(new Vector3(0f, 70f, -50f), Quaternion.Euler(55f, 0f, 0f));
        cam.farClipPlane = 500f;
        if (cam.GetComponent<CameraController>() == null) cam.gameObject.AddComponent<CameraController>();
    }

    static Mesh GrabMesh(PrimitiveType type)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        Mesh mesh = go.GetComponent<MeshFilter>().sharedMesh;
        Destroy(go);
        return mesh;
    }

    static Material MakeMaterial(Material template, Color color)
    {
        return new Material(template) { color = color, enableInstancing = true };
    }

    static GameObject CreateVisual(string name, Mesh mesh, Material mat, Vector3 pos, Vector3 scale)
    {
        GameObject go = new GameObject(name);
        go.transform.position = pos;
        go.transform.localScale = scale;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        return go;
    }

    void Update()
    {
        smoothedDt = Mathf.Lerp(smoothedDt, Time.unscaledDeltaTime, 0.05f);
        HandleInput();
        if (gameOver) return;

        if (toSpawn > 0)
        {
            spawnAccumulator += WaveSizes[wave - 1] / spawnDuration * Time.deltaTime;
            int n = Mathf.Min((int)spawnAccumulator, toSpawn);
            spawnAccumulator -= n;
            toSpawn -= n;
            for (int i = 0; i < n; i++) SpawnEnemy();
        }
        else if (wave < WaveSizes.Length)
        {
            waveTimer -= Time.deltaTime;
            if (waveTimer <= 0f) StartNextWave();
        }
        else if (Enemy.All.Count == 0)
        {
            EndGame("Победа: все волны отбиты");
        }
    }

    void StartNextWave()
    {
        toSpawn = WaveSizes[wave];
        wave++;
        spawnAccumulator = 0f;
        waveTimer = wavePause;
    }

    void HandleInput()
    {
        Keyboard kb = Keyboard.current;
        Mouse mouse = Mouse.current;
        if (kb == null || mouse == null) return;

        if (kb.spaceKey.wasPressedThisFrame && !gameOver)
        {
            paused = !paused;
            Time.timeScale = paused ? 0f : 1f;
        }
        if (kb.digit1Key.wasPressedThisFrame) selectedTower = 1;
        if (kb.digit2Key.wasPressedThisFrame) selectedTower = 2;

        if (gameOver) return;

        if (kb.nKey.wasPressedThisFrame && toSpawn == 0 && wave < WaveSizes.Length) StartNextWave();
        if (kb.tKey.wasPressedThisFrame)
        {
            for (int i = 0; i < stressBatch; i++) SpawnEnemy(false);
        }

        if (mouse.leftButton.wasPressedThisFrame) TryPlaceTower(mouse.position.ReadValue());
    }

    void TryPlaceTower(Vector2 screenPos)
    {
        Ray ray = cam.ScreenPointToRay(screenPos);
        Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
        if (!groundPlane.Raycast(ray, out float enter)) return;

        Vector2Int cell = GridMap.WorldToCell(ray.GetPoint(enter));
        int cost = selectedTower == 1 ? cannonCost : mortarCost;
        if (money < cost) return;
        if (!Grid.TryBlock(cell)) return;

        money -= cost;
        Vector3 pos = GridMap.CellToWorld(cell);

        if (selectedTower == 1)
        {
            GameObject go = CreateVisual("Cannon", cubeMesh, cannonMat,
                pos + Vector3.up * 0.75f, new Vector3(0.9f, 1.5f, 0.9f));
            Tower t = go.AddComponent<Tower>();
            t.range = 8f; t.damage = 5f; t.cooldown = 0.2f; t.splashRadius = 0f;
        }
        else
        {
            GameObject go = CreateVisual("Mortar", cylinderMesh, mortarMat,
                pos + Vector3.up * 0.75f, new Vector3(0.9f, 0.75f, 0.9f));
            Tower t = go.AddComponent<Tower>();
            t.range = 12f; t.damage = 15f; t.cooldown = 1.5f; t.splashRadius = 2.5f;
        }
    }

    void SpawnEnemy(bool givesReward = true)
    {
        int max = GridMap.Size - 1;
        int along = Random.Range(0, GridMap.Size);
        Vector2Int cell;
        switch (Random.Range(0, 4))
        {
            case 0: cell = new Vector2Int(along, 0); break;
            case 1: cell = new Vector2Int(along, max); break;
            case 2: cell = new Vector2Int(0, along); break;
            default: cell = new Vector2Int(max, along); break;
        }

        Vector3 pos = GridMap.CellToWorld(cell)
                      + new Vector3(Random.Range(-0.4f, 0.4f), 0.5f, Random.Range(-0.4f, 0.4f));

        bool fast = Random.value < fastEnemyShare;
        GameObject go = CreateVisual("Enemy", capsuleMesh, fast ? fastEnemyMat : enemyMat,
            pos, new Vector3(0.5f, 0.5f, 0.5f));

        Enemy e = go.AddComponent<Enemy>();
        int w = Mathf.Clamp(wave - 1, 0, WaveSizes.Length - 1);
        e.speed = fast ? 6f : 3f;
        e.health = (fast ? 5f : 10f) * WaveHealth[w];
        e.reward = givesReward ? (float)WaveBounty[w] / WaveSizes[w] : 0f;
    }

    public void SpawnProjectile(Vector3 from, Enemy target, float damage, float splashRadius, float speed)
    {
        GameObject go = CreateVisual("Projectile", sphereMesh, projectileMat, from, Vector3.one * 0.3f);
        go.AddComponent<Projectile>().Init(target, damage, splashRadius, speed);
    }

    public void AddMoney(float amount) => money += amount;

    public void DamageBase(int amount)
    {
        if (gameOver) return;
        baseHealth -= amount;
        if (baseHealth <= 0)
        {
            baseHealth = 0;
            EndGame("Поражение: база разрушена");
        }
    }

    void EndGame(string message)
    {
        gameOver = true;
        endMessage = message;
        Time.timeScale = 0f;
    }

    void OnGUI()
    {
        if (hudStyle == null)
        {
            hudStyle = new GUIStyle(GUI.skin.box) { fontSize = 18, alignment = TextAnchor.UpperLeft };
            hudStyle.normal.textColor = Color.white;
        }

        string waveInfo = toSpawn > 0
            ? $"Волна {wave}/{WaveSizes.Length} (ещё появится: {toSpawn})"
            : wave < WaveSizes.Length
                ? $"Волна {wave}/{WaveSizes.Length}, следующая через {Mathf.CeilToInt(waveTimer)} с"
                : $"Волна {wave}/{WaveSizes.Length}";

        string text =
            $"FPS: {1f / smoothedDt:0}   Кадр: {smoothedDt * 1000f:0.0} мс\n" +
            $"Врагов: {Enemy.All.Count}   Башен: {Tower.All.Count}   Снарядов: {Projectile.Count}\n" +
            $"{waveInfo}\n" +
            $"База: {baseHealth}   Деньги: {Mathf.FloorToInt(money)}\n" +
            $"Башня: {(selectedTower == 1 ? $"пушка ({cannonCost})" : $"мортира ({mortarCost})")}" +
            (paused ? "   ПАУЗА" : "");

        GUI.Box(new Rect(10, 10, 520, 135), text, hudStyle);

        if (gameOver)
        {
            GUI.Box(new Rect(Screen.width / 2f - 220, Screen.height / 2f - 30, 440, 60), endMessage, hudStyle);
        }
    }
}
