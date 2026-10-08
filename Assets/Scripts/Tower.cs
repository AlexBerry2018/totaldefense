using System.Collections.Generic;
using UnityEngine;

public class Tower : MonoBehaviour
{
    public static readonly List<Tower> All = new List<Tower>();

    public float range = 8f;
    public float damage = 5f;
    public float cooldown = 0.2f;
    public float splashRadius = 0f;
    public float projectileSpeed = 30f;

    float timer;

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    void Update()
    {
        timer -= Time.deltaTime;

        Enemy target = FindNearest();
        if (target == null || timer > 0f) return;

        timer = cooldown;
        GameManager.Instance.SpawnProjectile(
            transform.position + Vector3.up, target, damage, splashRadius, projectileSpeed);
    }

    Enemy FindNearest()
    {
        Vector3 pos = transform.position;
        float best = range * range;
        Enemy result = null;

        List<Enemy> enemies = Enemy.All;
        for (int i = 0; i < enemies.Count; i++)
        {
            Enemy e = enemies[i];
            if (e.health <= 0f) continue;
            Vector3 d = e.transform.position - pos;
            d.y = 0f;
            float sq = d.sqrMagnitude;
            if (sq < best)
            {
                best = sq;
                result = e;
            }
        }
        return result;
    }
}
