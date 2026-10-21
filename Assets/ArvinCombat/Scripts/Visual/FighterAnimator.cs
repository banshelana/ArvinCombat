using UnityEngine;

namespace ArvinCombat
{
    /// <summary>
    /// Thin wrapper over the generated Animator controller.
    /// Locomotion is a 2D blend tree (MoveX / MoveZ); every action is its own state
    /// that code cross-fades into. No transitions are needed in the controller.
    /// </summary>
    public class FighterAnimator : MonoBehaviour
    {
        static readonly int MoveX = Animator.StringToHash("MoveX");
        static readonly int MoveZ = Animator.StringToHash("MoveZ");
        static readonly int MoveSpeed = Animator.StringToHash("MoveSpeed");
        static readonly int ActionSpeed = Animator.StringToHash("ActionSpeed");
        static readonly int LocomotionState = Animator.StringToHash("Locomotion");

        public Animator animator;
        public WarriorVisual visual;
        public string current = "Locomotion";
        // last values, sent to the other player in online mode
        public int playSeq;
        public float lastSpeed = 1f;
        public Vector2 lastBlend;
        public float lastMoveSpeed = 1f;

        public void Init(Animator anim, WarriorVisual vis)
        {
            animator = anim;
            visual = vis;
            if (animator != null)
            {
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
        }

        public bool Has(string key)
        {
            if (animator == null || visual == null) return false;
            if (!visual.Has(key) || animator.runtimeAnimatorController == null) return false;
            return animator.HasState(0, Animator.StringToHash(key)) ||
                   animator.HasState(0, Animator.StringToHash("Base Layer." + key));
        }

        public float Length(string key) => visual != null ? visual.GetLength(key) : 0.8f;

        public void SetLocomotion(Vector2 local, float speedMul, float dt)
        {
            lastBlend = local;
            lastMoveSpeed = speedMul;
            if (animator == null) return;
            animator.SetFloat(MoveX, local.x, 0.12f, dt);
            animator.SetFloat(MoveZ, local.y, 0.12f, dt);
            animator.SetFloat(MoveSpeed, speedMul);
        }

        /// <summary>Plays an action state. Returns its real duration in seconds (0 if missing).</summary>
        public float Play(string key, float speed = 1f, float fade = 0.08f, float startNormalized = 0f)
        {
            if (!Has(key)) return 0f;
            speed = Mathf.Max(0.05f, speed);
            animator.SetFloat(ActionSpeed, speed);
            if (key == current) animator.Play(key, 0, startNormalized); // same move again: restart it
            else animator.CrossFadeInFixedTime(key, fade, 0, startNormalized * Length(key));
            current = key;
            lastSpeed = speed;
            playSeq++;
            return Length(key) / speed;
        }

        public void ToLocomotion(float fade = 0.15f)
        {
            if (animator == null || current == "Locomotion") return;
            animator.SetFloat(ActionSpeed, 1f);
            animator.CrossFadeInFixedTime(LocomotionState, fade, 0);
            current = "Locomotion";
            lastSpeed = 1f;
            playSeq++;
        }

        public void SetActionSpeed(float s)
        {
            if (animator != null) animator.SetFloat(ActionSpeed, s);
        }
    }
}
