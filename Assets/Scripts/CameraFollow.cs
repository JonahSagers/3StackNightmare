using UnityEngine;

// Attach to the Main Camera. Drag the Player into the "Target" slot.
public class CameraFollow : MonoBehaviour
{
    [Tooltip("The object the camera follows (your Player).")]
    public Transform target;

    [Tooltip("Where the camera sits relative to the target.")]
    public Vector3 offset = new Vector3(0f, 3f, -6f);

    [Tooltip("Higher = snappier. 0 = lock instantly with no smoothing.")]
    public float smoothSpeed = 10f;

    void LateUpdate()
    {
        if (target == null) return;

        Vector3 desired = target.position + offset;
        transform.position = smoothSpeed <= 0f
            ? desired
            : Vector3.Lerp(transform.position, desired, smoothSpeed * Time.deltaTime);

        // Aim at roughly the player's chest instead of their feet.
        transform.LookAt(target.position + Vector3.up * 1f);
    }
}
