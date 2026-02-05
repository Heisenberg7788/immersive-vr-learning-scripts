using UnityEngine;

[DisallowMultipleComponent]
public class WireRootOnReel : MonoBehaviour
{
    public enum AxleAxis { X, Y, Z }

    [Header("Reel Geometry (World Space)")]
    public Transform reelRoot;
    public AxleAxis axle = AxleAxis.Z;
    public float hubRadius = 0.10f;
    public Vector3 localRootOffset = Vector3.zero;

    [Header("Root Target")]
    public Transform rootTarget;

    [Header("Orientation")]
    public bool alignOutward = true;

    void Reset()
    {
        reelRoot = transform.parent ? transform.parent : transform;
        rootTarget = transform;
    }

    void LateUpdate()
    {
        if (!reelRoot) return;

        Vector3 axisWS = AxisDirWS();
        Vector3 centerWS = reelRoot.position;
        Transform t = rootTarget ? rootTarget : transform;

        Vector3 p = t.position;
        Vector3 v = p - centerWS;
        Vector3 axial = Vector3.Project(v, axisWS);
        Vector3 radial = v - axial;

        if (radial.sqrMagnitude < 1e-10f) radial = OrthoRight(axisWS) * hubRadius;
        else radial = radial.normalized * hubRadius;

        Vector3 hubPointWS = centerWS + axial + radial + reelRoot.TransformVector(localRootOffset);

        t.position = hubPointWS;

        if (alignOutward)
        {
            Vector3 outward = radial.normalized;
            if (outward.sqrMagnitude > 1e-8f)
            {
                Quaternion look = Quaternion.LookRotation(outward, axisWS);
                t.rotation = look;
            }
        }
    }

    Vector3 AxisDirWS()
    {
        Vector3 local = axle switch
        {
            AxleAxis.X => Vector3.right,
            AxleAxis.Y => Vector3.up,
            _ => Vector3.forward,
        };
        return (reelRoot ? reelRoot.TransformDirection(local) : local).normalized;
    }

    static Vector3 OrthoRight(Vector3 n)
    {
        Vector3 a = Mathf.Abs(n.y) < 0.99f ? Vector3.up : Vector3.right;
        Vector3 u = Vector3.Cross(n, a).normalized;
        return (u.sqrMagnitude < 1e-8f) ? Vector3.right : u;
    }
}
