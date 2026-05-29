using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public enum RoundPhase
{
    Lobby = 0,
    MonsterPreview = 1,
    SurvivorHide = 2,
    Active = 3,
    Ended = 4
}

[RequireComponent(typeof(NetworkObject))]
public class RoundManager : NetworkBehaviour
{
    public static RoundManager Instance { get; private set; }

    [Header("Phase Durations (seconds)")]
    public int previewDurationSec = 30;
    public int hideDurationSec = 30;

    [Header("Monster Lock Position")]
    public Vector3 monsterLockPosition = new Vector3(0f, -10f, 0f);

    [Header("Survivor Lock Position")]
    [Tooltip("ตำแหน่งที่จะ teleport Survivor ทุกคนไปกักไว้ระหว่าง MonsterPreview (เหมือนกับที่ Monster ถูกกักระหว่าง SurvivorHide)")]
    public Vector3 survivorLockPosition = new Vector3(0f, -20f, 0f);

    public NetworkVariable<RoundPhase> CurrentPhase = new NetworkVariable<RoundPhase>(
        RoundPhase.Lobby,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public NetworkVariable<int> PhaseTimer = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public NetworkVariable<NetworkObjectReference> MonsterPlayerRef = new NetworkVariable<NetworkObjectReference>(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private Vector3 _monsterOriginalSpawn = Vector3.zero;
    private Quaternion _monsterOriginalRotation = Quaternion.identity;

    // FIX #1: ใช้ Dictionary เก็บ spawn position ของ Survivor แต่ละคน
    // ตำแหน่งจะถูกขอจาก client โดยตรงผ่าน ServerRpc เพื่อหลีกเลี่ยงปัญหา
    // ClientNetworkTransform stale position บน server ในช่วงที่มี Relay latency
    private Dictionary<ulong, Vector3> _survivorOriginalSpawns = new Dictionary<ulong, Vector3>();

    private Coroutine _tickCoroutine;
    private bool _subscribedToGameTimer = false;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        Debug.Log("[RoundManager] Awake — Instance set.");
    }

    public override void OnNetworkSpawn()
    {
        Debug.Log($"[RoundManager] OnNetworkSpawn (IsServer={IsServer}).");
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer)
        {
            if (_tickCoroutine != null) { StopCoroutine(_tickCoroutine); _tickCoroutine = null; }
            UnsubscribeFromGameTimer();
        }
    }

    public void BeginRound()
    {
        Debug.Log($"[RoundManager] BeginRound called (IsServer={IsServer}).");
        if (!IsServer) { Debug.LogWarning("[RoundManager] BeginRound on non-server, ignored."); return; }
        if (CurrentPhase.Value != RoundPhase.Lobby)
        {
            Debug.LogWarning($"[RoundManager] BeginRound but phase already {CurrentPhase.Value}. Ignored.");
            return;
        }

        NetworkObject monsterObj = null;
        int connectedCount = 0;
        foreach (var kv in NetworkManager.Singleton.ConnectedClients)
        {
            connectedCount++;
            var po = kv.Value.PlayerObject;
            if (po == null) { Debug.LogWarning($"[RoundManager] Client {kv.Key} has no PlayerObject."); continue; }
            var state = po.GetComponent<PlayerStateSync>();
            int role = state != null ? state.RoleIndex.Value : -1;
            Debug.Log($"[RoundManager] Scanning client {kv.Key}: role={role}");
            if (state != null && state.RoleIndex.Value == 1)
            {
                monsterObj = po;
                break;
            }
        }
        Debug.Log($"[RoundManager] Connected clients scanned: {connectedCount}. monsterObj={(monsterObj != null ? monsterObj.name : "NULL")}");

        if (LobbyManager.Instance != null)
            LobbyManager.Instance.IsGameStarted.Value = true;

        if (monsterObj == null)
        {
            Debug.LogWarning("[RoundManager] No monster found. Jumping straight to Active.");
            MonsterPlayerRef.Value = default;
            TransitionTo(RoundPhase.Active);
            return;
        }

        _monsterOriginalSpawn = monsterObj.transform.position;
        _monsterOriginalRotation = monsterObj.transform.rotation;
        MonsterPlayerRef.Value = new NetworkObjectReference(monsterObj);

        Debug.Log($"[RoundManager] Monster found: clientId={monsterObj.OwnerClientId} originalSpawn={_monsterOriginalSpawn}");

        // FIX #1: ล้าง dict แล้วเริ่ม coroutine ขอ position จาก Survivor ทุกคนก่อน
        // แทนที่จะอ่าน po.transform.position บน server โดยตรง (ซึ่งอาจ stale กับ Relay)
        _survivorOriginalSpawns.Clear();
        StartCoroutine(CollectSpawnsAndTransition());
    }

    // FIX #1: ขอ position จาก Survivor ทุกคนผ่าน ClientRpc → ServerRpc
    // แล้วค่อย transition ไป MonsterPreview หลังได้ครบ (หรือ timeout 2 วินาที)
    private IEnumerator CollectSpawnsAndTransition()
    {
        int survivorCount = CountSurvivors();
        Debug.Log($"[RoundManager] Collecting spawn positions from {survivorCount} survivors...");

        if (survivorCount > 0)
        {
            CollectSurvivorPositionsClientRpc();

            float deadline = Time.time + 2f;
            while (_survivorOriginalSpawns.Count < survivorCount && Time.time < deadline)
                yield return null;

            Debug.Log($"[RoundManager] Collected {_survivorOriginalSpawns.Count}/{survivorCount} survivor positions. Transitioning.");
        }

        TransitionTo(RoundPhase.MonsterPreview);
    }

    // Server → ทุก client: ให้ Survivor report position ของตัวเองกลับมา
    [ClientRpc]
    private void CollectSurvivorPositionsClientRpc()
    {
        var myPlayer = NetworkManager.Singleton.LocalClient?.PlayerObject;
        if (myPlayer == null) return;

        var state = myPlayer.GetComponent<PlayerStateSync>();
        if (state == null || state.RoleIndex.Value != 0) return; // เฉพาะ Survivor เท่านั้น

        ReportMyPositionServerRpc(myPlayer.transform.position);
    }

    // Survivor client → Server: ส่ง position ที่ถูกต้องมาเก็บไว้
    [ServerRpc(RequireOwnership = false)]
    private void ReportMyPositionServerRpc(Vector3 pos, ServerRpcParams rpcParams = default)
    {
        ulong senderId = rpcParams.Receive.SenderClientId;
        _survivorOriginalSpawns[senderId] = pos;
        Debug.Log($"[RoundManager] Received spawn position from survivor clientId={senderId}: {pos}");
    }

    // นับจำนวน Survivor ที่ connected อยู่
    private int CountSurvivors()
    {
        int count = 0;
        foreach (var kv in NetworkManager.Singleton.ConnectedClients)
        {
            var state = kv.Value.PlayerObject?.GetComponent<PlayerStateSync>();
            if (state != null && state.RoleIndex.Value == 0) count++;
        }
        return count;
    }

    private void TransitionTo(RoundPhase next)
    {
        if (!IsServer) return;

        RoundPhase prev = CurrentPhase.Value;
        Debug.Log($"[RoundManager] >>> Transition: {prev} -> {next}");

        if (prev == RoundPhase.Active) UnsubscribeFromGameTimer();

        CurrentPhase.Value = next;

        if (_tickCoroutine != null) { StopCoroutine(_tickCoroutine); _tickCoroutine = null; }

        switch (next)
        {
            case RoundPhase.MonsterPreview:
                PhaseTimer.Value = previewDurationSec;
                Debug.Log($"[RoundManager] Starting MonsterPreview tick: PhaseTimer={PhaseTimer.Value}. Teleporting survivors to {survivorLockPosition}.");
                TeleportAllSurvivors(survivorLockPosition, locked: true);
                _tickCoroutine = StartCoroutine(TickPhaseTimer(RoundPhase.SurvivorHide));
                break;

            case RoundPhase.SurvivorHide:
                PhaseTimer.Value = hideDurationSec;
                Debug.Log($"[RoundManager] Starting SurvivorHide tick: PhaseTimer={PhaseTimer.Value}. Teleporting monster to {monsterLockPosition}. Warping survivors back to spawn.");
                TeleportMonster(monsterLockPosition, locked: true);
                TeleportAllSurvivorsToSpawns();
                _tickCoroutine = StartCoroutine(TickPhaseTimer(RoundPhase.Active));
                break;

            case RoundPhase.Active:
                PhaseTimer.Value = 0;
                Debug.Log($"[RoundManager] Entering Active. Teleporting monster back to {_monsterOriginalSpawn}.");
                TeleportMonster(_monsterOriginalSpawn, locked: false);
                SubscribeToGameTimer();
                break;

            case RoundPhase.Ended:
                PhaseTimer.Value = 0;
                UnsubscribeFromGameTimer();
                Debug.Log("[RoundManager] Round ended.");
                break;
        }
    }

    private IEnumerator TickPhaseTimer(RoundPhase nextPhaseWhenZero)
    {
        Debug.Log($"[RoundManager] TickPhaseTimer coroutine STARTED. PhaseTimer={PhaseTimer.Value} -> goal {nextPhaseWhenZero}");
        while (PhaseTimer.Value > 0)
        {
            yield return new WaitForSeconds(1f);
            PhaseTimer.Value = Mathf.Max(0, PhaseTimer.Value - 1);
            if (PhaseTimer.Value % 5 == 0)
                Debug.Log($"[RoundManager] PhaseTimer tick = {PhaseTimer.Value}");
        }
        Debug.Log($"[RoundManager] TickPhaseTimer reached 0 -> Transition({nextPhaseWhenZero})");
        TransitionTo(nextPhaseWhenZero);
    }

    private void TeleportMonster(Vector3 pos, bool locked)
    {
        if (!MonsterPlayerRef.Value.TryGet(out NetworkObject monsterObj))
        {
            Debug.LogWarning("[RoundManager] TeleportMonster: monster ref invalid.");
            return;
        }
        var receiver = monsterObj.GetComponent<PlayerPowerupReceiver>();
        if (receiver == null)
        {
            Debug.LogWarning("[RoundManager] TeleportMonster: monster has no PlayerPowerupReceiver component.");
            return;
        }
        var rpcParams = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { monsterObj.OwnerClientId } }
        };
        receiver.TeleportAndLockClientRpc(pos, locked, rpcParams);
    }

    // Teleport Survivor ทุกคนไปที่ตำแหน่งที่กำหนด (locked=true = kinematic ล็อกไม่ให้ขยับ)
    private void TeleportAllSurvivors(Vector3 pos, bool locked)
    {
        foreach (var kv in NetworkManager.Singleton.ConnectedClients)
        {
            var po = kv.Value.PlayerObject;
            if (po == null) continue;
            var state = po.GetComponent<PlayerStateSync>();
            if (state == null || state.RoleIndex.Value != 0) continue;

            var receiver = po.GetComponent<PlayerPowerupReceiver>();
            if (receiver == null)
            {
                Debug.LogWarning($"[RoundManager] TeleportAllSurvivors: client {kv.Key} has no PlayerPowerupReceiver.");
                continue;
            }
            var rpcParams = new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { po.OwnerClientId } }
            };
            receiver.TeleportAndLockClientRpc(pos, locked, rpcParams);
            Debug.Log($"[RoundManager] TeleportAllSurvivors: clientId={kv.Key} -> {pos} locked={locked}");
        }
    }

    // Warp Survivor แต่ละคนกลับไปที่ตำแหน่ง spawn ต้นฉบับที่รับมาจาก client และ unlock
    private void TeleportAllSurvivorsToSpawns()
    {
        foreach (var kv in NetworkManager.Singleton.ConnectedClients)
        {
            var po = kv.Value.PlayerObject;
            if (po == null) continue;
            var state = po.GetComponent<PlayerStateSync>();
            if (state == null || state.RoleIndex.Value != 0) continue;

            var receiver = po.GetComponent<PlayerPowerupReceiver>();
            if (receiver == null)
            {
                Debug.LogWarning($"[RoundManager] TeleportAllSurvivorsToSpawns: client {kv.Key} has no PlayerPowerupReceiver.");
                continue;
            }

            // ใช้ตำแหน่งที่ client รายงานมาตอน BeginRound ถ้ามี ไม่งั้น fallback ตำแหน่งปัจจุบัน server-side
            Vector3 spawnPos = _survivorOriginalSpawns.TryGetValue(kv.Key, out var orig)
                ? orig
                : po.transform.position;

            var rpcParams = new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { po.OwnerClientId } }
            };
            receiver.TeleportAndLockClientRpc(spawnPos, false, rpcParams);
            Debug.Log($"[RoundManager] TeleportAllSurvivorsToSpawns: clientId={kv.Key} -> {spawnPos}");
        }
    }

    private void SubscribeToGameTimer()
    {
        if (!IsServer || _subscribedToGameTimer) return;
        if (GameTimeManager.Instance == null)
        {
            Debug.LogWarning("[RoundManager] SubscribeToGameTimer: GameTimeManager.Instance is null.");
            return;
        }
        GameTimeManager.Instance.GameTimer.OnValueChanged += OnGameTimerChanged;
        _subscribedToGameTimer = true;
    }

    private void UnsubscribeFromGameTimer()
    {
        if (!_subscribedToGameTimer) return;
        if (GameTimeManager.Instance != null)
            GameTimeManager.Instance.GameTimer.OnValueChanged -= OnGameTimerChanged;
        _subscribedToGameTimer = false;
    }

    private void OnGameTimerChanged(int oldValue, int newValue)
    {
        if (!IsServer) return;
        if (newValue <= 0 && CurrentPhase.Value == RoundPhase.Active)
        {
            Debug.Log("[RoundManager] Main timer expired -> survivors win.");
            TransitionTo(RoundPhase.Ended);
        }
    }
}
