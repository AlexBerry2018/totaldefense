using ECS;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(TransformSystemGroup))]
public partial struct WaveSpawnSystem : ISystem
{
    Random random;
    EntityQuery enemyQuery;

    public void OnCreate(ref SystemState state)
    {
        random = new Random(20261008u);
        enemyQuery = SystemAPI.QueryBuilder().WithAll<EnemyData>().Build();
        state.RequireForUpdate<GameConfig>();
        state.RequireForUpdate<GameState>();
    }

    public void OnUpdate(ref SystemState state)
    {
        GameConfig config = SystemAPI.GetSingleton<GameConfig>();
        GameState game = SystemAPI.GetSingleton<GameState>();
        if (game.GameOver) return;

        float dt = SystemAPI.Time.DeltaTime;
        int waveIndex = math.clamp(game.Wave - 1, 0, WaveTable.Count - 1);

        if (game.StressRequest > 0)
        {
            Spawn(ref state, config, game.StressRequest, waveIndex, false);
            game.StressRequest = 0;
        }

        if (game.SkipWaveRequest)
        {
            game.SkipWaveRequest = false;
            if (game.ToSpawn == 0 && game.Wave < WaveTable.Count) StartNextWave(ref game, config);
        }
        else if (game.ToSpawn > 0)
        {
            game.SpawnAccumulator += WaveTable.Sizes[game.Wave - 1] / config.SpawnDuration * dt;
            int n = math.min((int)game.SpawnAccumulator, game.ToSpawn);
            game.SpawnAccumulator -= n;
            game.ToSpawn -= n;
            if (n > 0) Spawn(ref state, config, n, game.Wave - 1, true);
        }
        else if (game.Wave < WaveTable.Count)
        {
            game.WaveTimer -= dt;
            if (game.WaveTimer <= 0f) StartNextWave(ref game, config);
        }
        else if (enemyQuery.IsEmpty)
        {
            game.GameOver = true;
            game.Victory = true;
        }

        SystemAPI.SetSingleton(game);
    }

    static void StartNextWave(ref GameState game, in GameConfig config)
    {
        game.ToSpawn = WaveTable.Sizes[game.Wave];
        game.Wave++;
        game.SpawnAccumulator = 0f;
        game.WaveTimer = config.WavePause;
    }

    void Spawn(ref SystemState state, in GameConfig config, int count, int waveIndex, bool givesReward)
    {
        int fastCount = 0;
        for (int i = 0; i < count; i++)
        {
            if (random.NextFloat() < config.FastEnemyShare) fastCount++;
        }

        float healthMultiplier = WaveTable.HealthMultiplier[waveIndex];
        float reward = givesReward ? (float)WaveTable.Bounty[waveIndex] / WaveTable.Sizes[waveIndex] : 0f;

        SpawnBatch(ref state, config.EnemyPrefab, count - fastCount, 3f, 10f * healthMultiplier, reward);
        SpawnBatch(ref state, config.FastEnemyPrefab, fastCount, 6f, 5f * healthMultiplier, reward);
    }

    void SpawnBatch(ref SystemState state, Entity prefab, int count, float speed, float health, float reward)
    {
        if (count <= 0) return;

        EntityManager em = state.EntityManager;
        NativeArray<Entity> entities = em.Instantiate(prefab, count, Allocator.Temp);
        const int size = GridMap.Size;

        for (int i = 0; i < entities.Length; i++)
        {
            int along = random.NextInt(0, size);
            int cx, cy;
            switch (random.NextInt(0, 4))
            {
                case 0: cx = along; cy = 0; break;
                case 1: cx = along; cy = size - 1; break;
                case 2: cx = 0; cy = along; break;
                default: cx = size - 1; cy = along; break;
            }

            float3 position = new float3(
                cx - size * 0.5f + 0.5f + random.NextFloat(-0.4f, 0.4f),
                0.5f,
                cy - size * 0.5f + 0.5f + random.NextFloat(-0.4f, 0.4f));

            em.SetComponentData(entities[i], LocalTransform.FromPositionRotationScale(position, quaternion.identity, 0.5f));
            em.SetComponentData(entities[i], new EnemyData { Speed = speed, Reward = reward });
            em.SetComponentData(entities[i], new Health { Value = health });
        }

        entities.Dispose();
    }
}

[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(WaveSpawnSystem))]
[UpdateBefore(typeof(TransformSystemGroup))]
public partial struct EnemyMoveSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<MainThreadMode>();
        state.RequireForUpdate<GameConfig>();
        state.RequireForUpdate<GameState>();
        state.RequireForUpdate<FlowField>();
    }

    public void OnUpdate(ref SystemState state)
    {
        GameConfig config = SystemAPI.GetSingleton<GameConfig>();
        GameState game = SystemAPI.GetSingleton<GameState>();
        FlowField field = SystemAPI.GetSingleton<FlowField>();
        if (game.GameOver) return;

        float dt = SystemAPI.Time.DeltaTime;
        float2 basePosition = config.BasePosition.xz;
        int leaked = 0;

        foreach (var (transform, health, enemy) in
                 SystemAPI.Query<RefRW<LocalTransform>, RefRW<Health>, RefRW<EnemyData>>())
        {
            if (health.ValueRO.Value <= 0f) continue;

            float3 position = transform.ValueRO.Position;
            float2 flat = position.xz;

            if (math.lengthsq(basePosition - flat) < 1.5f * 1.5f)
            {
                leaked++;
                health.ValueRW.Value = 0f;
                enemy.ValueRW.Reward = 0f;
                continue;
            }

            flat += field.Direction(flat) * (enemy.ValueRO.Speed * dt);
            transform.ValueRW.Position = new float3(flat.x, position.y, flat.y);
        }

        if (leaked > 0)
        {
            game.BaseHealth -= leaked;
            if (game.BaseHealth <= 0)
            {
                game.BaseHealth = 0;
                game.GameOver = true;
                game.Victory = false;
            }
            SystemAPI.SetSingleton(game);
        }
    }
}

[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(EnemyMoveSystem))]
[UpdateBefore(typeof(TransformSystemGroup))]
public partial struct TowerShootSystem : ISystem
{
    EntityQuery enemyQuery;

    public void OnCreate(ref SystemState state)
    {
        enemyQuery = SystemAPI.QueryBuilder().WithAll<EnemyData, Health, LocalTransform>().Build();
        state.RequireForUpdate<MainThreadMode>();
        state.RequireForUpdate<GameConfig>();
        state.RequireForUpdate<GameState>();
    }

    public void OnUpdate(ref SystemState state)
    {
        GameConfig config = SystemAPI.GetSingleton<GameConfig>();
        GameState game = SystemAPI.GetSingleton<GameState>();
        if (game.GameOver) return;

        float dt = SystemAPI.Time.DeltaTime;

        NativeArray<Entity> enemies = enemyQuery.ToEntityArray(Allocator.Temp);
        NativeArray<LocalTransform> enemyTransforms = enemyQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        NativeArray<Health> enemyHealth = enemyQuery.ToComponentDataArray<Health>(Allocator.Temp);
        EntityCommandBuffer ecb = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (tower, transform) in SystemAPI.Query<RefRW<TowerData>, RefRO<LocalTransform>>())
        {
            tower.ValueRW.Timer -= dt;

            float3 towerPosition = transform.ValueRO.Position;
            float bestDistance = tower.ValueRO.Range * tower.ValueRO.Range;
            int best = -1;

            for (int i = 0; i < enemies.Length; i++)
            {
                if (enemyHealth[i].Value <= 0f) continue;
                float distance = math.lengthsq(enemyTransforms[i].Position.xz - towerPosition.xz);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            if (best < 0 || tower.ValueRO.Timer > 0f) continue;

            tower.ValueRW.Timer = tower.ValueRO.Cooldown;

            Entity projectile = ecb.Instantiate(config.ProjectilePrefab);
            float3 start = towerPosition + new float3(0f, 1f, 0f);
            ecb.SetComponent(projectile, LocalTransform.FromPositionRotationScale(start, quaternion.identity, 0.3f));
            ecb.SetComponent(projectile, new ProjectileData
            {
                Target = enemies[best],
                TargetPos = enemyTransforms[best].Position,
                Damage = tower.ValueRO.Damage,
                SplashRadius = tower.ValueRO.SplashRadius,
                Speed = tower.ValueRO.ProjectileSpeed
            });
        }

        ecb.Playback(state.EntityManager);
        ecb.Dispose();
        enemies.Dispose();
        enemyTransforms.Dispose();
        enemyHealth.Dispose();
    }
}

[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(TowerShootSystem))]
[UpdateBefore(typeof(TransformSystemGroup))]
public partial struct ProjectileSystem : ISystem
{
    struct Explosion
    {
        public float2 Position;
        public float RadiusSq;
        public float Damage;
    }

    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<MainThreadMode>();
        state.RequireForUpdate<GameConfig>();
        state.RequireForUpdate<GameState>();
    }

    public void OnUpdate(ref SystemState state)
    {
        GameState game = SystemAPI.GetSingleton<GameState>();
        if (game.GameOver) return;

        float dt = SystemAPI.Time.DeltaTime;
        NativeList<Explosion> explosions = new NativeList<Explosion>(Allocator.Temp);
        EntityCommandBuffer ecb = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (transform, projectile, entity) in
                 SystemAPI.Query<RefRW<LocalTransform>, RefRW<ProjectileData>>().WithEntityAccess())
        {
            Entity target = projectile.ValueRO.Target;
            bool targetAlive = SystemAPI.HasComponent<Health>(target) && SystemAPI.GetComponent<Health>(target).Value > 0f;
            if (targetAlive) projectile.ValueRW.TargetPos = SystemAPI.GetComponent<LocalTransform>(target).Position;

            float3 targetPosition = projectile.ValueRO.TargetPos;
            float3 position = transform.ValueRO.Position;
            float3 to = targetPosition - position;
            float distance = math.length(to);
            float step = projectile.ValueRO.Speed * dt;

            if (distance > step + 0.1f)
            {
                transform.ValueRW.Position = position + to / distance * step;
                continue;
            }

            if (dt <= 0f) continue;

            if (projectile.ValueRO.SplashRadius > 0f)
            {
                explosions.Add(new Explosion
                {
                    Position = targetPosition.xz,
                    RadiusSq = projectile.ValueRO.SplashRadius * projectile.ValueRO.SplashRadius,
                    Damage = projectile.ValueRO.Damage
                });
            }
            else if (targetAlive)
            {
                Health health = SystemAPI.GetComponent<Health>(target);
                health.Value -= projectile.ValueRO.Damage;
                SystemAPI.SetComponent(target, health);
            }

            ecb.DestroyEntity(entity);
        }

        if (explosions.Length > 0)
        {
            foreach (var (health, transform) in
                     SystemAPI.Query<RefRW<Health>, RefRO<LocalTransform>>().WithAll<EnemyData>())
            {
                if (health.ValueRO.Value <= 0f) continue;
                float2 position = transform.ValueRO.Position.xz;

                for (int i = 0; i < explosions.Length; i++)
                {
                    if (math.lengthsq(position - explosions[i].Position) <= explosions[i].RadiusSq)
                    {
                        health.ValueRW.Value -= explosions[i].Damage;
                    }
                }
            }
        }

        ecb.Playback(state.EntityManager);
        ecb.Dispose();
        explosions.Dispose();
    }
}

[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(ProjectileSystem))]
[UpdateBefore(typeof(TransformSystemGroup))]
public partial struct DeathSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<MainThreadMode>();
        state.RequireForUpdate<GameConfig>();
        state.RequireForUpdate<GameState>();
    }

    public void OnUpdate(ref SystemState state)
    {
        GameState game = SystemAPI.GetSingleton<GameState>();
        EntityCommandBuffer ecb = new EntityCommandBuffer(Allocator.Temp);
        float earned = 0f;
        bool anyDead = false;

        foreach (var (health, enemy, entity) in
                 SystemAPI.Query<RefRO<Health>, RefRO<EnemyData>>().WithEntityAccess())
        {
            if (health.ValueRO.Value > 0f) continue;
            earned += enemy.ValueRO.Reward;
            anyDead = true;
            ecb.DestroyEntity(entity);
        }

        if (anyDead)
        {
            ecb.Playback(state.EntityManager);
            game.Money += earned;
            SystemAPI.SetSingleton(game);
        }
        ecb.Dispose();
    }
}
