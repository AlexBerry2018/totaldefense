using ECS;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

public class EcsGameManager : MonoBehaviour
{
    [Header("Экономика")]
    public float startMoney = 500f;
    public int baseHealth = 1000;
    public int cannonCost = 25;
    public int mortarCost = 200;

    [Header("Волны")]
    public float firstWaveDelay = 10f;
    public float wavePause = 20f;
    public float spawnDuration = 10f;
    [Range(0f, 1f)] public float fastEnemyShare = 0.25f;

    [Header("Режим")]
    public bool useJobs = true;

    [Header("Графика")]
    public Material baseMaterial;

    [Header("Замеры")]
    public int stressBatch = 5000;

    const int MaterialEnemy = 0, MaterialFastEnemy = 1, MaterialCannon = 2, MaterialMortar = 3, MaterialProjectile = 4;
    const int MeshCapsule = 0, MeshCube = 1, MeshCylinder = 2, MeshSphere = 3;

    GridMap grid;
    Camera cam;
    World world;
    EntityManager em;
    Entity gameEntity;
    Entity cannonPrefab;
    Entity mortarPrefab;
    EntityQuery enemyQuery;
    EntityQuery towerQuery;
    EntityQuery projectileQuery;
    EntityQuery cleanupQuery;
    NativeArray<int> fieldAnchor;
    NativeArray<byte> fieldBlocked;
    NativeArray<float2> fieldExit;

    int selectedTower = 1;
    bool paused;
    float smoothedDt = 1f / 60f;
    GUIStyle hudStyle;

    void Awake()
    {
        Time.timeScale = 1f;

        grid = new GridMap();
        grid.Rebuild();

        world = World.DefaultGameObjectInjectionWorld;
        em = world.EntityManager;

        enemyQuery = em.CreateEntityQuery(typeof(EnemyData));
        towerQuery = em.CreateEntityQuery(typeof(TowerData));
        projectileQuery = em.CreateEntityQuery(typeof(ProjectileData));
        cleanupQuery = em.CreateEntityQuery(new EntityQueryDesc
        {
            All = new ComponentType[] { typeof(GameEntity) },
            Options = EntityQueryOptions.IncludePrefab
        });

        Material template = baseMaterial;
        if (template == null)
        {
            GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            template = tmp.GetComponent<Renderer>().sharedMaterial;
            Destroy(tmp);
            Debug.LogWarning("EcsGameManager: не назначен Base Material, в сборке объекты будут розовыми.");
        }

        BuildScene(template);
        CreateEntities(template);
    }

    void OnDestroy()
    {
        if (world != null && world.IsCreated)
        {
            em.DestroyEntity(cleanupQuery);
        }
        if (fieldAnchor.IsCreated) fieldAnchor.Dispose();
        if (fieldBlocked.IsCreated) fieldBlocked.Dispose();
        if (fieldExit.IsCreated) fieldExit.Dispose();
    }

    void BuildScene(Material template)
    {
        float worldSize = GridMap.Size * GridMap.CellSize;

        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = new Vector3(worldSize / 10f, 1f, worldSize / 10f);
        ground.GetComponent<Renderer>().sharedMaterial = MakeMaterial(template, new Color(0.18f, 0.22f, 0.18f));
        Destroy(ground.GetComponent<Collider>());

        GameObject baseObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
        baseObject.name = "Base";
        baseObject.transform.position = GridMap.CellToWorld(GridMap.BaseCell) + Vector3.up;
        baseObject.transform.localScale = new Vector3(3f, 2f, 3f);
        baseObject.GetComponent<Renderer>().sharedMaterial = MakeMaterial(template, new Color(0.2f, 0.9f, 0.4f));
        Destroy(baseObject.GetComponent<Collider>());

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

    void CreateEntities(Material template)
    {
        Material[] materials =
        {
            MakeMaterial(template, new Color(0.85f, 0.2f, 0.2f)),
            MakeMaterial(template, new Color(1f, 0.6f, 0.1f)),
            MakeMaterial(template, new Color(0.3f, 0.5f, 0.9f)),
            MakeMaterial(template, new Color(0.6f, 0.3f, 0.8f)),
            MakeMaterial(template, new Color(1f, 1f, 0.4f))
        };
        Mesh[] meshes =
        {
            GrabMesh(PrimitiveType.Capsule),
            GrabMesh(PrimitiveType.Cube),
            GrabMesh(PrimitiveType.Cylinder),
            GrabMesh(PrimitiveType.Sphere)
        };

        RenderMeshArray renderMeshArray = new RenderMeshArray(materials, meshes);
        RenderMeshDescription description = new RenderMeshDescription(
            shadowCastingMode: ShadowCastingMode.Off,
            receiveShadows: false);

        Entity enemyPrefab = CreatePrefab(description, renderMeshArray, MaterialEnemy, MeshCapsule);
        em.AddComponentData(enemyPrefab, new EnemyData());
        em.AddComponentData(enemyPrefab, new Health());

        Entity fastEnemyPrefab = CreatePrefab(description, renderMeshArray, MaterialFastEnemy, MeshCapsule);
        em.AddComponentData(fastEnemyPrefab, new EnemyData());
        em.AddComponentData(fastEnemyPrefab, new Health());

        Entity projectilePrefab = CreatePrefab(description, renderMeshArray, MaterialProjectile, MeshSphere);
        em.AddComponentData(projectilePrefab, new ProjectileData());

        cannonPrefab = CreatePrefab(description, renderMeshArray, MaterialCannon, MeshCube);
        em.AddComponentData(cannonPrefab, new TowerData
        {
            Range = 8f, Damage = 5f, Cooldown = 0.2f, SplashRadius = 0f, ProjectileSpeed = 30f
        });
        em.AddComponentData(cannonPrefab, new PostTransformMatrix { Value = float4x4.Scale(0.9f, 1.5f, 0.9f) });

        mortarPrefab = CreatePrefab(description, renderMeshArray, MaterialMortar, MeshCylinder);
        em.AddComponentData(mortarPrefab, new TowerData
        {
            Range = 12f, Damage = 15f, Cooldown = 1.5f, SplashRadius = 2.5f, ProjectileSpeed = 30f
        });
        em.AddComponentData(mortarPrefab, new PostTransformMatrix { Value = float4x4.Scale(0.9f, 0.75f, 0.9f) });

        int cells = GridMap.Size * GridMap.Size;
        fieldAnchor = new NativeArray<int>(cells, Allocator.Persistent);
        fieldBlocked = new NativeArray<byte>(cells, Allocator.Persistent);
        fieldExit = new NativeArray<float2>(cells, Allocator.Persistent);
        SyncFlowField();

        Vector3 basePosition = GridMap.CellToWorld(GridMap.BaseCell);

        gameEntity = em.CreateEntity();
        em.AddComponent<GameEntity>(gameEntity);
        if (useJobs) em.AddComponent<JobsMode>(gameEntity);
        else em.AddComponent<MainThreadMode>(gameEntity);
        em.AddComponentData(gameEntity, new GameConfig
        {
            EnemyPrefab = enemyPrefab,
            FastEnemyPrefab = fastEnemyPrefab,
            ProjectilePrefab = projectilePrefab,
            BasePosition = new float3(basePosition.x, 0.5f, basePosition.z),
            WavePause = wavePause,
            SpawnDuration = spawnDuration,
            FastEnemyShare = fastEnemyShare
        });
        em.AddComponentData(gameEntity, new GameState
        {
            Money = startMoney,
            BaseHealth = baseHealth,
            WaveTimer = firstWaveDelay
        });
        em.AddComponentData(gameEntity, new FlowField
        {
            Anchor = fieldAnchor,
            Blocked = fieldBlocked,
            ExitDir = fieldExit
        });
    }

    Entity CreatePrefab(RenderMeshDescription description, RenderMeshArray renderMeshArray, int material, int mesh)
    {
        Entity entity = em.CreateEntity();
        RenderMeshUtility.AddComponents(entity, em, description, renderMeshArray,
            MaterialMeshInfo.FromRenderMeshArrayIndices(material, mesh));
        em.AddComponentData(entity, LocalTransform.Identity);
        em.AddComponentData(entity, new LocalToWorld { Value = float4x4.identity });
        em.AddComponent<GameEntity>(entity);
        em.AddComponent<Prefab>(entity);
        return entity;
    }

    void SyncFlowField()
    {
        int[] anchors = grid.Anchors;
        Vector2[] exits = grid.ExitDirs;
        bool[] blocked = grid.Blocked;
        for (int i = 0; i < anchors.Length; i++)
        {
            fieldAnchor[i] = anchors[i];
            fieldBlocked[i] = blocked[i] ? (byte)1 : (byte)0;
            fieldExit[i] = new float2(exits[i].x, exits[i].y);
        }
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

    void Update()
    {
        smoothedDt = Mathf.Lerp(smoothedDt, Time.unscaledDeltaTime, 0.05f);

        GameState game = em.GetComponentData<GameState>(gameEntity);
        if (game.GameOver)
        {
            Time.timeScale = 0f;
            return;
        }

        Keyboard kb = Keyboard.current;
        Mouse mouse = Mouse.current;
        if (kb == null || mouse == null) return;

        if (kb.spaceKey.wasPressedThisFrame)
        {
            paused = !paused;
            Time.timeScale = paused ? 0f : 1f;
        }
        if (kb.digit1Key.wasPressedThisFrame) selectedTower = 1;
        if (kb.digit2Key.wasPressedThisFrame) selectedTower = 2;

        bool changed = false;
        if (kb.nKey.wasPressedThisFrame)
        {
            game.SkipWaveRequest = true;
            changed = true;
        }
        if (kb.tKey.wasPressedThisFrame)
        {
            game.StressRequest += stressBatch;
            changed = true;
        }
        if (mouse.leftButton.wasPressedThisFrame && TryPlaceTower(mouse.position.ReadValue(), ref game))
        {
            changed = true;
        }

        if (changed) em.SetComponentData(gameEntity, game);
    }

    bool TryPlaceTower(Vector2 screenPos, ref GameState game)
    {
        Ray ray = cam.ScreenPointToRay(screenPos);
        Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
        if (!groundPlane.Raycast(ray, out float enter)) return false;

        Vector2Int cell = GridMap.WorldToCell(ray.GetPoint(enter));
        int cost = selectedTower == 1 ? cannonCost : mortarCost;
        if (game.Money < cost) return false;
        if (!grid.TryBlock(cell)) return false;

        em.CompleteAllTrackedJobs();
        SyncFlowField();
        game.Money -= cost;

        Vector3 position = GridMap.CellToWorld(cell);
        Entity tower = em.Instantiate(selectedTower == 1 ? cannonPrefab : mortarPrefab);
        em.SetComponentData(tower, LocalTransform.FromPosition(new float3(position.x, 0.75f, position.z)));
        return true;
    }

    void OnGUI()
    {
        if (hudStyle == null)
        {
            hudStyle = new GUIStyle(GUI.skin.box) { fontSize = 18, alignment = TextAnchor.UpperLeft };
            hudStyle.normal.textColor = Color.white;
        }

        GameState game = em.GetComponentData<GameState>(gameEntity);

        string waveInfo = game.ToSpawn > 0
            ? $"Волна {game.Wave}/{WaveTable.Count} (ещё появится: {game.ToSpawn})"
            : game.Wave < WaveTable.Count
                ? $"Волна {game.Wave}/{WaveTable.Count}, следующая через {Mathf.CeilToInt(game.WaveTimer)} с"
                : $"Волна {game.Wave}/{WaveTable.Count}";

        string text =
            $"{(useJobs ? "ECS + Jobs + Burst" : "ECS")}   FPS: {1f / smoothedDt:0}   Кадр: {smoothedDt * 1000f:0.0} мс\n" +
            $"Врагов: {enemyQuery.CalculateEntityCount()}   Башен: {towerQuery.CalculateEntityCount()}   " +
            $"Снарядов: {projectileQuery.CalculateEntityCount()}\n" +
            $"{waveInfo}\n" +
            $"База: {game.BaseHealth}   Деньги: {Mathf.FloorToInt(game.Money)}\n" +
            $"Башня: {(selectedTower == 1 ? $"пушка ({cannonCost})" : $"мортира ({mortarCost})")}" +
            (paused ? "   ПАУЗА" : "");

        GUI.Box(new Rect(10, 10, 520, 135), text, hudStyle);

        if (game.GameOver)
        {
            string message = game.Victory ? "Победа: все волны отбиты" : "Поражение: база разрушена";
            GUI.Box(new Rect(Screen.width / 2f - 220, Screen.height / 2f - 30, 440, 60), message, hudStyle);
        }
    }
}
