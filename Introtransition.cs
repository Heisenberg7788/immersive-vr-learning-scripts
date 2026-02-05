using UnityEngine;

public class IntroTransition : MonoBehaviour
{
    public GameObject introZone;
    public GameObject mainEnvironment;
    public GameObject xrRig;
    public Transform moveToPoint;

    public Material galaxySkybox;
    public Material defaultSkybox;

    void Start()
    {
        // Start with galaxy skybox
        if (galaxySkybox != null)
        {
            RenderSettings.skybox = galaxySkybox;
        }
    }

    public void StartApp()
    {
        // Move XR rig to gameplay area
        xrRig.transform.position = moveToPoint.position;
        xrRig.transform.rotation = moveToPoint.rotation;

        // Hide intro, show main scene
        introZone.SetActive(false);
        mainEnvironment.SetActive(true);

        // Switch to normal gameplay skybox
        if (defaultSkybox != null)
        {
            RenderSettings.skybox = defaultSkybox;
        }
    }
}