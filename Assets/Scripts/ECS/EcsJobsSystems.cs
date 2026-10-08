using ECS;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(WaveSpawnSystem))]
[UpdateBefore(typeof(TransformSystemGroup))]
public partial struct JobsEnemyMoveSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<JobsMode>();
        state.RequireForUpdate<GameConfig>();
        state.RequireForUpdate<GameState>();
        state.RequireForUpdate<FlowField>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        GameConfig config = SystemAPI.GetSingleton<GameConfig>();
        GameState game = SystemAPI.GetSingleton<GameState>();
        FlowField field = SystemAPI.GetSingleton<FlowField>();
        if (game.GameOver) return;

        new EnemyMoveJob
        {
            Anchor = field.Anchor,
            Blocked = field.Blocked,
            ExitDir = field.ExitDir,
            BasePosition = config.BasePosition.xz,
            DeltaTime = SystemAPI.Time.DeltaTime
        }.ScheduleParallel();
    }
}

[BurstCompile]
public partial struct EnemyMoveJob : IJobEntity
{
    [ReadOnly] public NativeArray<int> Anchor;
    [ReadOnly] public NativeArray<byte> Blocked;
    [ReadOnly] public NativeArray<float2> ExitDir;
    public float2 BasePosition;
    public float DeltaTime;

    void Execute(ref LocalTransform transform, ref Health health, ref EnemyData enemy)
    {
        if (health.Value <= 0f) return;

        float3 position = transform.Position;
        float2 flat = position.xz;

        if (math.lengthsq(BasePosition - flat) < 1.5f * 1.5f)
        {
            health.Value = 0f;
            enemy.Reward = -1f;
            return;
        }

        flat += FlowMath.Direction(Anchor, Blocked, ExitDir, flat) * (enemy.Speed * DeltaTime);
        transform.Position = new float3(flat.x, position.y, flat.y);
    }
}

public struct ProjectileHit
{
    public Entity Target;
    public float2 Position;
    public float RadiusSq;
    public float Damage;
    public bool TargetAlive;
}

[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(JobsEnemyMoveSystem))]
[UpdateBefore(typeof(TransformSystemGroup))]
public partial struct JobsCombatSystem : ISystem
{
    EntityQuery enemyQuery;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        enemyQuery = SystemAPI.QueryBuilder().WithAll<EnemyData, Health, LocalTransform>().Build();
        state.RequireForUpdate<JobsMode>();
        state.RequireForUpdate<GameConfig>();
        state.RequireForUpdate<GameState>();
        state.RequireForUpdate<EndSimulationEntityCommandBufferSystem.Singleton>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        GameConfig config = SystemAPI.GetSingleton<GameConfig>();
        GameState game = SystemAPI.GetSingleton<GameState>();
        if (game.GameOver) return;

        float dt = SystemAPI.Time.DeltaTime;

        NativeArray<Entity> enemies = enemyQuery.ToEntityArray(Allocator.TempJob);
        NativeArray<LocalTransform> enemyTransforms =
            enemyQuery.ToComponentDataArray<LocalTransform>(Allocator.TempJob);
        NativeArray<Health> enemyHealth = enemyQuery.ToComponentDataArray<Health>(Allocator.TempJob);

        NativeParallelMultiHashMap<int, int> grid =
            new NativeParallelMultiHashMap<int, int>(math.max(enemies.Length, 1), Allocator.TempJob);
        NativeQueue<ProjectileHit> hits = new NativeQueue<ProjectileHit>(Allocator.TempJob);

        var ecbSingleton = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>();
        EntityCommandBuffer spawnBuffer = ecbSingleton.CreateCommandBuffer(state.WorldUnmanaged);
        EntityCommandBuffer destroyBuffer = ecbSingleton.CreateCommandBuffer(state.WorldUnmanaged);

        JobHandle handle = new BuildGridJob
        {
            Transforms = enemyTransforms,
            Healths = enemyHealth,
            Grid = grid
        }.Schedule(state.Dependency);

        handle = new TowerShootJob
        {
            Grid = grid,
            Enemies = enemies,
            Transforms = enemyTransforms,
            ProjectilePrefab = config.ProjectilePrefab,
            DeltaTime = dt,
            Ecb = spawnBuffer.AsParallelWriter()
        }.ScheduleParallel(handle);

        handle = new ProjectileAimJob
        {
            TransformLookup = SystemAPI.GetComponentLookup<LocalTransform>(true),
            HealthLookup = SystemAPI.GetComponentLookup<Health>(true)
        }.ScheduleParallel(handle);

        handle = new ProjectileMoveJob
        {
            DeltaTime = dt,
            Hits = hits.AsParallelWriter(),
            Ecb = destroyBuffer.AsParallelWriter()
        }.ScheduleParallel(handle);

        handle = new ApplyHitsJob
        {
            Hits = hits,
            Grid = grid,
            Enemies = enemies,
            Transforms = enemyTransforms,
            HealthLookup = SystemAPI.GetComponentLookup<Health>(false)
        }.Schedule(handle);

        state.Dependency = handle;

        enemies.Dispose(handle);
        enemyTransforms.Dispose(handle);
        enemyHealth.Dispose(handle);
        grid.Dispose(handle);
        hits.Dispose(handle);
    }
}

[BurstCompile]
public struct BuildGridJob : IJob
{
    [ReadOnly] public NativeArray<LocalTransform> Transforms;
    [ReadOnly] public NativeArray<Health> Healths;
    public NativeParallelMultiHashMap<int, int> Grid;

    public void Execute()
    {
        for (int i = 0; i < Transforms.Length; i++)
        {
            if (Healths[i].Value <= 0f) continue;
            float3 position = Transforms[i].Position;
            Grid.Add(SpatialGrid.Key(SpatialGrid.Coord(position.x), SpatialGrid.Coord(position.z)), i);
        }
    }
}

[BurstCompile]
public partial struct TowerShootJob : IJobEntity
{
    [ReadOnly] public NativeParallelMultiHashMap<int, int> Grid;
    [ReadOnly] public NativeArray<Entity> Enemies;
    [ReadOnly] public NativeArray<LocalTransform> Transforms;
    public Entity ProjectilePrefab;
    public float DeltaTime;
    public EntityCommandBuffer.ParallelWriter Ecb;

    void Execute([ChunkIndexInQuery] int sortKey, ref TowerData tower, in LocalTransform transform)
    {
        tower.Timer -= DeltaTime;
        if (tower.Timer > 0f) return;

        float3 towerPosition = transform.Position;
        float bestDistance = tower.Range * tower.Range;
        int best = -1;

        int minX = SpatialGrid.Coord(towerPosition.x - tower.Range);
        int maxX = SpatialGrid.Coord(towerPosition.x + tower.Range);
        int minY = SpatialGrid.Coord(towerPosition.z - tower.Range);
        int maxY = SpatialGrid.Coord(towerPosition.z + tower.Range);

        for (int cy = minY; cy <= maxY; cy++)
        {
            for (int cx = minX; cx <= maxX; cx++)
            {
                if (!Grid.TryGetFirstValue(SpatialGrid.Key(cx, cy), out int index, out var iterator)) continue;
                do
                {
                    float distance = math.lengthsq(Transforms[index].Position.xz - towerPosition.xz);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = index;
                    }
                } while (Grid.TryGetNextValue(out index, ref iterator));
            }
        }

        if (best < 0) return;

        tower.Timer = tower.Cooldown;

        Entity projectile = Ecb.Instantiate(sortKey, ProjectilePrefab);
        float3 start = towerPosition + new float3(0f, 1f, 0f);
        Ecb.SetComponent(sortKey, projectile,
            LocalTransform.FromPositionRotationScale(start, quaternion.identity, 0.3f));
        Ecb.SetComponent(sortKey, projectile, new ProjectileData
        {
            Target = Enemies[best],
            TargetPos = Transforms[best].Position,
            TargetAlive = true,
            Damage = tower.Damage,
            SplashRadius = tower.SplashRadius,
            Speed = tower.ProjectileSpeed
        });
    }
}

[BurstCompile]
public partial struct ProjectileAimJob : IJobEntity
{
    [ReadOnly] public ComponentLookup<LocalTransform> TransformLookup;
    [ReadOnly] public ComponentLookup<Health> HealthLookup;

    void Execute(ref ProjectileData projectile)
    {
        bool alive = HealthLookup.TryGetComponent(projectile.Target, out Health health) && health.Value > 0f;
        projectile.TargetAlive = alive;
        if (alive && TransformLookup.TryGetComponent(projectile.Target, out LocalTransform target))
        {
            projectile.TargetPos = target.Position;
        }
    }
}

[BurstCompile]
public partial struct ProjectileMoveJob : IJobEntity
{
    public float DeltaTime;
    public NativeQueue<ProjectileHit>.ParallelWriter Hits;
    public EntityCommandBuffer.ParallelWriter Ecb;

    void Execute(Entity entity, [ChunkIndexInQuery] int sortKey, ref LocalTransform transform,
        in ProjectileData projectile)
    {
        if (DeltaTime <= 0f) return;

        float3 position = transform.Position;
        float3 to = projectile.TargetPos - position;
        float distance = math.length(to);
        float step = projectile.Speed * DeltaTime;

        if (distance > step + 0.1f)
        {
            transform.Position = position + to / distance * step;
            return;
        }

        Hits.Enqueue(new ProjectileHit
        {
            Target = projectile.Target,
            Position = projectile.TargetPos.xz,
            RadiusSq = projectile.SplashRadius * projectile.SplashRadius,
            Damage = projectile.Damage,
            TargetAlive = projectile.TargetAlive
        });
        Ecb.DestroyEntity(sortKey, entity);
    }
}

[BurstCompile]
public struct ApplyHitsJob : IJob
{
    public NativeQueue<ProjectileHit> Hits;
    [ReadOnly] public NativeParallelMultiHashMap<int, int> Grid;
    [ReadOnly] public NativeArray<Entity> Enemies;
    [ReadOnly] public NativeArray<LocalTransform> Transforms;
    public ComponentLookup<Health> HealthLookup;

    public void Execute()
    {
        while (Hits.TryDequeue(out ProjectileHit hit))
        {
            if (hit.RadiusSq <= 0f)
            {
                if (!hit.TargetAlive) continue;
                if (!HealthLookup.TryGetComponent(hit.Target, out Health health)) continue;
                if (health.Value <= 0f) continue;
                health.Value -= hit.Damage;
                HealthLookup[hit.Target] = health;
                continue;
            }

            float radius = math.sqrt(hit.RadiusSq);
            int minX = SpatialGrid.Coord(hit.Position.x - radius);
            int maxX = SpatialGrid.Coord(hit.Position.x + radius);
            int minY = SpatialGrid.Coord(hit.Position.y - radius);
            int maxY = SpatialGrid.Coord(hit.Position.y + radius);

            for (int cy = minY; cy <= maxY; cy++)
            {
                for (int cx = minX; cx <= maxX; cx++)
                {
                    if (!Grid.TryGetFirstValue(SpatialGrid.Key(cx, cy), out int index, out var iterator)) continue;
                    do
                    {
                        if (math.lengthsq(Transforms[index].Position.xz - hit.Position) > hit.RadiusSq) continue;

                        Entity enemy = Enemies[index];
                        Health health = HealthLookup[enemy];
                        if (health.Value <= 0f) continue;
                        health.Value -= hit.Damage;
                        HealthLookup[enemy] = health;
                    } while (Grid.TryGetNextValue(out index, ref iterator));
                }
            }
        }
    }
}

[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(JobsCombatSystem))]
[UpdateBefore(typeof(TransformSystemGroup))]
public partial struct JobsDeathSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<JobsMode>();
        state.RequireForUpdate<GameConfig>();
        state.RequireForUpdate<GameState>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        GameState game = SystemAPI.GetSingleton<GameState>();
        EntityCommandBuffer ecb = new EntityCommandBuffer(Allocator.Temp);
        float earned = 0f;
        int leaked = 0;
        bool anyDead = false;

        foreach (var (health, enemy, entity) in
                 SystemAPI.Query<RefRO<Health>, RefRO<EnemyData>>().WithEntityAccess())
        {
            if (health.ValueRO.Value > 0f) continue;

            if (enemy.ValueRO.Reward < 0f) leaked++;
            else earned += enemy.ValueRO.Reward;

            anyDead = true;
            ecb.DestroyEntity(entity);
        }

        if (anyDead)
        {
            ecb.Playback(state.EntityManager);

            game.Money += earned;
            if (leaked > 0 && !game.GameOver)
            {
                game.BaseHealth -= leaked;
                if (game.BaseHealth <= 0)
                {
                    game.BaseHealth = 0;
                    game.GameOver = true;
                    game.Victory = false;
                }
            }
            SystemAPI.SetSingleton(game);
        }
        ecb.Dispose();
    }
}
