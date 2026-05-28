using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Handles animation parameters for the local player.
/// Attach this to the same GameObject as MainPlayerScript.
///
/// Animator Parameters required:
///   - Speed    (Float)   → drive Blend Tree: 0 = Idle, 0.5 = Walk, 1 = Sprint
///   - Attack   (Trigger) → one-shot attack animation
///   - Grounded (Bool)    → true when on the ground
/// </summary>
[RequireComponent(typeof(Animator))]
public class PlayerAnimationController : NetworkBehaviour
{
    [Header("References")]
    [Tooltip("Assign the Animator on the character's visual mesh (child object)")]
    public Animator animator;

    [Header("Blend Tree Smoothing")]
    [Tooltip("How fast Speed blends between values. Higher = snappier.")]
    public float speedDampTime = 0.1f;

    // Cached parameter hashes for performance
    private static readonly int SpeedHash    = Animator.StringToHash("SpeedHash");
    private static readonly int AttackHash   = Animator.StringToHash("AttackHash");

    // Internal references
    private MainPlayerScript _playerScript;
    private Rigidbody _rb;

    private void Awake()
    {
        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        _playerScript = GetComponent<MainPlayerScript>();
        _rb = GetComponent<Rigidbody>();
    }

    private void Update()
    {
        // Only drive animation on the local owner
        if (!IsOwner) return;
        if (animator == null) return;

        UpdateMovementBlend();
    }

    // ─── Movement Blend Tree ──────────────────────────────────────────────────

    private void UpdateMovementBlend()
    {
        if (_rb == null) return;

        Vector3 horizontalVel = new Vector3(_rb.velocity.x, 0f, _rb.velocity.z);
        float currentSpeed = horizontalVel.magnitude;

        // Normalise against sprint speed so the Blend Tree always goes 0 → 1
        float normalised = Mathf.Clamp01(currentSpeed / _playerScript.sprintSpeed);

        animator.SetFloat(SpeedHash, normalised, speedDampTime, Time.deltaTime);
    }


    // ─── Attack ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Call this from MainPlayerScript inside AttemptAttack() to fire the trigger.
    /// </summary>
    public void TriggerAttack()
    {
        if (!IsOwner || animator == null) return;
        animator.SetTrigger(AttackHash);
    }
}
