using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// World map (scene "WorldMap"): a small diorama of the regions. Single and multi player both
// come here from the lobby. The host picks an unlocked region (mouse click or arrows + Enter)
// and the run starts at that region's first stage. After a region's result screen the map
// opens again and plays the unlock of the next region once.
// Multiplayer: only the host chooses; web participants see the "map" status. Remote approval
// is blocked here (no stage, no video sender); the request list stays for the next stage.
public class WorldMapController : MonoBehaviour
{
    [SerializeField] private Camera mapCamera;
    [Tooltip("Marker that floats above the selected region (optional).")]
    [SerializeField] private Transform selectMarker;
    [SerializeField] private Vector3 viewOffset = new Vector3(0f, 9f, -12f);
    [SerializeField, Min(0.1f)] private float cameraSharpness = 3f;
    [SerializeField, Min(0.1f)] private float unlockDuration = 2.5f;
    [SerializeField] private string lobbySceneName = "Lobby";

    private const string MapStageId = "map";
    private const string MapStageName = "지도 · 지역 선택";

    private readonly List<WorldMapRegion> regions = new List<WorldMapRegion>();
    private NaWaBaStageList list;
    private RemoteInputGateway gateway;
    private int selected;
    private int unlockingRegion = -1;
    private float unlockWait;
    private bool loading, confirmReset;
    private Rect panelRect;
    private GUIStyle titleStyle, nameStyle, textStyle, buttonStyle, tagStyle, bannerStyle;

    private bool Busy => loading || unlockingRegion >= 0;

    private void Start()
    {
        if (mapCamera == null) mapCamera = Camera.main;
        list = NaWaBaStageList.Instance;
        foreach (WorldMapRegion region in FindObjectsByType<WorldMapRegion>(FindObjectsSortMode.None))
            if (region.gameObject.scene == gameObject.scene) regions.Add(region);
        regions.Sort((a, b) => a.RegionIndex.CompareTo(b.RegionIndex));
        if (regions.Count == 0)
            Debug.LogError("WorldMapController: no WorldMapRegion. Run 'NaWaBa > 지도 만들기 (WorldMap)'.", this);
        if (list == null)
            Debug.LogError("WorldMapController: Resources/NaWaBaStages not found.", this);

        if (GameSession.Mode == GameMode.Multiplayer && MultiplayerHostSession.Instance != null)
            gateway = MultiplayerHostSession.Instance.Gateway;
        if (gateway != null)
        {
            gateway.AllowRemoteApproval = false;
            // A remote turn ends with the region (result screen); make sure nobody keeps it here.
            if (gateway.HasAuthority) gateway.ReclaimControl();
            gateway.ReportStage(MapStageId, MapStageName, "");
        }

        int justUnlocked = WorldProgress.ConsumeJustUnlocked();
        foreach (WorldMapRegion region in regions)
            region.ShowLocked(!WorldProgress.IsUnlocked(region.RegionIndex) || region.RegionIndex == justUnlocked);

        int focus = justUnlocked >= 0 ? justUnlocked
            : WorldProgress.LastRegion >= 0 ? WorldProgress.LastRegion : HighestPlayable();
        selected = Mathf.Max(0, regions.FindIndex(r => r.RegionIndex == focus));
        if (justUnlocked >= 0 && selected < regions.Count && regions[selected].RegionIndex == justUnlocked)
        {
            unlockingRegion = justUnlocked;
            unlockWait = 0.8f;   // let the camera arrive first
        }

        // Start from an overview of the whole map, then glide to the selected region.
        if (mapCamera != null && regions.Count > 0)
        {
            Vector3 center = Vector3.zero;
            foreach (WorldMapRegion region in regions) center += region.FocusPoint;
            center /= regions.Count;
            mapCamera.transform.position = center + viewOffset * 1.8f;
            mapCamera.transform.LookAt(center);
        }
    }

    private void Update()
    {
        if (gateway != null) gateway.AllowRemoteApproval = false;
        float dt = Time.unscaledDeltaTime;
        UpdateCamera(dt);
        UpdateMarker();
        if (loading) return;

        if (unlockingRegion >= 0)
        {
            UpdateUnlock(dt);
            return;
        }
        HandleInput();
    }

    // ---------- unlock ----------

    private void UpdateUnlock(float dt)
    {
        WorldMapRegion region = regions.Find(r => r.RegionIndex == unlockingRegion);
        bool skip = Keyboard.current != null &&
            (Keyboard.current.spaceKey.wasPressedThisFrame || Keyboard.current.escapeKey.wasPressedThisFrame);
        if (region == null)
        {
            unlockingRegion = -1;
            return;
        }
        if (unlockWait > 0f)
        {
            unlockWait -= dt;
            if (unlockWait <= 0f && !skip) region.PlayUnlock(unlockDuration);
            if (!skip) return;
        }
        if (skip) region.FinishUnlock();
        if (!region.IsAnimating)
        {
            region.ShowLocked(false);
            unlockingRegion = -1;
        }
    }

    // ---------- input ----------

    private void HandleInput()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard.leftArrowKey.wasPressedThisFrame || keyboard.aKey.wasPressedThisFrame) Select(selected - 1);
            if (keyboard.rightArrowKey.wasPressedThisFrame || keyboard.dKey.wasPressedThisFrame) Select(selected + 1);
            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame ||
                keyboard.spaceKey.wasPressedThisFrame)
                StartSelected();
        }

        Mouse mouse = Mouse.current;
        if (mouse == null || mapCamera == null || !mouse.leftButton.wasPressedThisFrame) return;
        Vector2 position = mouse.position.ReadValue();
        // IMGUI rects are measured from the top of the screen.
        if (panelRect.Contains(new Vector2(position.x, Screen.height - position.y))) return;
        if (!Physics.Raycast(mapCamera.ScreenPointToRay(position), out RaycastHit hit, 500f)) return;
        WorldMapRegion clicked = hit.collider.GetComponentInParent<WorldMapRegion>();
        if (clicked == null) return;
        int index = regions.IndexOf(clicked);
        if (index == selected) StartSelected();
        else Select(index);
    }

    private void Select(int index)
    {
        if (regions.Count == 0) return;
        selected = Mathf.Clamp(index, 0, regions.Count - 1);
        confirmReset = false;
    }

    private void StartSelected()
    {
        if (Busy || selected >= regions.Count) return;
        int region = regions[selected].RegionIndex;
        string scene = PlayableScene(region);
        if (string.IsNullOrEmpty(scene)) return;
        loading = true;
        WorldProgress.LastRegion = region;
        RunRecord.BeginRun();
        Debug.Log($"[WorldMap] start region {region} ({Title(region)}) at {scene}", this);
        if (!StageSceneLoader.Load(scene, null, this)) loading = false;
    }

    private void ReturnToLobby()
    {
        if (Busy || !StageSceneLoader.CanLoad(lobbySceneName)) return;
        loading = true;
        if (!StageSceneLoader.Load(lobbySceneName, null, this)) loading = false;
    }

    // ---------- region info ----------

    private string PlayableScene(int region)
    {
        if (list == null || !WorldProgress.IsUnlocked(region) || region >= list.RegionCount) return "";
        string scene = list.FirstSceneOfRegion(region);
        return StageSceneLoader.CanLoad(scene) ? scene : "";
    }

    private int HighestPlayable()
    {
        int best = 0;
        foreach (WorldMapRegion region in regions)
            if (!string.IsNullOrEmpty(PlayableScene(region.RegionIndex))) best = region.RegionIndex;
        return best;
    }

    private string Title(int region) =>
        list != null && region < list.RegionCount ? list.RegionTitle(region)
            : regions.Find(r => r.RegionIndex == region)?.FallbackTitle ?? "???";

    private string StateTag(int region)
    {
        if (!WorldProgress.IsUnlocked(region) || region == unlockingRegion) return "LOCKED";
        if (string.IsNullOrEmpty(PlayableScene(region))) return "COMING SOON";
        return WorldProgress.IsCleared(region) ? "CLEAR" : "OPEN";
    }

    // ---------- camera ----------

    private void UpdateCamera(float dt)
    {
        if (mapCamera == null || selected >= regions.Count) return;
        Vector3 target = regions[selected].FocusPoint;
        float k = 1f - Mathf.Exp(-cameraSharpness * dt);
        Transform cam = mapCamera.transform;
        cam.position = Vector3.Lerp(cam.position, target + viewOffset, k);
        Quaternion look = Quaternion.LookRotation(target - cam.position, Vector3.up);
        cam.rotation = Quaternion.Slerp(cam.rotation, look, k);
    }

    private void UpdateMarker()
    {
        if (selectMarker == null || selected >= regions.Count) return;
        float bob = Mathf.Sin(Time.unscaledTime * 3f) * 0.25f;
        selectMarker.position = regions[selected].LabelPoint + Vector3.up * (0.6f + bob);
        selectMarker.rotation = Quaternion.Euler(0f, Time.unscaledTime * 90f, 45f);
    }

    // ---------- screen ----------

    private void OnGUI()
    {
        EnsureStyles();
        GUI.Label(new Rect(0, 12, Screen.width, 44), "NaWaBa · 탐험 지도", titleStyle);
        string difficulty = GameSession.Difficulty != null ? GameSession.Difficulty.name : "-";
        string mode = GameSession.Mode == GameMode.Multiplayer ? "MULTI" : "SINGLE";
        GUI.Label(new Rect(16, 12, 400, 26), $"{mode} · {difficulty}", textStyle);
        if (gateway != null)
            GUI.Label(new Rect(16, 38, 700, 26),
                $"Server: {(gateway.IsConnected ? "CONNECTED" : "DISCONNECTED")} / Room: {gateway.RoomLabel}" +
                $" / Guests: {gateway.ParticipantCount} / Waiting: {gateway.QueueCount}", textStyle);

        DrawRegionLabels();

        if (unlockingRegion >= 0)
        {
            GUI.Label(new Rect(0, Screen.height * 0.18f, Screen.width, 60),
                $"NEW REGION · {Title(unlockingRegion)}", bannerStyle);
            GUI.Label(new Rect(0, Screen.height - 40, Screen.width, 30), "SPACE: SKIP", tagStyle);
            panelRect = Rect.zero;
            return;
        }
        DrawPanel();
    }

    private void DrawRegionLabels()
    {
        if (mapCamera == null) return;
        for (int i = 0; i < regions.Count; i++)
        {
            Vector3 screen = mapCamera.WorldToScreenPoint(regions[i].LabelPoint);
            if (screen.z <= 0f) continue;
            int region = regions[i].RegionIndex;
            var rect = new Rect(screen.x - 120, Screen.height - screen.y - 46, 240, 30);
            GUI.Label(rect, (i == selected ? "> " : "") + Title(region), nameStyle);
            GUI.Label(new Rect(rect.x, rect.y + 28, rect.width, 22), StateTag(region), tagStyle);
        }
    }

    private void DrawPanel()
    {
        float width = Mathf.Min(760f, Screen.width - 32f), height = 150f;
        panelRect = new Rect((Screen.width - width) / 2f, Screen.height - height - 16f, width, height);
        GUI.Box(panelRect, GUIContent.none);
        GUI.Box(panelRect, GUIContent.none);
        if (selected >= regions.Count) return;

        int region = regions[selected].RegionIndex;
        string tag = StateTag(region);
        GUI.Label(new Rect(panelRect.x + 20, panelRect.y + 10, width - 40, 34), $"{Title(region)}  [{tag}]", nameStyle);
        string line = list != null && region < list.RegionCount ? list.RegionDescription(region) : "";
        if (tag == "LOCKED") line = "앞 지역을 클리어하면 열립니다.";
        else if (tag == "COMING SOON") line = string.IsNullOrEmpty(line) ? "준비 중인 지역입니다." : line + " (준비 중)";
        else if (list != null) line += $"  ·  게임 {list.StageCountOfRegion(region)}개";
        GUI.Label(new Rect(panelRect.x + 20, panelRect.y + 46, width - 40, 26), line, textStyle);

        float y = panelRect.yMax - 58f, bw = (width - 80f) / 3f;
        bool playable = !string.IsNullOrEmpty(PlayableScene(region));
        GUI.enabled = playable && !Busy;
        if (GUI.Button(new Rect(panelRect.x + 20, y, bw, 42), "START", buttonStyle)) StartSelected();
        GUI.enabled = !Busy;
        if (GUI.Button(new Rect(panelRect.x + 40 + bw, y, bw, 42), "LOBBY", buttonStyle)) ReturnToLobby();
        if (!confirmReset)
        {
            if (GUI.Button(new Rect(panelRect.x + 60 + bw * 2, y, bw, 42), "RESET MAP", buttonStyle)) confirmReset = true;
        }
        else
        {
            float half = (bw - 8f) / 2f;
            if (GUI.Button(new Rect(panelRect.x + 60 + bw * 2, y, half, 42), "RESET!", buttonStyle))
            {
                WorldProgress.ResetAll();
                foreach (WorldMapRegion r in regions) r.ShowLocked(!WorldProgress.IsUnlocked(r.RegionIndex));
                Select(0);
            }
            if (GUI.Button(new Rect(panelRect.x + 68 + bw * 2 + half, y, half, 42), "NO", buttonStyle)) confirmReset = false;
        }
        GUI.enabled = true;
        GUI.Label(new Rect(panelRect.x, panelRect.y - 26, width, 24), "←/→ 선택 · ENTER 시작 · 클릭으로도 선택", tagStyle);
    }

    private void EnsureStyles()
    {
        if (titleStyle != null) return;
        titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        titleStyle.normal.textColor = new Color(1f, 0.9f, 0.45f);
        nameStyle = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        nameStyle.normal.textColor = Color.white;
        textStyle = new GUIStyle(GUI.skin.label) { fontSize = 18 };
        textStyle.normal.textColor = Color.white;
        tagStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        tagStyle.normal.textColor = new Color(0.85f, 0.92f, 1f);
        buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 18, fontStyle = FontStyle.Bold };
        bannerStyle = new GUIStyle(GUI.skin.label) { fontSize = 40, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        bannerStyle.normal.textColor = new Color(1f, 0.85f, 0.2f);
    }
}
