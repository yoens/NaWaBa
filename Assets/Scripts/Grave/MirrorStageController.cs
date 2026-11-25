using System.Collections.Generic;
using TMPro;
using UnityEngine;

// Region 2 (무덤), game 3: the mirror cave under the graveyard. In each room the light from a
// crack must be bounced off mirrors onto a crystal; then the door sinks and the next room is
// open. Walk into a mirror to push it one cell, jump next to it to turn it. A blue plate puts
// the room's mirrors back. No fail; control can change hands any time (all stops meanwhile).
// After the last room a stairway leads up to the treasure; opening it clears the stage
// (the region, when it is the region's last game).
// Difficulty = how many rooms are played (Easy 2 / Normal 3 / Hard 4); rooms after those
// start already solved, with their doors open.
[DefaultExecutionOrder(-100)]
public class MirrorStageController : MonoBehaviour, IStageFlow
{
    [Header("Connections (auto-found when empty)")]
    [SerializeField] private ClimbActorController actor;
    [SerializeField] private InputRouter inputRouter;
    [SerializeField] private RemoteInputGateway gateway;
    [SerializeField] private TMP_Text statusText;

    [Header("Course (set by the builder, in order)")]
    [SerializeField] private MirrorRoom[] rooms = new MirrorRoom[0];
    [SerializeField] private MirrorTreasure treasure;

    [Header("Web display")]
    [SerializeField] private string stageId = "mirror";
    [SerializeField] private string stageName = "무덤 3 · 거울 동굴";

    [Header("Rules (difficulty overrides the room count)")]
    [SerializeField, Range(1, 4)] private int roomsToPlay = 3;
    [SerializeField, Min(0.05f)] private float pushHold = 0.18f;      // seconds of walking into a mirror
    [SerializeField, Min(0.5f)] private float turnReach = 1.9f;       // metres from a mirror's centre
    [SerializeField, Min(0f)] private float hostResumeDelay = 1.5f;
    [SerializeField] private bool playIntro = true;
    [SerializeField, Min(0f)] private float treasureDelay = 2.5f;
    [SerializeField, Min(0f)] private float nextStageDelay = 3f;

    [Header("After the run")]
    [SerializeField] private string newGameSceneName = "Stage_Dodge";
    [SerializeField] private string lobbySceneName = "Lobby";

    private const string StageControls = "move,jump,look";

    private enum State { Intro, Playing, Treasure, Advancing, Finished }

    private State state = State.Intro;
    private StageHandoff handoff;
    private StageIntroCamera intro;
    private ClimbCameraController followCamera;
    private RemoteInputSource stageRemoteInputSource;
    private MirrorPiece pushing;
    private float pushTimer, timer, advanceLeft, messageLeft;
    private bool initialized, usesPersistentGateway, presetting, roomsPrepared;
    private string nextScene = "";
    private string message = "";
    private string[] resultLines;
    private string statusLabel = "";
    private GUIStyle titleStyle, lineStyle, buttonStyle, labelStyle, hintStyle, markerStyle;

    private bool IsSinglePlayer => GameSession.Mode == GameMode.SinglePlayer;
    private bool IntroPlaying => intro != null && intro.IsPlaying;
    private int PlayedRooms => Mathf.Clamp(roomsToPlay, 1, Mathf.Max(1, rooms.Length));

    private void Awake()
    {
        if (actor == null) actor = FindInScene<ClimbActorController>();
        if (inputRouter == null) inputRouter = FindInScene<InputRouter>();
        if (gateway == null) gateway = FindInScene<RemoteInputGateway>();
        if (actor == null || inputRouter == null)
        {
            Debug.LogError("MirrorStageController: ClimbActorController와 InputRouter가 필요합니다.", this);
            enabled = false;
            return;
        }

        RemoteInputGateway configuredGateway = gateway;
        usesPersistentGateway = !IsSinglePlayer && configuredGateway != null &&
            configuredGateway.UsesGameSessionMode;
        if (usesPersistentGateway)
        {
            stageRemoteInputSource = configuredGateway.RemoteInputTarget;
            configuredGateway.enabled = false;
            gateway = RemoteInputGateway.ResolveForScene(configuredGateway);
            if (gateway == null || stageRemoteInputSource == null)
            {
                Debug.LogError("MirrorStageController: 멀티플레이 세션을 찾을 수 없습니다. 로비에서 MULTI PLAY로 시작하세요.", this);
                usesPersistentGateway = false;
                gateway = null;
            }
        }

        NaWaBaDifficulty difficulty = GameSession.Difficulty;
        if (difficulty != null)
        {
            roomsToPlay = difficulty.MirrorRooms;
            if (!string.IsNullOrEmpty(difficulty.WebLabel) && !stageName.Contains("("))
                stageName = $"{stageName} ({difficulty.WebLabel})";
        }

        if (rooms == null || rooms.Length == 0)
        {
            var found = new List<MirrorRoom>();
            foreach (MirrorRoom room in FindObjectsByType<MirrorRoom>(FindObjectsSortMode.None))
                if (room.gameObject.scene == gameObject.scene) found.Add(room);
            found.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            rooms = found.ToArray();
        }
        if (treasure == null) treasure = FindInScene<MirrorTreasure>();
        if (rooms.Length == 0 || treasure == null)
            Debug.LogError("MirrorStageController: 거울 방이나 보물이 없습니다. " +
                "'NaWaBa > 무덤 거울 동굴 만들기 (Stage_Mirror)'를 실행하세요.", this);
        followCamera = FindInScene<ClimbCameraController>();

        RunRecord.BeginRunIfIdle();
        if (gateway != null) gateway.AllowRemoteApproval = false;
        handoff = new StageHandoff(usesPersistentGateway ? gateway : null, hostResumeDelay);
        initialized = true;
    }

    private void OnEnable()
    {
        if (!initialized) return;
        state = State.Intro;
        resultLines = null;
        pushing = null;
        message = "";
        actor.ClearRespawnPoint();
        handoff.Reset();
        actor.InputLocked = false;
        actor.Fell += OnFell;
        actor.JumpPressed += OnJump;
        foreach (MirrorRoom room in rooms) if (room != null) room.SolvedNow += OnRoomSolved;

        if (usesPersistentGateway)
        {
            bool carried = gateway.IsCarryingControl;
            if (gateway.BindStageInputs(inputRouter, stageRemoteInputSource, stageId, stageName, StageControls))
            {
                if (!carried) inputRouter.UseLocalInput();
                gateway.RemoteControlPreparing += OnRemotePreparing;
                gateway.RemoteControlStarted += OnRemoteStarted;
                gateway.RemoteControlEnded += OnRemoteEnded;
                if (carried) handoff.OnPreparing();
            }
            else
            {
                Debug.LogError("MirrorStageController: 스테이지 입력을 멀티플레이 세션에 연결하지 못했습니다.", this);
                usesPersistentGateway = false;
            }
        }
        if (!usesPersistentGateway) inputRouter.UseLocalInput();

        actor.Frozen = true;
        intro = playIntro ? StageIntroCamera.For(followCamera) : null;
        if (intro != null) intro.Play(IntroRoute(), followCamera, OnIntroFinished);
        else OnIntroFinished();
    }

    // The rooms set themselves up in their own Awake (after this controller's), so the rooms
    // the difficulty skips are solved here.
    private void Start()
    {
        if (!initialized || roomsPrepared) return;
        roomsPrepared = true;
        presetting = true;
        for (int i = PlayedRooms; i < rooms.Length; i++)
            if (rooms[i] != null) rooms[i].PresetSolved();
        presetting = false;
        Debug.Log($"[Mirror] playing {PlayedRooms} of {rooms.Length} rooms", this);
    }

    private void OnDisable()
    {
        if (actor != null)
        {
            actor.Fell -= OnFell;
            actor.JumpPressed -= OnJump;
        }
        foreach (MirrorRoom room in rooms) if (room != null) room.SolvedNow -= OnRoomSolved;
        if (usesPersistentGateway && gateway != null)
        {
            gateway.RemoteControlPreparing -= OnRemotePreparing;
            gateway.RemoteControlStarted -= OnRemoteStarted;
            gateway.RemoteControlEnded -= OnRemoteEnded;
            gateway.AllowRemoteApproval = false;
            if (gateway.HasAuthority && !gateway.IsCarryingControl) gateway.ReclaimControl();
            gateway.UnbindStageInputs(inputRouter);
        }
        if (intro != null) intro.Skip();
        if (actor != null)
        {
            actor.Frozen = false;
            actor.InputLocked = false;
        }
        handoff?.Reset();
    }

    public void PrepareToLeave() => enabled = false;

    // Treasure -> rooms (last to first) -> start.
    private List<Vector3> IntroRoute()
    {
        var route = new List<Vector3>();
        if (treasure != null) route.Add(treasure.transform.position);
        for (int i = rooms.Length - 1; i >= 0; i--)
            if (rooms[i] != null) route.Add(rooms[i].transform.position);
        route.Add(actor.transform.position);
        return route;
    }

    private void OnIntroFinished()
    {
        if (!isActiveAndEnabled || state != State.Intro) return;
        state = State.Playing;
    }

    private int SolvedCount()
    {
        int n = 0;
        for (int i = 0; i < PlayedRooms; i++) if (rooms[i] != null && rooms[i].Solved) n++;
        return n;
    }

    private void Update()
    {
        if (!initialized) return;
        float dt = Time.deltaTime;
        handoff.Tick(dt);

        bool done = state == State.Finished || state == State.Advancing;
        if (usesPersistentGateway)
            gateway.AllowRemoteApproval = gateway.isActiveAndEnabled && !done && inputRouter.isActiveAndEnabled;

        bool paused = handoff.Paused && !done;
        actor.Frozen = paused || IntroPlaying || done || state == State.Treasure;
        foreach (MirrorRoom room in rooms) if (room != null) room.Frozen = paused;

        if (paused)
        {
            pushing = null;
            SetStatus(handoff.Label);
            return;
        }
        if (!IntroPlaying && !done) RunRecord.AddControlTime(gateway, dt);
        if (messageLeft > 0f) messageLeft -= dt;

        switch (state)
        {
            case State.Intro:
                SetStatus("LIGHT THE CRYSTALS!\n(SPACE: SKIP)");
                break;
            case State.Playing:
                UpdatePush(dt);
                int solved = SolvedCount();
                string head = solved >= PlayedRooms ? "THE WAY UP IS OPEN!" : $"MIRROR ROOM {solved + 1}/{PlayedRooms}";
                SetStatus(messageLeft > 0f ? message : head);
                break;
            case State.Treasure:
                timer -= dt;
                SetStatus("TREASURE!");
                if (timer <= 0f) Clear();
                break;
            case State.Advancing:
                advanceLeft -= dt;
                SetStatus(advanceLeft > 0f ? $"CLEAR!\nNEXT STAGE IN {Mathf.CeilToInt(advanceLeft)}" : "LOADING...");
                if (advanceLeft <= 0f && !StageSceneLoader.IsLoading &&
                    !StageSceneLoader.Load(nextScene, this, this, carryControl: true))
                    FinishRun(RunRecord.Current(gateway));
                break;
            case State.Finished:
                SetStatus("REGION CLEAR!");
                break;
        }
    }

    // ---------- mirrors ----------

    // Walking into a mirror (towards its centre, roughly along a grid axis) for a moment pushes it.
    private void UpdatePush(float dt)
    {
        Vector2 move = inputRouter.isActiveAndEnabled ? Vector2.ClampMagnitude(inputRouter.ReadMove(), 1f) : Vector2.zero;
        MirrorPiece candidate = null;
        Vector2Int dir = Vector2Int.zero;
        if (move.sqrMagnitude > 0.25f && followCamera != null)
        {
            Vector3 wish = followCamera.PlanarRotation * new Vector3(move.x, 0f, move.y);
            float best = float.MaxValue;
            foreach (MirrorRoom room in rooms)
            {
                if (room == null || room.Solved) continue;
                Vector3 player = room.transform.InverseTransformPoint(actor.transform.position);
                Vector3 wishLocal = room.transform.InverseTransformDirection(wish);
                wishLocal.y = 0f;
                if (wishLocal.sqrMagnitude < 0.0001f) continue;
                wishLocal.Normalize();
                foreach (MirrorPiece piece in room.Pieces)
                {
                    if (piece.Moving) continue;
                    Vector3 delta = room.Center(piece.Cell) - player;
                    delta.y = 0f;
                    Vector2Int axis = MirrorRoom.Axis(delta);
                    float along = Mathf.Abs(axis.x != 0 ? delta.x : delta.z);
                    float lateral = Mathf.Abs(axis.x != 0 ? delta.z : delta.x);
                    if (along > room.Cell * 0.75f || lateral > room.Cell * 0.35f) continue;
                    if (Vector3.Dot(wishLocal, new Vector3(axis.x, 0f, axis.y)) < 0.7f) continue;
                    if (along < best) { best = along; candidate = piece; dir = axis; }
                }
            }
        }
        if (candidate == null || candidate != pushing)
        {
            pushing = candidate;
            pushTimer = 0f;
            return;
        }
        pushTimer += dt;
        if (pushTimer < pushHold) return;
        pushTimer = 0f;
        candidate.Room.TryPush(candidate, dir);
    }

    private void OnJump()
    {
        if (state != State.Playing || handoff.Paused || IntroPlaying) return;
        MirrorPiece nearest = null;
        float best = turnReach * turnReach;
        foreach (MirrorRoom room in rooms)
        {
            if (room == null || room.Solved) continue;
            foreach (MirrorPiece piece in room.Pieces)
            {
                Vector3 d = piece.transform.position - actor.transform.position;
                d.y = 0f;
                if (d.sqrMagnitude < best) { best = d.sqrMagnitude; nearest = piece; }
            }
        }
        if (nearest != null) nearest.Room.TryTurn(nearest);
    }

    // Called by MirrorResetPlate.
    public void OnRoomReset(MirrorRoom room)
    {
        if (state != State.Playing) return;
        Show("MIRRORS RESET");
    }

    private void OnRoomSolved(MirrorRoom room)
    {
        if (presetting || !isActiveAndEnabled) return;
        actor.SetRespawnPoint(actor.transform.position, actor.transform.rotation);
        RunRecord.Player player = RunRecord.Current(gateway);
        int solved = SolvedCount();
        bool last = solved >= PlayedRooms;
        Show(last ? "THE WAY UP IS OPEN!" : "THE DOOR OPENS!");
        Debug.Log($"[Mirror] {room.name} solved by {player.Name} ({solved}/{PlayedRooms})", this);
        if (usesPersistentGateway)
            gateway.ReportRunEvent("checkpoint", last
                ? $"{player.Name} 마지막 수정을 밝힘! 위로 가는 길이 열렸습니다"
                : $"{player.Name} 거울 방 {solved}/{PlayedRooms} 통과!");
    }

    private void Show(string text)
    {
        message = text;
        messageLeft = 2.2f;
    }

    // ---------- treasure and clear ----------

    // Called by MirrorTreasure.
    public void OnTreasureReached(MirrorTreasure chest)
    {
        if (state != State.Playing || handoff.Paused || IntroPlaying) return;
        state = State.Treasure;
        timer = treasureDelay;
        chest.Open();
    }

    private void Clear()
    {
        RunRecord.Player player = RunRecord.NoteClear(gateway, stageName);
        nextScene = StageSceneLoader.NextStage(gameObject.scene.name, "");
        if (StageSceneLoader.CanLoad(nextScene))
        {
            state = State.Advancing;
            advanceLeft = nextStageDelay;
            if (usesPersistentGateway)
                gateway.ReportRunEvent("stage_cleared", $"{player.Name}님이 {stageName} 클리어! 다음 스테이지로 이동합니다");
            return;
        }
        StageSceneLoader.CompleteRegion(gameObject.scene.name, nextScene);
        FinishRun(player);
    }

    private void FinishRun(RunRecord.Player player)
    {
        state = State.Finished;
        RunRecord.Finish();
        resultLines = RunRecord.SummaryLines();
        Debug.Log("[Mirror] REGION CLEAR, last: " + player.Name, this);
        if (usesPersistentGateway)
        {
            if (gateway.HasAuthority) gateway.EndRunControl();
            gateway.ReportRunEvent("run_result", $"무덤 아래의 보물을 찾았다! 지역 클리어 · 마지막: {player.Name}", resultLines);
        }
    }

    private void OnFell()
    {
        RunRecord.Player player = RunRecord.NoteFall(gateway);
        if (usesPersistentGateway) gateway.ReportRunEvent("fell", $"{player.Name} 추락! 다시 시작");
    }

    // ---------- handoff ----------

    private void OnRemotePreparing()
    {
        if (state == State.Finished || state == State.Advancing)
        {
            gateway.ReclaimControl();
            return;
        }
        handoff.OnPreparing();
    }

    private void OnRemoteStarted()
    {
        if (!handoff.Paused || !gateway.IsControlling || !inputRouter.IsRemoteControl)
        {
            gateway.ReclaimControl();
            return;
        }
        handoff.OnStarted();
        Debug.Log("[Mirror] 원격 조작 시작: " + gateway.PlayerName, this);
    }

    private void OnRemoteEnded()
    {
        if (state == State.Finished || state == State.Advancing) handoff.Reset();
        else handoff.OnEnded();
        inputRouter.UseLocalInput();
    }

    // ---------- status and result ----------

    private void SetStatus(string text)
    {
        statusLabel = text;
        if (statusText != null && statusText.text != text) statusText.text = text;
    }

    private void OnGUI()
    {
        if (!initialized) return;
        EnsureStyles();
        if (statusText == null && !string.IsNullOrEmpty(statusLabel))
            GUI.Label(new Rect(0, 20, Screen.width, 70), statusLabel, labelStyle);
        if (state == State.Playing && !handoff.Paused)
            GUI.Label(new Rect(0, HintY(), Screen.width, 30),
                "WALK INTO A MIRROR: PUSH  ·  JUMP NEXT TO IT: TURN  ·  BLUE PLATE: RESET", hintStyle);
        if (state == State.Playing && !handoff.Paused) DrawTargetMarker();
        if (state != State.Finished || resultLines == null) return;

        float width = Mathf.Min(720f, Screen.width - 40f);
        float height = 150f + resultLines.Length * 30f;
        var panel = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);
        GUI.Box(panel, GUIContent.none);
        GUI.Box(panel, GUIContent.none);
        GUI.Label(new Rect(panel.x, panel.y + 14, width, 44), "REGION CLEAR!", titleStyle);
        float y = panel.y + 66;
        foreach (string line in resultLines)
        {
            GUI.Label(new Rect(panel.x + 24, y, width - 48, 28), line, lineStyle);
            y += 30f;
        }
        float buttonWidth = (width - 72f) / 2f;
        if (GUI.Button(new Rect(panel.x + 24, panel.yMax - 62, buttonWidth, 44), "MAP (지도)", buttonStyle))
            StageSceneLoader.LoadMap(this, this, newGameSceneName);
        if (GUI.Button(new Rect(panel.x + 48 + buttonWidth, panel.yMax - 62, buttonWidth, 44), "LOBBY", buttonStyle))
            StageSceneLoader.Load(lobbySceneName, this, this);
    }

    // The controls line sits just above the scene's bottom buttons (RETRY / LOBBY …), wherever
    // the canvas scaler puts them; rechecked twice a second.
    private float hintY = -1f, hintCheckAt;

    private float HintY()
    {
        if (hintY >= 0f && Time.unscaledTime < hintCheckAt) return hintY;
        hintCheckAt = Time.unscaledTime + 0.5f;
        float top = 0f;   // highest bottom-area button edge, in pixels from the bottom
        var corners = new Vector3[4];
        foreach (UnityEngine.UI.Selectable button in FindObjectsByType<UnityEngine.UI.Selectable>(FindObjectsSortMode.None))
        {
            if (!button.isActiveAndEnabled || button.gameObject.scene != gameObject.scene) continue;
            Canvas canvas = button.GetComponentInParent<Canvas>();
            if (canvas == null || !canvas.isActiveAndEnabled) continue;
            Camera cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            ((RectTransform)button.transform).GetWorldCorners(corners);
            float low = float.MaxValue, high = float.MinValue;
            foreach (Vector3 corner in corners)
            {
                float y = RectTransformUtility.WorldToScreenPoint(cam, corner).y;
                low = Mathf.Min(low, y);
                high = Mathf.Max(high, y);
            }
            if (low < Screen.height * 0.3f) top = Mathf.Max(top, high);
        }
        hintY = Screen.height - Mathf.Max(16f, top + 10f) - 30f;
        return hintY;
    }

    // Screen marker over the crystal of the room being played (kept on the screen edge when the
    // crystal is off screen), so the goal is clear in the video too.
    private void DrawTargetMarker()
    {
        MirrorTarget goal = null;
        for (int i = 0; i < PlayedRooms && goal == null; i++)
            if (rooms[i] != null && !rooms[i].Solved) goal = rooms[i].Target;
        Camera view = followCamera != null ? followCamera.GetComponent<Camera>() : null;
        if (view == null) view = Camera.main;
        if (goal == null || view == null) return;

        Vector3 p = view.WorldToScreenPoint(goal.transform.position + Vector3.up * 2.3f);
        bool behind = p.z < 0f;
        Vector2 at = new Vector2(p.x, Screen.height - p.y);
        if (behind) at = new Vector2(Screen.width - at.x, Screen.height - at.y);
        const float margin = 60f;
        bool clamped = behind || at.x < margin || at.x > Screen.width - margin || at.y < margin || at.y > Screen.height - margin;
        at.x = Mathf.Clamp(at.x, margin, Screen.width - margin);
        at.y = Mathf.Clamp(at.y, margin + 40f, Screen.height - margin - 30f);

        float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 5f);
        Color old = GUI.color;
        GUI.color = new Color(0.4f, 0.95f, 1f, 0.65f + 0.35f * pulse);
        string arrow = !clamped ? "V" : at.x <= margin ? "<" : at.x >= Screen.width - margin ? ">" : at.y <= margin + 40f ? "^" : "V";
        GUI.Label(new Rect(at.x - 80f, at.y - 44f - pulse * 6f, 160f, 26f), "LIGHT HERE", markerStyle);
        GUI.Label(new Rect(at.x - 80f, at.y - 20f - pulse * 6f, 160f, 30f), arrow, markerStyle);
        GUI.color = old;
    }

    private void EnsureStyles()
    {
        if (titleStyle != null) return;
        titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 34, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        titleStyle.normal.textColor = new Color(1f, 0.85f, 0.2f);
        lineStyle = new GUIStyle(GUI.skin.label) { fontSize = 19 };
        lineStyle.normal.textColor = Color.white;
        buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 18, fontStyle = FontStyle.Bold };
        labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 28, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        labelStyle.normal.textColor = Color.white;
        hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        hintStyle.normal.textColor = new Color(0.75f, 0.95f, 1f);
        markerStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        markerStyle.normal.textColor = Color.white;
    }

    private T FindInScene<T>() where T : Component
    {
        foreach (T candidate in FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (candidate.gameObject.scene == gameObject.scene) return candidate;
        return null;
    }
}
