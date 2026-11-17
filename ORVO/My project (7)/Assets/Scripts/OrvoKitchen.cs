// Orvo 주방 화면: 백엔드(GET /api/state)를 0.3초마다 읽어 Unity 주방에 보여 준다.
//
// 쓰는 법
//   1) 이 파일을 Unity 프로젝트의 Assets/Scripts/ 폴더에 넣는다.
//   2) Play를 누른다. 씬에 따로 붙일 필요 없다(시작할 때 스스로 만들어진다).
//   3) 화면 왼쪽 위의 "서버" 칸에 백엔드 주소를 넣고 [적용]을 누른다. 주소는 저장돼서 다음에도 남는다.
//        같은 컴퓨터에서 백엔드를 켰으면 http://127.0.0.1:8000 (또는 8001)
//
// 하는 일
//   - 기기(1·2번 가스레인지, 후드, 환풍기, 전자레인지) 위에 이름·켜짐·화력·타이머를 글자로 띄우고, 켜지면 불빛을 켠다.
//   - 냉장고 위에 온도를 띄운다(5도를 넘으면 빨간색).
//   - 타이머가 끝나면(alerts의 timer_done) 삐 소리를 낸다.
//   - 음성 명령이 들어오면(last_command.seq가 바뀌면) 들은 말과 답을 화면 아래에 잠깐 보여 준다.
//
// 백엔드 규칙: 웹소켓 없이 HTTP 폴링, 상태 JSON에는 null이 없고 시각은 long(epoch ms).
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

public class OrvoKitchen : MonoBehaviour
{
    [Header("서버")]
    [Tooltip("백엔드 주소. 실행 중 화면에서 바꾸면 저장된다.")]
    public string serverUrl = "http://127.0.0.1:8000";
    [Tooltip("상태를 읽는 간격(초)")]
    public float pollInterval = 0.3f;

    [Header("씬 오브젝트 이름 (백엔드 기기 ID → 씬의 이름)")]
    public string gas1Object = "Prop_Stove";
    public string gas2Object = "Prop_MiniStove";
    public string hoodObject = "Prop_Fan_01";
    public string fanObject = "Prop_Fan_01 (1)";
    public string microwaveObject = "Prop_Microwave_01";
    public string fridgeObject = "Prop_Fridge_01";
    [Tooltip("씬에 이미 있는 'Fridge Status : ' 글자(TextMeshPro). 없으면 건너뛴다.")]
    public string fridgeTextObject = "Text (TMP)";
    [Tooltip("씬에 떠 있는 흰 판(월드 캔버스)의 이름")]
    public string panelObject = "Canvas";
    [Tooltip("흰 판을 어떻게 할지. Keep = 그대로 두고 냉장고 온도만 넣음, Hide = 실행 중에만 숨김, "
           + "Orders = 흰 판을 숨기고 벽에 주문 모니터를 건다. 어느 쪽이든 씬 파일은 바뀌지 않는다.")]
    public PanelMode panelMode = PanelMode.Orders;

    public enum PanelMode { Keep, Hide, Orders }

    [Header("주문 모니터 (panelMode = Orders일 때)")]
    [Tooltip("모니터 가운데 위치. 기본값은 뒷벽(WALL MZ)의 싱크대 위 빈 자리라서 냉장고 글자를 가리지 않는다.")]
    public Vector3 monitorCenter = new Vector3(-1.15f, 2.65f, -3.04f);
    [Tooltip("모니터가 바라보는 방향(Y축 회전). 180 = 뒷벽에서 방 안쪽을 봄, 270 = 옆벽(Wall PX)에서 방 안쪽을 봄")]
    public float monitorYaw = 180f;
    [Tooltip("모니터 크기(가로, 세로) 미터")]
    public Vector2 monitorSize = new Vector2(2.0f, 1.1f);
    [Tooltip("한 번에 보여 줄 주문 수")]
    public int monitorMaxOrders = 6;

    [Header("라이더 호출 대기 모니터 (panelMode = Orders일 때)")]
    [Tooltip("조리 완료했지만 아직 라이더를 부르지 않은 주문을 보여 주는 두 번째 모니터")]
    public bool showRiderMonitor = true;
    [Tooltip("기본값은 뒷벽에서 주문 모니터 옆(수납장 위). 냉장고 글자와 겹치지 않게 냉장고 왼쪽까지만 쓴다.")]
    public Vector3 riderMonitorCenter = new Vector3(0.70f, 2.65f, -3.04f);
    public float riderMonitorYaw = 180f;
    public Vector2 riderMonitorSize = new Vector2(1.5f, 1.1f);

    [Header("보이는 모양")]
    public float labelHeight = 0.35f;
    public float labelSize = 0.03f;
    public bool playAlarmSound = true;
    [Tooltip("정보 상자를 화면 왼쪽 아래에 둔다(끄면 왼쪽 위). 기기 글자를 가리지 않는 쪽으로 고른다.")]
    public bool hudAtBottom = true;

    // ---------------------------------------------------------------- 백엔드 상태 JSON (이름을 바꾸면 안 됨)
    [Serializable] public class KDevice { public string id; public string name; public bool on; public int level; }
    [Serializable] public class KTimer { public string id; public string name; public string device; public int set_sec; public int remaining_sec; public string state; }
    [Serializable] public class KOrder { public int id; public int slot; public string menu; public int qty; public string request; public string photo; public string status; public long created_at; public long accepted_at; public long done_at; public long rider_at; }
    [Serializable] public class KEnv { public float kitchen_temp; public float fridge_temp; }
    [Serializable] public class KAlert { public string kind; public string target; public string message; }
    [Serializable] public class KLastCommand { public int seq; public string text; public string reply; public long ts; }
    [Serializable] public class KState
    {
        public long server_time;
        public KDevice[] devices;
        public KTimer[] timers;
        public KOrder[] orders;
        public KEnv env;
        public KAlert[] alerts;
        public KLastCommand last_command;
    }

    // ---------------------------------------------------------------- 씬에 붙이는 표시
    class Marker
    {
        public string id;
        public Transform target;
        public TextMesh label;
        public Light glow;
        public Color onColor;
        public Vector3 home;             // 글자의 원래 자리(기기 바로 위)
    }

    const string PrefKey = "orvo_server_url";
    const float FridgeLimit = 5f;

    readonly Dictionary<string, Marker> markers = new Dictionary<string, Marker>();
    Marker fridgeMarker;
    Component fridgeTmp;                 // TMPro.TMP_Text (없을 수 있어서 Component로 들고 있다)
    Font koreanFont;
    KState state;
    bool connected;
    string errorText = "";
    float lastOkTime = -999f;
    float lastRoundTripMs;
    string urlInput;
    int lastSeq = int.MinValue;
    float bubbleUntil;
    string bubbleHeard = "", bubbleReply = "";
    AudioSource audioSource;
    AudioClip beep;
    float nextBeepTime;
    GUIStyle boxStyle, textStyle, smallStyle, bubbleStyle, fieldStyle, buttonStyle;
    int styleForHeight;
    Texture2D boxBackground;
    MonitorView orderMonitor, riderMonitor;

    class MonitorView
    {
        public TextMesh title, body;
        public int menuLength;           // 한 줄에 들어가는 메뉴 글자 수
    }

    // 씬에 OrvoKitchen이 없으면 시작할 때 하나 만든다(파일만 넣고 Play를 눌러도 되게).
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (UnityEngine.Object.FindAnyObjectByType<OrvoKitchen>() == null)
            new GameObject("OrvoKitchen").AddComponent<OrvoKitchen>();
    }

    void Awake()
    {
        serverUrl = Normalize(PlayerPrefs.GetString(PrefKey, serverUrl));
        urlInput = serverUrl;
        koreanFont = Font.CreateDynamicFontFromOSFont(
            new[] { "Malgun Gothic", "맑은 고딕", "Apple SD Gothic Neo", "NanumGothic", "Noto Sans CJK KR", "Arial" }, 48);
        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;
        audioSource.spatialBlend = 0f;
        beep = MakeBeep();
    }

    void Start()
    {
        AddMarker("gas_1", gas1Object, new Color(1f, 0.45f, 0.1f));
        AddMarker("gas_2", gas2Object, new Color(1f, 0.45f, 0.1f));
        AddMarker("hood", hoodObject, new Color(0.3f, 0.8f, 1f));
        AddMarker("fan", fanObject, new Color(0.3f, 0.8f, 1f));
        AddMarker("microwave", microwaveObject, new Color(1f, 0.9f, 0.4f));
        fridgeMarker = MakeMarker("fridge", fridgeObject, new Color(0.5f, 0.7f, 1f));

        // 씬에 이미 있는 TextMeshPro 글자를 찾는다. TMPro 형식을 직접 쓰지 않고 'text' 속성이 있는 컴포넌트를 고른다.
        var textObject = GameObject.Find(fridgeTextObject);
        if (textObject != null)
        {
            foreach (var component in textObject.GetComponents<Component>())
            {
                if (component == null) continue;
                var type = component.GetType();
                if (type.Name.Contains("TextMeshPro") && type.GetProperty("text") != null) { fridgeTmp = component; break; }
            }
        }

        // 흰 판 숨기기: 실행 중에만 꺼 둔다. Play를 멈추면 원래대로 돌아오고 씬 파일은 바뀌지 않는다.
        if (panelMode == PanelMode.Hide || panelMode == PanelMode.Orders)
        {
            var panel = GameObject.Find(panelObject);
            if (panel != null) panel.SetActive(false);
            else Debug.LogWarning($"[Orvo] 씬에서 흰 판 '{panelObject}'을(를) 찾지 못했어요.");
            fridgeTmp = null;                 // 숨긴 판의 글자는 건드리지 않는다
        }
        if (panelMode == PanelMode.Orders)
        {
            // 주문 줄은 "3번 대기 " 다음에, 라이더 줄은 "3번 " 다음에 메뉴가 온다(앞머리 글자 수가 다름).
            orderMonitor = BuildMonitor("Order", monitorCenter, monitorYaw, monitorSize, "주문 현황", 5.5f);
            if (showRiderMonitor)
                riderMonitor = BuildMonitor("Rider", riderMonitorCenter, riderMonitorYaw, riderMonitorSize, "라이더 호출 대기", 3.5f);
        }

        StartCoroutine(PollLoop());
    }

    // ---------------------------------------------------------------- 표시 만들기
    void AddMarker(string id, string objectName, Color onColor)
    {
        var marker = MakeMarker(id, objectName, onColor);
        if (marker != null) markers[id] = marker;
    }

    Marker MakeMarker(string id, string objectName, Color onColor)
    {
        var target = GameObject.Find(objectName);
        if (target == null)
        {
            Debug.LogWarning($"[Orvo] 씬에서 '{objectName}'을(를) 찾지 못했어요({id}). Inspector에서 이름을 맞춰 주세요.");
            return null;
        }
        // 오브젝트의 보이는 크기를 구해서 그 위에 글자를 띄운다.
        var bounds = new Bounds(target.transform.position, Vector3.zero);
        bool first = true;
        foreach (var r in target.GetComponentsInChildren<Renderer>())
        {
            if (first) { bounds = r.bounds; first = false; }
            else bounds.Encapsulate(r.bounds);
        }
        var top = new Vector3(bounds.center.x, bounds.max.y, bounds.center.z);

        var labelObject = new GameObject("OrvoLabel_" + id);
        labelObject.transform.position = top + Vector3.up * labelHeight;
        var label = labelObject.AddComponent<TextMesh>();
        label.font = koreanFont;
        label.fontSize = 64;
        label.characterSize = labelSize;
        label.anchor = TextAnchor.LowerCenter;
        label.alignment = TextAlignment.Center;
        label.richText = false;
        label.text = "";
        labelObject.GetComponent<MeshRenderer>().sharedMaterial = koreanFont.material;

        var glowObject = new GameObject("OrvoGlow_" + id);
        glowObject.transform.position = top + Vector3.up * 0.15f;
        var glow = glowObject.AddComponent<Light>();
        glow.type = LightType.Point;
        glow.color = onColor;
        glow.range = 2.5f;
        glow.intensity = 0f;
        glow.shadows = LightShadows.None;
        glow.enabled = false;

        return new Marker { id = id, target = target.transform, label = label, glow = glow, onColor = onColor,
                            home = labelObject.transform.position };
    }

    // ---------------------------------------------------------------- 주문 모니터(벽에 거는 화면)
    MonitorView BuildMonitor(string monitorName, Vector3 center, float yaw, Vector2 size, string titleText, float prefixChars)
    {
        var root = new GameObject("Orvo" + monitorName + "Monitor");
        root.transform.position = center;
        root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

        // 화면: 얇은 상자(어느 쪽에서 봐도 보이게 Quad 대신 Cube를 쓴다). 테두리는 조금 큰 상자.
        MakeSlab(root.transform, monitorName + "Frame", new Vector3(size.x + 0.08f, size.y + 0.08f, 0.04f),
                 new Vector3(0f, 0f, 0.03f), new Color(0.02f, 0.02f, 0.03f));
        MakeSlab(root.transform, monitorName + "Screen", new Vector3(size.x, size.y, 0.04f),
                 new Vector3(0f, 0f, 0f), new Color(0.07f, 0.10f, 0.16f));

        // 글자는 화면 앞(로컬 -Z 쪽)에 둔다. TextMesh는 로컬 +Z를 등지고 볼 때 바로 읽힌다.
        float left = -size.x / 2f + 0.08f;
        float top = size.y / 2f - 0.06f;
        float lineHeight = size.y / (monitorMaxOrders + 2.2f);
        float bodyHeight = lineHeight * 0.9f;
        var view = new MonitorView();
        view.title = MakeMonitorText(root.transform, monitorName + "Title", new Vector3(left, top, -0.03f), lineHeight * 1.05f);
        view.body = MakeMonitorText(root.transform, monitorName + "Body", new Vector3(left, top - lineHeight * 1.5f, -0.03f), bodyHeight);
        view.title.text = titleText;
        view.body.text = "서버에 연결하는 중이에요";
        // 한글 한 글자의 너비는 줄 높이와 비슷하다. 화면 너비에 들어가는 만큼만 메뉴를 보여 준다.
        view.menuLength = Mathf.Max(4, Mathf.FloorToInt((size.x - 0.16f) / bodyHeight - prefixChars));
        return view;
    }

    void MakeSlab(Transform parent, string slabName, Vector3 size, Vector3 localPosition, Color color)
    {
        var slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        slab.name = "OrvoMonitor" + slabName;
        var slabCollider = slab.GetComponent<Collider>();
        if (slabCollider != null) Destroy(slabCollider);
        slab.transform.SetParent(parent, false);
        slab.transform.localPosition = localPosition;
        slab.transform.localScale = size;
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        var slabRenderer = slab.GetComponent<Renderer>();
        if (shader != null)
        {
            var material = new Material(shader);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            slabRenderer.sharedMaterial = material;
        }
    }

    TextMesh MakeMonitorText(Transform parent, string textName, Vector3 localPosition, float lineHeight)
    {
        var textObject = new GameObject("OrvoMonitor" + textName);
        textObject.transform.SetParent(parent, false);
        textObject.transform.localPosition = localPosition;
        var text = textObject.AddComponent<TextMesh>();
        text.font = koreanFont;
        text.fontSize = 64;
        text.characterSize = lineHeight / 6.4f;      // 글자 한 줄 높이 ≈ characterSize × fontSize × 0.1
        text.anchor = TextAnchor.UpperLeft;
        text.alignment = TextAlignment.Left;
        text.richText = true;
        text.color = Color.white;
        textObject.GetComponent<MeshRenderer>().sharedMaterial = koreanFont.material;
        return text;
    }

    void UpdateMonitor()
    {
        if (orderMonitor != null)
        {
            int waiting = 0, cooking = 0;
            var lines = new List<string>();
            if (state.orders != null)
            {
                foreach (var order in state.orders)
                {
                    bool isNew = order.status == "new";
                    bool isCooking = order.status == "accepted";
                    if (!isNew && !isCooking) continue;
                    if (isNew) waiting++; else cooking++;
                    if (lines.Count >= monitorMaxOrders) continue;
                    string mark = isNew ? "<color=#FFD24A>대기</color>" : "<color=#7CFC8A>조리</color>";
                    string qty = order.qty > 1 ? " x" + order.qty : "";
                    lines.Add($"{order.slot}번 {mark} {ShortMenu(order.menu, orderMonitor.menuLength)}{qty}");
                }
            }
            orderMonitor.title.text = $"주문 현황   대기 {waiting} · 조리 {cooking}";
            int hidden = waiting + cooking - lines.Count;
            if (hidden > 0) lines.Add($"<color=#AAAAAA>외 {hidden}건</color>");
            orderMonitor.body.text = lines.Count > 0 ? string.Join("\n", lines.ToArray()) : "<color=#AAAAAA>들어온 주문이 없어요</color>";
        }

        if (riderMonitor != null)
        {
            // 조리 완료(done)했지만 아직 라이더를 부르지 않은 주문. 라이더를 부르면 목록에서 빠진다.
            int ready = 0;
            var lines = new List<string>();
            if (state.orders != null)
            {
                foreach (var order in state.orders)
                {
                    if (order.status != "done") continue;
                    ready++;
                    if (lines.Count >= monitorMaxOrders) continue;
                    string qty = order.qty > 1 ? " x" + order.qty : "";
                    long minutes = order.done_at > 0 ? (state.server_time - order.done_at) / 60000 : 0;
                    string waited = minutes >= 1 ? $" <color=#FFB060>{minutes}분</color>" : "";
                    lines.Add($"{order.slot}번 {ShortMenu(order.menu, riderMonitor.menuLength)}{qty}{waited}");
                }
            }
            string count = ready > 0 ? $"<color=#FFB060>{ready}건</color>" : "0건";
            riderMonitor.title.text = $"라이더 호출 대기 {count}";
            int hidden = ready - lines.Count;
            if (hidden > 0) lines.Add($"<color=#AAAAAA>외 {hidden}건</color>");
            riderMonitor.body.text = lines.Count > 0 ? string.Join("\n", lines.ToArray()) : "<color=#AAAAAA>기다리는 주문이 없어요</color>";
        }
    }

    // "[1.5인] 구이삼겹 김치찜" → "구이삼겹 김치찜". 너무 길면 줄인다. 색 태그와 섞이지 않게 꺾쇠는 뺀다.
    static string ShortMenu(string menu, int maxLength)
    {
        menu = (menu ?? "").Trim();
        if (menu.StartsWith("["))
        {
            int close = menu.IndexOf(']');
            if (close >= 0) menu = menu.Substring(close + 1).Trim();
        }
        menu = menu.Replace("<", "").Replace(">", "");
        return menu.Length > maxLength ? menu.Substring(0, Mathf.Max(1, maxLength - 1)) + "…" : menu;
    }

    // ---------------------------------------------------------------- 서버 읽기
    IEnumerator PollLoop()
    {
        while (true)
        {
            float started = Time.realtimeSinceStartup;
            using (var request = UnityWebRequest.Get(serverUrl + "/api/state"))
            {
                request.timeout = 3;
                request.SetRequestHeader("ngrok-skip-browser-warning", "1");
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success)
                {
                    KState parsed = null;
                    try { parsed = JsonUtility.FromJson<KState>(request.downloadHandler.text); }
                    catch (Exception e) { errorText = "응답을 읽지 못했어요: " + e.Message; }
                    if (parsed != null && parsed.devices != null)
                    {
                        state = parsed;
                        connected = true;
                        errorText = "";
                        lastOkTime = Time.realtimeSinceStartup;
                        lastRoundTripMs = (Time.realtimeSinceStartup - started) * 1000f;
                        Apply();
                    }
                    else if (errorText == "") errorText = "응답 모양이 달라요";
                }
                else
                {
                    errorText = request.error ?? "";
                    if (errorText.Contains("Insecure"))
                        errorText = "HTTP가 막혀 있어요. Project Settings > Player > Other Settings > Allow downloads over HTTP를 Always allowed로";
                }
            }
            if (Time.realtimeSinceStartup - lastOkTime > 2f) connected = false;
            yield return new WaitForSecondsRealtime(connected ? pollInterval : 1f);
        }
    }

    // ---------------------------------------------------------------- 상태를 화면에
    void Apply()
    {
        foreach (var device in state.devices)
        {
            if (!markers.TryGetValue(device.id, out var marker)) continue;
            var timer = FindTimer(device.id);
            string line = device.name + "\n" + (device.on ? "켜짐" : "꺼짐");
            if (device.on && device.level > 0) line += " " + device.level + "단계";
            if (timer != null && timer.state == "running") line += "\n타이머 " + Clock(timer.remaining_sec);
            if (timer != null && timer.state == "ringing") line += "\n시간이 다 됐어요";
            marker.label.text = line;
            bool ringing = timer != null && timer.state == "ringing";
            marker.label.color = ringing ? Color.red : (device.on ? Color.white : new Color(0.75f, 0.75f, 0.75f));
            marker.glow.enabled = device.on;
            marker.glow.intensity = device.on ? 2f + Mathf.Max(0, device.level) * 1.5f : 0f;
        }

        if (state.env != null)
        {
            bool hot = state.env.fridge_temp > FridgeLimit;
            if (fridgeMarker != null)
            {
                fridgeMarker.label.text = "냉장고\n" + state.env.fridge_temp.ToString("0.0") + "도";
                fridgeMarker.label.color = hot ? Color.red : Color.white;
                fridgeMarker.glow.enabled = hot;
                fridgeMarker.glow.color = Color.red;
                fridgeMarker.glow.intensity = hot ? 3f : 0f;
            }
            if (fridgeTmp != null)
            {
                // TextMeshPro 형식을 직접 쓰지 않고 text 속성만 바꾼다(패키지가 없어도 컴파일되게).
                var property = fridgeTmp.GetType().GetProperty("text");
                if (property != null)
                    property.SetValue(fridgeTmp, "Fridge Status : " + state.env.fridge_temp.ToString("0.0") + " C" + (hot ? "  (HIGH)" : ""));
            }
        }

        UpdateMonitor();

        if (state.last_command != null && state.last_command.seq != lastSeq)
        {
            // 처음 읽은 값은 예전 명령이므로 띄우지 않는다. 서버를 다시 켜면 seq가 0부터라서 != 로 비교한다.
            if (lastSeq != int.MinValue && !string.IsNullOrEmpty(state.last_command.reply + state.last_command.text))
            {
                bubbleHeard = state.last_command.text ?? "";
                bubbleReply = state.last_command.reply ?? "";
                bubbleUntil = Time.realtimeSinceStartup + 6f;
            }
            lastSeq = state.last_command.seq;
        }
    }

    KTimer FindTimer(string deviceId)
    {
        if (state == null || state.timers == null) return null;
        foreach (var timer in state.timers)
            if (timer.device == deviceId) return timer;
        return null;
    }

    bool HasAlert(string kind)
    {
        if (state == null || state.alerts == null) return false;
        foreach (var alert in state.alerts)
            if (alert.kind == kind) return true;
        return false;
    }

    static string Clock(int sec)
    {
        if (sec < 0) sec = 0;
        return (sec / 60) + ":" + (sec % 60).ToString("00");
    }

    void Update()
    {
        // 타이머가 끝난 동안 1초마다 삐 소리
        if (playAlarmSound && connected && HasAlert("timer_done") && Time.realtimeSinceStartup >= nextBeepTime)
        {
            audioSource.PlayOneShot(beep, 0.6f);
            nextBeepTime = Time.realtimeSinceStartup + 1f;
        }
    }

    void LateUpdate()
    {
        // 글자가 늘 카메라를 보게 한다.
        var cam = Camera.main;
        if (cam == null) return;
        foreach (var marker in markers.Values) Face(marker, cam);
        if (fridgeMarker != null) Face(fridgeMarker, cam);
    }

    static void Face(Marker marker, Camera cam)
    {
        // 냉장고처럼 키가 큰 기기는 글자가 화면 위로 나가므로, 화면 안에 들어올 때까지 조금씩 내린다.
        var position = marker.home;
        for (int i = 0; i < 30; i++)
        {
            var view = cam.WorldToViewportPoint(position);
            if (view.z <= 0f || view.y <= 0.86f) break;
            position.y -= 0.1f;
        }
        marker.label.transform.position = position;
        marker.label.transform.rotation = Quaternion.LookRotation(position - cam.transform.position);
    }

    static AudioClip MakeBeep()
    {
        const int rate = 44100;
        int samples = rate / 4;                           // 0.25초
        var data = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / rate;
            float fade = Mathf.Clamp01(Mathf.Min(i, samples - i) / (rate * 0.01f));   // 딱 소리 방지
            data[i] = Mathf.Sin(2f * Mathf.PI * 880f * t) * fade;
        }
        var clip = AudioClip.Create("OrvoBeep", samples, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    static string Normalize(string value)
    {
        value = (value ?? "").Trim();
        while (value.EndsWith("/")) value = value.Substring(0, value.Length - 1);
        if (value.Length > 0 && !value.StartsWith("http://") && !value.StartsWith("https://")) value = "http://" + value;
        return value;
    }

    // ---------------------------------------------------------------- 화면 위 정보(왼쪽 위 상자, 아래 말풍선)
    void BuildStyles()
    {
        if (boxStyle != null && styleForHeight == Screen.height) return;
        styleForHeight = Screen.height;
        int size = Mathf.Max(13, Screen.height / 45);
        if (boxBackground == null)
        {
            // 뒤의 기기 글자가 비치지 않게 진한 바탕을 쓴다.
            boxBackground = new Texture2D(1, 1);
            boxBackground.SetPixel(0, 0, new Color(0.05f, 0.07f, 0.1f, 0.88f));
            boxBackground.Apply();
        }
        boxStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(12, 12, 10, 10) };
        boxStyle.normal.background = boxBackground;
        textStyle = new GUIStyle(GUI.skin.label) { font = koreanFont, fontSize = size, wordWrap = true, richText = true };
        textStyle.normal.textColor = Color.white;
        smallStyle = new GUIStyle(textStyle) { fontSize = Mathf.Max(11, size - 3) };
        bubbleStyle = new GUIStyle(textStyle) { fontSize = size + 6, alignment = TextAnchor.MiddleCenter };
        fieldStyle = new GUIStyle(GUI.skin.textField) { font = koreanFont, fontSize = size };
        buttonStyle = new GUIStyle(GUI.skin.button) { font = koreanFont, fontSize = size };
    }

    void OnGUI()
    {
        BuildStyles();
        float width = Mathf.Clamp(Screen.width * 0.34f, 320f, 560f);
        GUILayout.BeginArea(new Rect(12, 12, width, Screen.height - 24));
        if (hudAtBottom) GUILayout.FlexibleSpace();          // 상자를 아래로 민다
        GUILayout.BeginVertical(boxStyle);

        GUILayout.BeginHorizontal();
        GUILayout.Label("서버", textStyle, GUILayout.Width(textStyle.fontSize * 2.6f));
        urlInput = GUILayout.TextField(urlInput ?? "", fieldStyle);
        if (GUILayout.Button("적용", buttonStyle, GUILayout.Width(textStyle.fontSize * 3.6f)))
        {
            serverUrl = Normalize(urlInput);
            urlInput = serverUrl;
            PlayerPrefs.SetString(PrefKey, serverUrl);
            PlayerPrefs.Save();
            lastOkTime = -999f;
            connected = false;
        }
        GUILayout.EndHorizontal();

        if (connected)
            GUILayout.Label($"<color=#7CFC8A>연결됨</color>  {lastRoundTripMs:0}ms", textStyle);
        else
            GUILayout.Label($"<color=#FF7070>연결 안 됨</color>  {errorText}", textStyle);

        if (connected && state != null)
        {
            if (state.env != null)
            {
                string fridgeColor = state.env.fridge_temp > FridgeLimit ? "#FF7070" : "#FFFFFF";
                GUILayout.Label($"주방 {state.env.kitchen_temp:0.0}도   냉장고 <color={fridgeColor}>{state.env.fridge_temp:0.0}도</color>", textStyle);
            }

            if (state.timers != null)
            {
                foreach (var timer in state.timers)
                {
                    if (timer.state == "running")
                        GUILayout.Label($"{timer.name}  {Clock(timer.remaining_sec)}", textStyle);
                    else if (timer.state == "ringing")
                        GUILayout.Label($"<color=#FF7070>{timer.name}  시간이 다 됐어요</color>", textStyle);
                }
            }

            if (state.orders != null && state.orders.Length > 0)
            {
                int waiting = 0, cooking = 0;
                foreach (var order in state.orders)
                {
                    if (order.status == "new") waiting++;
                    else if (order.status == "accepted") cooking++;
                }
                GUILayout.Label($"주문  접수 대기 {waiting}   조리 중 {cooking}", textStyle);
                foreach (var order in state.orders)
                {
                    if (order.status != "new" && order.status != "accepted") continue;
                    string mark = order.status == "new" ? "대기" : "조리";
                    string qty = order.qty > 1 ? " x" + order.qty : "";
                    GUILayout.Label($"  {order.slot}번 [{mark}] {order.menu}{qty}", smallStyle);
                }
            }
        }

        GUILayout.EndVertical();
        GUILayout.EndArea();

        if (Time.realtimeSinceStartup < bubbleUntil)
        {
            float bubbleWidth = Mathf.Min(Screen.width - 40f, 1100f);
            float bubbleHeight = bubbleStyle.fontSize * 4.2f;
            // 정보 상자가 아래에 있으면 말풍선은 위에, 아니면 아래에 둔다(서로 겹치지 않게).
            float bubbleY = hudAtBottom ? 16f : Screen.height - bubbleHeight - 24f;
            var rect = new Rect((Screen.width - bubbleWidth) / 2f, bubbleY, bubbleWidth, bubbleHeight);
            GUI.Box(rect, GUIContent.none, boxStyle);
            string heard = string.IsNullOrEmpty(bubbleHeard) ? "" : $"<color=#BBBBBB>\"{bubbleHeard}\"</color>\n";
            GUI.Label(rect, heard + bubbleReply, bubbleStyle);
        }
    }
}
