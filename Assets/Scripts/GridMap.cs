using UnityEngine;

public class GridMap
{
    public const int Size = 128;
    public const float CellSize = 1f;

    const int CellCount = Size * Size;
    const float Unreachable = float.MaxValue;

    public readonly bool[] Blocked = new bool[CellCount];

    readonly float[] dist = new float[CellCount];
    readonly int[] anchor = new int[CellCount];
    readonly Vector2[] exitDir = new Vector2[CellCount];
    readonly int[] losTested = new int[CellCount];
    readonly bool[] losResult = new bool[CellCount];

    float[] heapKeys = new float[CellCount * 2];
    int[] heapCells = new int[CellCount * 2];
    int heapCount;

    static readonly int[] StepX = { 1, -1, 0, 0, 1, 1, -1, -1 };
    static readonly int[] StepY = { 0, 0, 1, -1, 1, -1, 1, -1 };

    public int[] Anchors => anchor;
    public Vector2[] ExitDirs => exitDir;

    public static Vector2Int BaseCell => new Vector2Int(Size / 2, Size / 2);

    public static int Index(int x, int y) => y * Size + x;

    public static bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Size && y < Size;

    public static Vector2Int WorldToCell(Vector3 p)
    {
        int x = Mathf.FloorToInt(p.x / CellSize + Size * 0.5f);
        int y = Mathf.FloorToInt(p.z / CellSize + Size * 0.5f);
        return new Vector2Int(Mathf.Clamp(x, 0, Size - 1), Mathf.Clamp(y, 0, Size - 1));
    }

    public static Vector3 CellToWorld(Vector2Int c)
    {
        return new Vector3((c.x - Size * 0.5f + 0.5f) * CellSize, 0f, (c.y - Size * 0.5f + 0.5f) * CellSize);
    }

    public Vector3 Direction(Vector3 worldPos)
    {
        Vector2Int c = WorldToCell(worldPos);
        int i = Index(c.x, c.y);

        if (Blocked[i])
        {
            Vector2 e = exitDir[i];
            return new Vector3(e.x, 0f, e.y);
        }

        int a = anchor[i];
        Vector3 target = CellToWorld(new Vector2Int(a % Size, a / Size));
        Vector3 to = target - worldPos;
        to.y = 0f;
        float len = to.magnitude;
        return len > 0.001f ? to / len : Vector3.zero;
    }

    public bool CanBuild(Vector2Int c)
    {
        if (c.x <= 0 || c.y <= 0 || c.x >= Size - 1 || c.y >= Size - 1) return false;
        if (Blocked[Index(c.x, c.y)]) return false;
        Vector2Int b = BaseCell;
        if (Mathf.Abs(c.x - b.x) <= 2 && Mathf.Abs(c.y - b.y) <= 2) return false;
        return true;
    }

    public bool TryBlock(Vector2Int c)
    {
        if (!CanBuild(c)) return false;
        int i = Index(c.x, c.y);
        Blocked[i] = true;
        if (Rebuild()) return true;

        Blocked[i] = false;
        Rebuild();
        return false;
    }

    public bool Rebuild()
    {
        Vector2Int b = BaseCell;
        int baseIndex = Index(b.x, b.y);

        int blockedCount = 0;
        for (int i = 0; i < CellCount; i++)
        {
            dist[i] = Unreachable;
            anchor[i] = baseIndex;
            losTested[i] = -1;
            exitDir[i] = Vector2.zero;
            if (Blocked[i]) blockedCount++;
        }

        if (blockedCount == 0)
        {
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float dx = x - b.x, dy = y - b.y;
                    dist[Index(x, y)] = Mathf.Sqrt(dx * dx + dy * dy);
                }
            }
            return true;
        }

        heapCount = 0;
        dist[baseIndex] = 0f;
        HeapPush(0f, baseIndex);

        while (heapCount > 0)
        {
            HeapPop(out float key, out int cur);
            if (key > dist[cur]) continue;

            int cx = cur % Size, cy = cur / Size;
            int a = anchor[cur];
            int ax = a % Size, ay = a / Size;

            for (int n = 0; n < 8; n++)
            {
                int nx = cx + StepX[n], ny = cy + StepY[n];
                if (!InBounds(nx, ny)) continue;
                int ni = Index(nx, ny);
                if (Blocked[ni]) continue;
                if (n >= 4 && (Blocked[Index(nx, cy)] || Blocked[Index(cx, ny)])) continue;

                bool visible;
                if (losTested[ni] == a)
                {
                    visible = losResult[ni];
                }
                else
                {
                    visible = HasLineOfSight(nx, ny, ax, ay);
                    losTested[ni] = a;
                    losResult[ni] = visible;
                }

                float candidate;
                int candidateAnchor;
                if (visible)
                {
                    float dx = nx - ax, dy = ny - ay;
                    candidate = dist[a] + Mathf.Sqrt(dx * dx + dy * dy);
                    candidateAnchor = a;
                }
                else
                {
                    candidate = dist[cur] + (n >= 4 ? 1.41421356f : 1f);
                    candidateAnchor = cur;
                }

                if (candidate < dist[ni] - 0.0001f)
                {
                    dist[ni] = candidate;
                    anchor[ni] = candidateAnchor;
                    HeapPush(candidate, ni);
                }
            }
        }

        bool allReachable = true;
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                int i = Index(x, y);
                if (!Blocked[i])
                {
                    if (dist[i] == Unreachable) allReachable = false;
                    continue;
                }

                float best = Unreachable;
                Vector2 dir = Vector2.zero;
                for (int n = 0; n < 8; n++)
                {
                    int nx = x + StepX[n], ny = y + StepY[n];
                    if (!InBounds(nx, ny)) continue;
                    float d = dist[Index(nx, ny)];
                    if (d >= best) continue;
                    best = d;
                    dir = new Vector2(StepX[n], StepY[n]).normalized;
                }
                exitDir[i] = dir;
            }
        }
        return allReachable;
    }

    bool HasLineOfSight(int x0, int y0, int x1, int y1)
    {
        if (x0 == x1 && y0 == y1) return true;

        float fx = x0 + 0.5f, fy = y0 + 0.5f;
        float dx = x1 - x0, dy = y1 - y0;
        float length = Mathf.Sqrt(dx * dx + dy * dy);
        int steps = Mathf.CeilToInt(length * 4f);
        float sx = dx / steps, sy = dy / steps;

        int px = x0, py = y0;
        for (int s = 1; s <= steps; s++)
        {
            int cx = (int)(fx + sx * s);
            int cy = (int)(fy + sy * s);
            if (cx == px && cy == py) continue;

            if (Blocked[Index(cx, cy)]) return false;
            if (cx != px && cy != py && (Blocked[Index(cx, py)] || Blocked[Index(px, cy)])) return false;

            px = cx;
            py = cy;
        }
        return true;
    }

    void HeapPush(float key, int cell)
    {
        if (heapCount == heapKeys.Length)
        {
            System.Array.Resize(ref heapKeys, heapCount * 2);
            System.Array.Resize(ref heapCells, heapCount * 2);
        }

        int i = heapCount++;
        while (i > 0)
        {
            int parent = (i - 1) / 2;
            if (heapKeys[parent] <= key) break;
            heapKeys[i] = heapKeys[parent];
            heapCells[i] = heapCells[parent];
            i = parent;
        }
        heapKeys[i] = key;
        heapCells[i] = cell;
    }

    void HeapPop(out float key, out int cell)
    {
        key = heapKeys[0];
        cell = heapCells[0];

        heapCount--;
        if (heapCount == 0) return;

        float lastKey = heapKeys[heapCount];
        int lastCell = heapCells[heapCount];
        int i = 0;
        while (true)
        {
            int child = i * 2 + 1;
            if (child >= heapCount) break;
            if (child + 1 < heapCount && heapKeys[child + 1] < heapKeys[child]) child++;
            if (heapKeys[child] >= lastKey) break;
            heapKeys[i] = heapKeys[child];
            heapCells[i] = heapCells[child];
            i = child;
        }
        heapKeys[i] = lastKey;
        heapCells[i] = lastCell;
    }
}
