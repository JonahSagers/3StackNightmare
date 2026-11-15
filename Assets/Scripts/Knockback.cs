using System.Collections;
using UnityEngine;

// Added automatically when something gets knocked back. Slides the object along the ground,
// fast at first and easing to a stop.
public class Knockback : MonoBehaviour
{
    Coroutine running;
    Collider ground;

    public void Push(Vector3 direction, float distance, float duration)
    {
        if (running != null) StopCoroutine(running);
        running = StartCoroutine(Slide(direction, distance, duration));
    }

    IEnumerator Slide(Vector3 direction, float distance, float duration)
    {
        if (ground == null)
        {
            GameObject g = GameObject.Find("Ground");
            if (g != null) ground = g.GetComponent<Collider>();
        }

        Vector3 start = transform.position;
        for (float t = 0f; t < 1f; t += Time.deltaTime / duration)
        {
            float eased = 1f - (1f - t) * (1f - t); // ease-out: quick shove, soft stop
            transform.position = KeepOnGround(start + direction * distance * eased);
            yield return null;
        }
        transform.position = KeepOnGround(start + direction * distance);
        running = null;
    }

    Vector3 KeepOnGround(Vector3 pos)
    {
        if (Lane.Active != null) pos = Lane.Active.Clamp(pos); // can't be knocked through lane walls
        if (ground == null) return pos;
        Bounds b = ground.bounds;
        pos.x = Mathf.Clamp(pos.x, b.min.x + 0.5f, b.max.x - 0.5f);
        pos.z = Mathf.Clamp(pos.z, b.min.z + 0.5f, b.max.z - 0.5f);
        return pos;
    }
}
