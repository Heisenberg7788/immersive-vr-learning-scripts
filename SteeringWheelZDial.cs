using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

[RequireComponent(typeof(XRGrabInteractable))]
public class SteeringWheelZDial : MonoBehaviour
{
    public enum Axis { X, Y, Z }

    [Header("Wheel Axis")]
    public Axis wheelAxis = Axis.Z;   // You said Z rotates

    [Header("Angle Limits")]
    public bool clampAngle = true;
    public float minAngle = -540f;
    public float maxAngle = 540f;

    [Header("Direction")]
    public bool invert = false;

    [Header("Smoothing")]
    [Tooltip("Higher = snappier, Lower = smoother")]
    public float smoothSpeed = 16f;

    [Header("Lock Position")]
    public bool lockWorldPosition = true;

    // -----------------------------
    // INTERNAL STATE
    // -----------------------------

    XRGrabInteractable grab;
    Rigidbody rb;

    Vector3 lockedWorldPos;
    Quaternion baseLocalRotation;

    bool isHeld;
    float currentAngle;     // <- THIS is what drives everything
    float targetAngle;
    float angleVelocity;

    Vector3 prevHandDirOnPlane;

    // =============================
    // UNITY LIFECYCLE
    // =============================

    void Awake()
    {
        grab = GetComponent<XRGrabInteractable>();
        rb = GetComponent<Rigidbody>();

        lockedWorldPos = transform.position;
        baseLocalRotation = transform.localRotation;

        // XR MUST NOT MOVE POSITION OR ROTATION
        grab.trackPosition = false;
        grab.trackRotation = false;
        grab.movementType = XRBaseInteractable.MovementType.Kinematic;

        grab.selectEntered.AddListener(OnGrab);
        grab.selectExited.AddListener(OnRelease);

        if (rb != null)
        {
            rb.useGravity = false;
            rb.isKinematic = true;
        }
    }

    void OnDestroy()
    {
        grab.selectEntered.RemoveListener(OnGrab);
        grab.selectExited.RemoveListener(OnRelease);
    }

    void LateUpdate()
    {
        // HARD POSITION LOCK (no floating ever)
        if (lockWorldPosition)
            transform.position = lockedWorldPos;

        // Smooth angle
        currentAngle = Mathf.SmoothDampAngle(
            currentAngle,
            targetAngle,
            ref angleVelocity,
            1f / Mathf.Max(0.001f, smoothSpeed)
        );

        // Apply ONLY Z (or chosen axis)
        transform.localRotation = baseLocalRotation * AxisRotation(currentAngle);
    }

    // =============================
    // GRAB LOGIC
    // =============================

    void OnGrab(SelectEnterEventArgs args)
    {
        isHeld = true;
        angleVelocity = 0f;
        baseLocalRotation = transform.localRotation;

        Vector3 handPos = args.interactorObject.transform.position;
        prevHandDirOnPlane = GetHandDirectionOnPlane(handPos);
    }

    void OnRelease(SelectExitEventArgs args)
    {
        isHeld = false;
        angleVelocity = 0f;
    }

    void Update()
    {
        if (!isHeld || grab.interactorsSelecting.Count == 0)
            return;

        Transform hand = grab.interactorsSelecting[0].transform;
        Vector3 newDir = GetHandDirectionOnPlane(hand.position);

        if (newDir.sqrMagnitude < 0.0001f || prevHandDirOnPlane.sqrMagnitude < 0.0001f)
            return;

        float delta = Vector3.SignedAngle(prevHandDirOnPlane, newDir, AxisWorld());
        if (invert) delta = -delta;

        targetAngle += delta;

        if (clampAngle)
            targetAngle = Mathf.Clamp(targetAngle, minAngle, maxAngle);

        prevHandDirOnPlane = newDir;
    }

    // =============================
    // PUBLIC API (IMPORTANT)
    // =============================

    /// <summary>
    /// This is what your HEIGHT / LIFT script reads.
    /// </summary>
    public float GetAngle()
    {
        return currentAngle;
    }

    // =============================
    // MATH HELPERS
    // =============================

    Vector3 AxisWorld()
    {
        return wheelAxis == Axis.X ? transform.right :
               wheelAxis == Axis.Y ? transform.up :
               transform.forward;
    }

    Quaternion AxisRotation(float angle)
    {
        return wheelAxis == Axis.X ? Quaternion.Euler(angle, 0f, 0f) :
               wheelAxis == Axis.Y ? Quaternion.Euler(0f, angle, 0f) :
               Quaternion.Euler(0f, 0f, angle);
    }

    Vector3 GetHandDirectionOnPlane(Vector3 handWorldPos)
    {
        Vector3 axis = AxisWorld();
        Vector3 v = handWorldPos - transform.position;

        Vector3 onPlane = Vector3.ProjectOnPlane(v, axis);
        if (onPlane.sqrMagnitude < 0.00001f)
            return Vector3.zero;

        return onPlane.normalized;
    }
}
