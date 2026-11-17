using UnityEngine;

public class Billboard : MonoBehaviour
{
    private Camera mainCamera;

    void Start()
    {
        mainCamera = Camera.main;
    }

    void LateUpdate()
    {
        if (mainCamera == null) return;

        // 항상 메인 카메라를 바라보도록 회전
        transform.LookAt(mainCamera.transform);
    }
}
