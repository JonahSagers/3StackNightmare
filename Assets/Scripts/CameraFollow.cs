using UnityEngine;

// Attach to the Main Camera. Drag the Player into the "Target" slot.
// The camera follows the player's position, but its rotation stays fixed,
// so it slides along with the player instead of turning to face it.
public class CameraFollow : MonoBehaviour
{
    [Tooltip("The object the camera follows (your Player).")]
    public Transform target;

    [Tooltip("Where the camera sits relative to the target.")]
    public Vector3 offset = new Vector3(0f, 12f, -8f);

    [Tooltip("Higher = snappier. 0 = lock instantly with no smoothing.")]
    public float smoothSpeed = 10f;

    [Tooltip("On: aim at the player once when the game starts, then never rotate again.\nOff: keep whatever rotation the camera has in the scene.")]
    public bool aimAtTargetOnStart = true;

    void Start()
    {
        if (target == null) return;

        // Snap into place so there's no swoop on the first frame.
        transform.position = target.position + offset;

        if (aimAtTargetOnStart)
            transform.LookAt(target.position + Vector3.up * 1f);
    }

    void LateUpdate()
    {
        if (target == null) return;

        // Only the position changes. Rotation is never touched here.
        Vector3 desired = target.position + offset;
        transform.position = smoothSpeed <= 0f
            ? desired
            : Vector3.Lerp(transform.position, desired, smoothSpeed * Time.deltaTime);
    }
}
