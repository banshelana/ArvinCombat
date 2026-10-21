using UnityEngine;

namespace ArvinCombat
{
    /// <summary>
    /// Everything a fighter wants to do this frame. Fighters only read this struct,
    /// so a local player, the AI, or (later) a network player can drive them the same way.
    /// </summary>
    public struct FighterInput
    {
        public Vector3 move;      // world-space direction, length 0..1
        public bool attack;       // left mouse pressed this frame
        public bool kick;         // Q pressed this frame
        public AttackDir dir;     // which move: A = Left, D = Right, W = Front, S = Back (none = Front)
        public bool special;      // pressed this frame
        public bool dodge;        // pressed this frame
        public bool block;        // held
        public bool sprint;       // held (AI only; players run automatically)
        public bool jump;         // pressed this frame
    }

    public interface IFighterBrain
    {
        FighterInput GetInput(Fighter self, float dt);
    }

    /// <summary>
    /// Controls:
    ///   WASD               move (runs automatically when closing in on the enemy)
    ///   Left click + A/D/W/S   left / right / front / back attack of the warrior's main set
    ///   Q          + A/D/W/S   left / right / front / back kick (or the warrior's own Q set)
    ///   (no direction key = front move)
    ///   Right hold         block (press just before a hit = parry)
    ///   Right double       dodge roll (direction of WASD, backwards if none)
    ///   Left + Right       special (when the blue bar is full)
    ///   Space              jump (attack in the air = jump attack)
    /// </summary>
    public class PlayerBrain : IFighterBrain
    {
        public Transform cameraTransform;
        public float doubleClickTime = 0.28f;

        float _lastRight = -9f;

        public static AttackDir DirFromKeys()
        {
            bool a = Input.GetKey(KeyCode.A), d = Input.GetKey(KeyCode.D);
            bool w = Input.GetKey(KeyCode.W), s = Input.GetKey(KeyCode.S);
            if (a && !d) return AttackDir.Left;
            if (d && !a) return AttackDir.Right;
            if (s && !w) return AttackDir.Back;
            return AttackDir.Front;
        }

        public FighterInput GetInput(Fighter self, float dt)
        {
            var i = new FighterInput();
            float h = 0, v = 0;
            if (Input.GetKey(KeyCode.A)) h -= 1;
            if (Input.GetKey(KeyCode.D)) h += 1;
            if (Input.GetKey(KeyCode.S)) v -= 1;
            if (Input.GetKey(KeyCode.W)) v += 1;

            Vector3 fwd = Vector3.forward, right = Vector3.right;
            if (cameraTransform != null)
            {
                fwd = cameraTransform.forward; fwd.y = 0; fwd.Normalize();
                right = cameraTransform.right; right.y = 0; right.Normalize();
            }
            i.move = Vector3.ClampMagnitude(fwd * v + right * h, 1f);
            i.jump = Input.GetKeyDown(KeyCode.Space);
            i.dir = DirFromKeys();

            float now = Time.unscaledTime;
            bool lDown = Input.GetMouseButtonDown(0), rDown = Input.GetMouseButtonDown(1);
            bool lHeld = Input.GetMouseButton(0), rHeld = Input.GetMouseButton(1);

            if ((lDown && rHeld) || (rDown && lHeld))
            {
                // both buttons: special when ready, otherwise a normal attack (counter from block)
                if (self.Special01 >= 1f) i.special = true; else i.attack = true;
                _lastRight = -9f;
            }
            else
            {
                if (lDown) i.attack = true;
                if (rDown)
                {
                    if (now - _lastRight <= doubleClickTime) { i.dodge = true; _lastRight = -9f; }
                    else _lastRight = now;
                }
            }
            if (Input.GetKeyDown(KeyCode.Q)) i.kick = true;
            i.block = rHeld && !lHeld;
            return i;
        }
    }

    /// <summary>Does nothing (used before a round starts or for a dummy target).</summary>
    public class IdleBrain : IFighterBrain
    {
        public FighterInput GetInput(Fighter self, float dt) => new FighterInput();
    }
}
