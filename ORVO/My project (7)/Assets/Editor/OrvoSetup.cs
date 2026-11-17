// Orvo 주방 화면을 위한 프로젝트 설정(에디터 전용). Assets/Editor/ 폴더에 둔다.
//
// 백엔드는 http:// 주소라서, Unity가 기본으로 막는 "HTTP 내려받기"를 허용해 준다.
// 창이 뒤로 가도 화면이 계속 바뀌게 "Run In Background"도 켠다(발표 때 다른 창을 눌러도 멈추지 않게).
// Unity를 열 때마다 한 번 확인하고, 이미 맞게 돼 있으면 아무것도 하지 않는다.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class OrvoSetup
{
    static OrvoSetup()
    {
        if (PlayerSettings.insecureHttpOption != InsecureHttpOption.AlwaysAllowed)
        {
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
            Debug.Log("[Orvo] Allow downloads over HTTP를 Always allowed로 바꿨어요(백엔드가 http 주소라서).");
        }
        if (!PlayerSettings.runInBackground)
        {
            PlayerSettings.runInBackground = true;
            Debug.Log("[Orvo] Run In Background를 켰어요(창이 뒤로 가도 화면이 계속 바뀌게).");
        }
    }
}
#endif
