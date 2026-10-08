using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace ECS
{
    public struct GameEntity : IComponentData
    {
    }

    public struct MainThreadMode : IComponentData
    {
    }

    public struct JobsMode : IComponentData
    {
    }

    public struct EnemyData : IComponentData
    {
        public float Speed;
        public float Reward;
    }

    public struct Health : IComponentData
    {
        public float Value;
    }

    public struct TowerData : IComponentData
    {
        public float Range;
        public float Damage;
        public float Cooldown;
        public float SplashRadius;
        public float ProjectileSpeed;
        public float Timer;
    }

    public struct ProjectileData : IComponentData
    {
        public Entity Target;
        public float3 TargetPos;
        public bool TargetAlive;
        public float Damage;
        public float SplashRadius;
        public float Speed;
    }

    public struct GameConfig : IComponentData
    {
        public Entity EnemyPrefab;
        public Entity FastEnemyPrefab;
        public Entity ProjectilePrefab;
        public float3 BasePosition;
        public float WavePause;
        public float SpawnDuration;
        public float FastEnemyShare;
    }

    public struct GameState : IComponentData
    {
        public float Money;
        public int BaseHealth;
        public int Wave;
        public int ToSpawn;
        public float SpawnAccumulator;
        public float WaveTimer;
        public int StressRequest;
        public bool SkipWaveRequest;
        public bool GameOver;
        public bool Victory;
    }

    public struct FlowField : IComponentData
    {
        public NativeArray<int> Anchor;
        public NativeArray<byte> Blocked;
        public NativeArray<float2> ExitDir;

        public float2 Direction(float2 position)
        {
            return FlowMath.Direction(Anchor, Blocked, ExitDir, position);
        }
    }

    public static class FlowMath
    {
        public static float2 Direction(in NativeArray<int> anchor, in NativeArray<byte> blocked,
            in NativeArray<float2> exitDir, float2 position)
        {
            const int size = GridMap.Size;
            int x = math.clamp((int)math.floor(position.x + size * 0.5f), 0, size - 1);
            int y = math.clamp((int)math.floor(position.y + size * 0.5f), 0, size - 1);
            int i = y * size + x;

            if (blocked[i] != 0) return exitDir[i];

            int a = anchor[i];
            float2 target = new float2(a % size - size * 0.5f + 0.5f, a / size - size * 0.5f + 0.5f);
            float2 to = target - position;
            float len = math.length(to);
            return len > 0.001f ? to / len : float2.zero;
        }
    }

    public static class SpatialGrid
    {
        public const float CellSize = 4f;
        public const int Dim = 32;

        public static int Coord(float value)
        {
            return math.clamp((int)math.floor((value + GridMap.Size * 0.5f) / CellSize), 0, Dim - 1);
        }

        public static int Key(int cx, int cy)
        {
            return cy * Dim + cx;
        }
    }

    public static class WaveTable
    {
        public static readonly int[] Sizes = { 1000, 2000, 3000, 5000, 8000, 12000, 18000, 25000, 35000, 50000 };
        public static readonly int[] Bounty = { 1000, 2000, 3000, 3500, 3500, 3500, 3500, 4000, 4000, 4000 };
        public static readonly float[] HealthMultiplier = { 1f, 1f, 1.2f, 1.5f, 2f, 2.5f, 3f, 4f, 5f, 6f };

        public const int Count = 10;
    }
}