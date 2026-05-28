using UnityEngine;
using Unity.Netcode;
using UnityEngine.InputSystem;

public class PropTauntSkill : NetworkBehaviour
{
    [Header("Settings")]
    public float cooldownSeconds = 1f;
    public float autoTauntInterval = 60f;

    public AudioSource tauntAudioSource;

    private float _lastTauntTime = -999f;
    private float _autoTauntTimer = 0f;

    private void Update()
    {
        if (!IsOwner) return;

        _autoTauntTimer += Time.deltaTime;
        if (_autoTauntTimer >= autoTauntInterval)
        {
            _autoTauntTimer = 0f;
            TryTaunt();
        }
    }

    public void OnTaunt(InputAction.CallbackContext context)
    {
        if (!IsOwner) return;
        if (!context.performed) return;
        TryTaunt();
    }

    private void TryTaunt()
    {
        if (Time.time - _lastTauntTime < cooldownSeconds) return;
        _lastTauntTime = Time.time;
        UseTauntServerRpc();
    }

    [ServerRpc]
    private void UseTauntServerRpc()
    {
        PlayTauntEffectClientRpc();
    }

    [ClientRpc]
    private void PlayTauntEffectClientRpc()
    {
        if (tauntAudioSource != null)
            tauntAudioSource.Play();
    }
}
/*
public NetworkVariable<int> mana = new NetworkVariable<int>(
    100,
    NetworkVariableReadPermission.Everyone,
    NetworkVariableWritePermission.Server
);

// [เพิ่มใหม่] ตัวแปรสำหรับตั้งค่าเครื่องเล่นเสียง
public AudioSource tauntAudioSource;

public void OnTaunt(InputAction.CallbackContext context)
{
    if (!IsOwner) return;
    if (!context.performed) return;

    Debug.Log("[Local] Taunt button pressed. Sending request to Server...");
    UseTauntServerRpc();
}

[ServerRpc]
private void UseTauntServerRpc(ServerRpcParams rpcParams = default)
{
    int manaCost = 20;

    if (mana.Value >= manaCost)
    {
        mana.Value -= manaCost;
        Debug.Log($"[Server] Approved! Mana deducted. Current Mana: {mana.Value}. Sending ClientRpc...");
        PlayTauntEffectClientRpc();
    }
    else
    {
        Debug.Log("[Server] Request denied! Not enough mana.");
    }
}

[ClientRpc]
private void PlayTauntEffectClientRpc()
{
    Debug.Log($"[Client] Hahaha! Player {OwnerClientId} is taunting! (Current Mana: {mana.Value})");

    // [เพิ่มใหม่] สั่งให้เล่นเสียงเมื่อ ClientRpc ทำงาน
    if (tauntAudioSource != null)
    {
        tauntAudioSource.Play();
    }
}
}*/