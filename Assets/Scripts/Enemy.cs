using System.Collections.Generic;
using UnityEngine;

public class Enemy : MonoBehaviour
{
    public static readonly List<Enemy> All = new List<Enemy>(65536);

    public float speed = 3f;
    public float health = 10f;
    public float reward = 1f;

    int index = -1;

    void OnEnable()
    {
        index = All.Count;
        All.Add(this);
    }

    void OnDisable()
    {
        if (index < 0 || index >= All.Count) return;
        int last = All.Count - 1;
        Enemy moved = All[last];
        All[index] = moved;
        moved.index = index;
        All.RemoveAt(last);
        index = -1;
    }

    void Update()
    {
        GameManager gm = GameManager.Instance;
        Vector3 pos = transform.position;

        Vector3 toBase = gm.BasePosition - pos;
        toBase.y = 0f;
        if (toBase.sqrMagnitude < 1.5f * 1.5f)
        {
            gm.DamageBase(1);
            health = 0f;
            Destroy(gameObject);
            return;
        }

        transform.position = pos + gm.Grid.Direction(pos) * (speed * Time.deltaTime);
    }

    public void TakeDamage(float amount)
    {
        if (health <= 0f) return;
        health -= amount;
        if (health <= 0f)
        {
            GameManager.Instance.AddMoney(reward);
            Destroy(gameObject);
        }
    }
}
