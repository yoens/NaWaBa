using System.Collections.Generic;
using TMPro;
using UnityEngine;

// Stage 4 (inside the castle): trap corridors. Walk through the castle to the exit past
// swinging axes, saws, floor spikes and arrow traps. A hit sends the player back to the
// last checkpoint (Hard: to the entrance). Control can change hands any time; all traps
// freeze during the handoff. Reaching the exit ends the run (or moves on to the next stage).
[DefaultExecutionOrder(-100)]
public class KeepStageController : MonoBehaviour, IStageFlow
{
    [Header("Connections (auto-found when empty)")]
    [SerializeField] private ClimbActorController actor;
    [SerializeField] private InputRouter inputRouter;
    [SerializeField] private RemoteInputGateway gateway;
    [SerializeField] private TMP_Text statusText;

    [Header("Web display")]
    [SerializeField] private string stageId = "keep";
    [SerializeField] private string stageName = "4 · 성 안 함정 복도";

    [Header("Rules (difficulty overrides trap speed and respawn)")]
    [SerializeField, Range(0.3f, 3f)] private float trapSpeed = 1f;
    [SerializeField] private bool respawnAtEntrance;            // off = last checkpoint
    [SerializeField, Min(0f)] private float hitFreeze = 1f;     // seconds frozen after a hit
    [SerializeField, Min(0f)] private float safeTime = 1f;      // no damage right after respawn
    [SerializeField, Min(0f)] private float hostResumeDelay = 1.5f;
    [SerializeField] private bool playIntro = true;
    [SerializeField, Min(0f)] private float nextStageDelay = 3f;
    [Tooltip("Seconds of the treasure effect at the exit chest (TreasureChestFx) before the clear.")]
    [SerializeField, Min(0f)] private float treasureDelay = 2.5f;

    [Header("After the run")]
    [SerializeField] private string newGameSceneName = "Stage_Dodge";
    [SerializeField] private string lobbySceneName = "Lobby";

    private const string StageControls = "move,jump,look";

    private enum State { Intro, Playing, Hit, Treasure, Finished, Advancing }

    private State state = State.Intro;
    private StageHandoff handoff;
    private StageIntroCamera intro;
    private RemoteInputSource stageRemoteInputSource;
    private readonly List<TrapCheckpoint> checkpoints = new List<TrapCheckpoint>();
    private int progress;
    private float timer, safeLeft, advanceLeft;
    private bool initialized, usesPersistentGateway;
    private string nextScene = "";
    private string[] resultLines;
    private string statusLabel = "";
    private GUIStyle titleStyle, lineStyle, buttonStyle, labelStyle;

    private bool IsSinglePlayer => GameSession.Mode == GameMode.SinglePlayer;
    private bool IntroPlaying => intro != null && intro.IsPlaying;

    private void Awake()
    {
        if (actor == null) actor = FindInScene<ClimbActorController>();
        if (inputRouter == null) inputRouter = FindInScene<InputRouter>();
        if (gateway == null) gateway = FindInScene<RemoteInputGateway>();
        if (actor == null || inputRouter == null)
        {
            Debug.LogError("KeepStageController: ClimbActorController와 InputRouter가 필요합니다.", this);
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
                Debug.LogError("KeepStageController: 멀티플레이 세션을 찾을 수 없습니다. 로비에서 MULTI PLAY로 시작하세요.", this);
                usesPersistentGateway = false;
                gateway = null;
            }
        }

        NaWaBaDifficulty difficulty = GameSession.Difficulty;
        if (difficulty != null)
        {
            trapSpeed = difficulty.TrapSpeed;
            respawnAtEntrance = !difficulty.UseCheckpoints;
            if (!string.IsNullOrEmpty(difficulty.WebLabel) && !stageName.Contains("("))
                stageName = $"{stageName} ({difficulty.WebLabel})";
        }

        foreach (TrapCheckpoint checkpoint in FindObjectsByType<TrapCheckpoint>(FindObjectsSortMode.None))
            if (checkpoint.gameObject.scene == gameObject.scene) checkpoints.Add(checkpoint);
        checkpoints.Sort((a, b) => a.Progress.CompareTo(b.Progress));
        if (FindInScene<KeepGoal>() == null)
            Debug.LogError("KeepStageController: 출구(KeepGoal)가 없습니다. " +
                "'NaWaBa > 성 내부 만들기 (Stage_Keep)'를 실행하고 씬을 저장하세요.", this);

        // Per-cell floor/wall boxes snag the player's box collider at their seams; merge them
        // unless the scene already has them merged (menu "NaWaBa/성 내부 충돌 합치기").
        // (Found through the exit: during a scene load Awake runs before scene.isLoaded is true.)
        KeepGoal exit = FindInScene<KeepGoal>();
        Transform keep = exit != null ? exit.transform.root : null;
        if (keep == null || keep.name != KeepCollision.RootName)
            Debug.LogWarning("[Keep] \"" + KeepCollision.RootName + "\" not found - collision boxes not merged.", this);
        else if (KeepCollision.IsMerged(keep))
            Debug.Log("[Keep] collision boxes already merged in the scene", this);
        else
        {
            KeepCollision.Result merged = KeepCollision.Merge(keep);
            Debug.Log($"[Keep] merged collision boxes {merged.before} -> {merged.after} (skipped {merged.skipped})", this);
        }
        KeepStuckProbe probe = actor.GetComponent<KeepStuckProbe>();
        if (probe == null) probe = actor.gameObject.AddComponent<KeepStuckProbe>();
        probe.Configure(inputRouter);

        TrapClock.Time = 0f;
        TrapClock.Delta = 0f;
        if (gateway != null) gateway.AllowRemoteApproval = false;
        handoff = new StageHandoff(usesPersistentGateway ? gateway : null, hostResumeDelay);
        initialized = true;
    }

    private void OnEnable()
    {
        if (!initialized) return;
        state = State.Intro;
        resultLines = null;
        progress = 0;
        actor.ClearRespawnPoint();
        foreach (TrapCheckpoint checkpoint in checkpoints) checkpoint.MarkReached(false);
        handoff.Reset();
        actor.InputLocked = false;
        actor.Fell += OnFell;

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
                Debug.LogError("KeepStageController: 스테이지 입력을 멀티플레이 세션에 연결하지 못했습니다.", this);
                usesPersistentGateway = false;
            }
        }
        if (!usesPersistentGateway) inputRouter.UseLocalInput();

        actor.Frozen = true;
        ClimbCameraController followCamera = FindInScene<ClimbCameraController>();
        intro = playIntro ? StageIntroCamera.For(followCamera) : null;
        if (intro != null) intro.Play(IntroRoute(), followCamera, OnIntroFinished);
        else OnIntroFinished();
    }

    private void OnDisable()
    {
        TrapClock.Delta = 0f;
        if (actor != null) actor.Fell -= OnFell;
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

    // Exit -> checkpoints (far to near) -> entrance.
    private List<Vector3> IntroRoute()
    {
        var route = new List<Vector3>();
        KeepGoal goal = FindInScene<KeepGoal>();
        if (goal != null) route.Add(goal.transform.position);
        for (int i = checkpoints.Count - 1; i >= 0; i--) route.Add(checkpoints[i].RespawnPosition);
        route.Add(actor.transform.position);
        return route;
    }

    private void OnIntroFinished()
    {
        if (!isActiveAndEnabled || state != State.Intro) return;
        state = State.Playing;
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
        bool running = !paused && !IntroPlaying && !done;
        actor.Frozen = paused || IntroPlaying || done || state == State.Hit || state == State.Treasure;

        // Traps keep moving while the player is knocked down, but stop for handoffs.
        TrapClock.Delta = running ? dt * trapSpeed : 0f;
        TrapClock.Time += TrapClock.Delta;

        if (paused)
        {
            SetStatus(handoff.Label);
            return;
        }
        if (running) RunRecord.AddControlTime(gateway, dt);
        if (safeLeft > 0f) safeLeft -= dt;

        switch (state)
        {
            case State.Intro:
                SetStatus("REACH THE EXIT!\n(SPACE: SKIP)");
                break;
            case State.Playing:
                SetStatus(progress > 0 ? $"CHECKPOINT {CheckpointNumber()}/{checkpoints.Count}" : stageName);
                break;
            case State.Hit:
                timer -= dt;
                SetStatus("OUCH!");
                if (timer <= 0f) Respawn();
                break;
            case State.Treasure:
                timer -= dt;
                SetStatus("TREASURE!");
                if (timer <= 0f) ClearStage();
                break;
            case State.Advancing:
                advanceLeft -= dt;
                SetStatus(advanceLeft > 0f ? $"CLEAR!\nNEXT STAGE IN {Mathf.CeilToInt(advanceLeft)}" : "LOADING...");
                if (advanceLeft <= 0f && !StageSceneLoader.IsLoading &&
                    !StageSceneLoader.Load(nextScene, this, this, carryControl: true))
                    FinishRun(RunRecord.Current(gateway));
                break;
            case State.Finished:
                SetStatus("ALL CLEAR!");
                break;
        }
    }

    private int CheckpointNumber()
    {
        int number = 0;
        foreach (TrapCheckpoint checkpoint in checkpoints)
            if (checkpoint.Progress <= progress) number++;
        return number;
    }

    // ---------- traps, checkpoints, goal ----------

    // Called by TrapHazard when a trap touches the player.
    public void OnTrapHit(TrapHazard hazard)
    {
        if (state != State.Playing || handoff.Paused || IntroPlaying || safeLeft > 0f) return;
        RunRecord.Player player = RunRecord.NoteFail(gateway);
        state = State.Hit;
        timer = hitFreeze;
        Debug.Log($"[Keep] {player.Name} hit by {hazard.name}", this);
        if (usesPersistentGateway)
            gateway.ReportRunEvent("failed", respawnAtEntrance || progress == 0
                ? $"{player.Name} 함정에 걸림! 입구에서 다시 시작"
                : $"{player.Name} 함정에 걸림! 체크포인트 {CheckpointNumber()}에서 다시 시작");
    }

    private void Respawn()
    {
        if (respawnAtEntrance)
        {
            progress = 0;
            foreach (TrapCheckpoint checkpoint in checkpoints) checkpoint.MarkReached(false);
            actor.RespawnAtStart();
        }
        else actor.Respawn();
        safeLeft = safeTime;
        state = State.Playing;
    }

    // Called by TrapCheckpoint. True when it became the new respawn point.
    public bool ReachCheckpoint(TrapCheckpoint checkpoint)
    {
        if (state != State.Playing || respawnAtEntrance || checkpoint.Progress <= progress) return false;
        progress = checkpoint.Progress;
        actor.SetRespawnPoint(checkpoint.RespawnPosition, actor.transform.rotation);
        RunRecord.Player player = RunRecord.Current(gateway);
        Debug.Log($"[Keep] checkpoint {CheckpointNumber()} by {player.Name}", this);
        if (usesPersistentGateway)
            gateway.ReportRunEvent("checkpoint", $"{player.Name} 체크포인트 {CheckpointNumber()}/{checkpoints.Count} 도착!");
        return true;
    }

    // Called by KeepGoal.
    public void OnReachedGoal()
    {
        if (state != State.Playing || handoff.Paused) return;
        TreasureChestFx treasure = FindInScene<TreasureChestFx>();
        if (treasure != null && !treasure.IsOpen && treasureDelay > 0f)
        {
            // The treasure moment first (coins, light), then the clear.
            state = State.Treasure;
            timer = treasureDelay;
            treasure.Open();
            return;
        }
        ClearStage();
    }

    private void ClearStage()
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
        Debug.Log("[Keep] ALL CLEAR, last: " + player.Name, this);
        if (usesPersistentGateway)
        {
            if (gateway.HasAuthority) gateway.EndRunControl();
            gateway.ReportRunEvent("run_result", $"성을 돌파했다! 전체 클리어 · 마지막: {player.Name}", resultLines);
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
        Debug.Log("[Keep] 원격 조작 시작: " + gateway.PlayerName, this);
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
        if (state != State.Finished || resultLines == null) return;

        float width = Mathf.Min(720f, Screen.width - 40f);
        float height = 150f + resultLines.Length * 30f;
        var panel = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);
        GUI.Box(panel, GUIContent.none);
        GUI.Box(panel, GUIContent.none);
        GUI.Label(new Rect(panel.x, panel.y + 14, width, 44), "ALL CLEAR!", titleStyle);
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
    }

    private T FindInScene<T>() where T : Component
    {
        foreach (T candidate in FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (candidate.gameObject.scene == gameObject.scene) return candidate;
        return null;
    }
}
