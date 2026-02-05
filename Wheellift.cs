using UnityEngine;

public class WheelLift : MonoBehaviour
{
    [Header("References")]
    public Transform wheel;          // steering wheel transform
    public Transform targetToMove;   // the thing that goes up/down (LiftSystem)

    [Header("Motion")]
    public float minY = 0.0f;        // lowest Y (world or local depending on setting)
    public float maxY = 1.0f;        // highest Y
    public float degreesForFullTravel = 360f; // how many wheel degrees for min->max
    public bool useLocalPosition = true;

    [Header("Wheel Axis")]
    public bool wheelUsesZ = true;   // you said Z changes when rotating
    public bool invert = false;

    float startWheelDeg;
    float startY;

    void Start()
    {
        if (!wheel || !targetToMove)
        {
            enabled = false;
            return;
        }

        startWheelDeg = GetWheelAngleDeg();
        startY = useLocalPosition ? targetToMove.localPosition.y : targetToMove.position.y;
    }

    void Update()
    {
        float currentDeg = GetWheelAngleDeg();
        float deltaDeg = Mathf.DeltaAngle(startWheelDeg, currentDeg); // smooth -180..180

        if (invert) deltaDeg = -deltaDeg;

        // Convert degrees -> normalized 0..1 travel
        float t = Mathf.Clamp01((deltaDeg / degreesForFullTravel) + 0.5f);

        float y = Mathf.Lerp(minY, maxY, t);

        if (useLocalPosition)
        {
            Vector3 p = targetToMove.localPosition;
            p.y = y;
            targetToMove.localPosition = p;
        }
        else
        {
            Vector3 p = targetToMove.position;
            p.y = y;
            targetToMove.position = p;
        }
    }

    float GetWheelAngleDeg()
    {
        Vector3 e = wheel.localEulerAngles;
        return wheelUsesZ ? e.z : e.y; // you said Z, but you can switch
    }
}
