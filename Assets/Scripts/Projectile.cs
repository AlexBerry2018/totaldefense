using System.Collections.Generic;
using UnityEngine;

public class Projectile : MonoBehaviour
{
    public static int Count;

    Enemy target;
    Vector3 targetPos;
    float damage;
    float splashRadius;
    float speed;

    void OnEnable() => Count++;
    void OnDisable() => Count--;

    public void Init(Enemy target, float damage, float splashRadius, float speed)
    {
        this.target = target;
        this.damage = damage;
        this.splashRadius = splashRadius;
        this.speed = speed;
        targetPos = target.transform.position;
    }

    void Update()
    {
        if (target != null && target.health > 0f) targetPos = target.transform.position;

        Vector3 pos = Vector3.MoveTowards(transform.position, targetPos, speed * Time.deltaTime);
        transform.position = pos;

        if ((pos - targetPos).sqrMagnitude < 0.01f) Hit();
    }

    void Hit()
    {
        if (splashRadius > 0f)
        {
            float r2 = splashRadius * splashRadius;
            List<Enemy> enemies = Enemy.All;
            for (int i = 0; i < enemies.Count; i++)
            {
                Enemy e = enemies[i];
                if ((e.transform.position - targetPos).sqrMagnitude <= r2) e.TakeDamage(damage);
            }
        }
        else if (target != null && target.health > 0f)
        {
            target.TakeDamage(damage);
        }

        Destroy(gameObject);
    }
}
