using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

[RequireComponent(typeof(Rigidbody))]
public class Screw : MonoBehaviour
{
    [Header("Thread Params")]
    [Tooltip("How much the screw moves per full 360° turn.")]
    public float threadPitchMetersPerTurn = 0.002f;

    [Tooltip("Maximum distance the screw can travel into the wood.")]
    public float maxTravelMeters = 0.03f;

    [Tooltip("+1 = clockwise tightens, -1 = counterclockwise tightens")]
    public int tightenDirection = +1;

    [Header("References")]
    public XRGrabInteractable grab;

    [Tooltip("Tip transform (child). If empty, will search by name.")]
    public Transform tip;

    [Tooltip("Head transform (child). Optional, used only for alignment cues.")]
    public Transform head;

    [Header("Model Axis (IMPORTANT)")]
    [Tooltip("Which LOCAL axis of the screw points from head -> tip (along the shaft). Example: (0,1,0) means local Y.")]
    public Vector3 screwAxisLocal = new Vector3(0, 1, 0);

    Rigidbody rb;

    public Rigidbody RB => rb;

    public Vector3 ShaftAxisWorld
    {
        get
        {
            Vector3 a = transform.TransformDirection(screwAxisLocal);
            return a.sqrMagnitude < 1e-6f ? transform.up : a.normalized;
        }
    }

    public Transform Tip => tip != null ? tip : transform;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        if (grab == null) grab = GetComponent<XRGrabInteractable>();

        // Auto-find common children if not assigned
        if (tip == null)
        {
            var t = transform.Find("Tip");
            if (t != null) tip = t;
        }

        if (head == null)
        {
            var h = transform.Find("Head");
            if (h != null) head = h;
        }
    }

    public void SetKinematic(bool value)
    {
        if (rb != null) rb.isKinematic = value;
    }

    public void DisableGrab(bool disable)
    {
        if (grab != null) grab.enabled = !disable;
    }
}
