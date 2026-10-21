using System;
using UnityEngine;

namespace ArvinCombat
{
    public enum FighterState { Frozen, Move, Attack, Block, Dodge, Jump, Recover, HitStun, Knockdown, GetUp, Stunned, Dead, Victory }

    /// <summary>
    /// The combat simulation for one fighter. It does not read the keyboard: a brain
    /// (player, AI, or later network) fills a FighterInput each frame.
    /// Movement is done without physics (simple + network friendly); the arena is a circle.
    /// </summary>
    public class Fighter : MonoBehaviour
    {
        public const float SpecialMax = 100f;
        public const float BodyRadius = 0.45f;
        public static float ArenaRadius = 9.5f;
        public const float Gravity = 22f;
        public const float JumpVelocity = 7f;
        public const float JumpCost = 8f;

        [Header("Identity")]
        public string fighterName = "Fighter";
        public bool isPlayer;
        public WarriorDef def;
        public RuntimeStats stats;
        public EnemyTrait[] traits = new EnemyTrait[0];

        [Header("Runtime")]
        public Fighter opponent;
        public IFighterBrain brain;
        public FighterAnimator anim;
        public FighterState state = FighterState.Frozen;
        public float health;
        public float stamina;
        public float special;

        // events for UI / FX / match
        public event Action<Fighter, float, Vector3, bool> OnDamaged;   // victim, amount, point, blocked
        public event Action<Fighter, string> OnPopup;                   // fighter, text ("PARRY!")
        public event Action<Fighter> OnDeath;
        public event Action<Fighter, AttackData> OnMoveStarted;  // for the HUD move list
        public const int MaxChain = 4;                           // moves per combo before a breath

        // online
        public bool isRemote;                                   // driven by network snapshots
        public event Action<string, float> OnRemoteCommand;     // fired on a remote fighter ("stun", seconds)
        public Action<AttackData, float> OnRemoteHit;           // local fighter hit the remote one
        Vector3 _netPos;
        float _netYaw;
        int _netSeq = -1;
        bool _netHasState;

        // action runtime
        AttackData _atk;
        bool _atkIsSpecial;
        int _chain;              // how many moves were chained without a break (0 = first)
        float _t, _dur;
        bool _hitDone;
        AttackData _queued;      // next move pressed during the current one
        Vector3 _dodgeDir;
        float _dodgeDist;
        float _blockStart;
        float _blockHitTimer;
        float _staminaDelay;
        float _lastHitTaken = -99f;
        Vector3 _push;           // knockback velocity
        float _rageTimer;
        float _bleedTimer, _bleedDps;
        Fighter _bleedSource;
        bool _getUpPending;
        float _height, _vy;          // jump physics (no rigidbody)
        Vector3 _airVel;
        bool _airAttacked, _airHitDone;
        float _airAttackTime;
        AttackData _airAtk;

        // ---------------------------------------------------------------- setup

        public void Setup(WarriorDef warrior, RuntimeStats s, EnemyTrait[] enemyTraits, string displayName, bool player, IFighterBrain b, FighterAnimator a)
        {
            def = warrior;
            stats = s;
            traits = enemyTraits ?? new EnemyTrait[0];
            fighterName = displayName;
            isPlayer = player;
            brain = b;
            anim = a;
            transform.localScale = Vector3.one * stats.scale;
            _airAtk = new AttackData
            {
                anim = "JumpAttack", displayName = "Jump Attack", damage = def.hand[3].damage * 0.9f, reach = def.hand[2].reach + 0.4f,
                arc = 75f, hitType = HitType.Heavy, knockback = 1.2f, staminaCost = 10f
            };
            ResetForRound(transform.position, transform.rotation);
        }

        public void ResetForRound(Vector3 pos, Quaternion rot)
        {
            transform.SetPositionAndRotation(pos, rot);
            health = stats.maxHealth;
            stamina = stats.maxStamina;
            special = 0f;
            _rageTimer = 0f;
            _bleedTimer = 0f;
            _push = Vector3.zero;
            _atk = null;
            _getUpPending = false;
            _height = _vy = 0f;
            _airAttacked = false;
            state = FighterState.Frozen;
            if (anim != null) { anim.current = ""; anim.ToLocomotion(0.05f); }
        }

        public void SetFrozen(bool frozen)
        {
            if (state == FighterState.Dead) return;
            if (frozen) { state = FighterState.Frozen; if (anim != null) anim.ToLocomotion(); }
            else if (state == FighterState.Frozen) state = FighterState.Move;
        }

        public void SetVictory()
        {
            if (state == FighterState.Dead) return;
            state = FighterState.Victory;
            if (anim != null) anim.ToLocomotion();
        }

        // ---------------------------------------------------------------- queries (used by AI / UI)

        public bool IsDead => state == FighterState.Dead;
        public bool IsAttacking => state == FighterState.Attack;
        public bool IsBlocking => state == FighterState.Block;
        public bool IsJumping => state == FighterState.Jump;
        public float Height => _height;
        public AttackData CurrentAttack => _atk;
        public float ActionNormalized => _dur > 0 ? _t / _dur : 1f;
        public bool IsWindingUp => state == FighterState.Attack && _atk != null && ActionNormalized < _atk.hitStart;
        public bool CanAct => state == FighterState.Move || state == FighterState.Block;
        public float Health01 => stats != null && stats.maxHealth > 0 ? Mathf.Clamp01(health / stats.maxHealth) : 0f;
        public float Stamina01 => stats != null && stats.maxStamina > 0 ? Mathf.Clamp01(stamina / stats.maxStamina) : 0f;
        public float Special01 => Mathf.Clamp01(special / SpecialMax);
        public bool IsRaging => _rageTimer > 0f;
        public bool HasTrait(EnemyTrait t) { foreach (var x in traits) if (x == t) return true; return false; }
        public float TypicalReach => def != null && def.hand[0] != null ? def.hand[0].reach * stats.reachMultiplier : 2f;

        public float DistanceToOpponent()
        {
            if (opponent == null) return 999f;
            var d = opponent.transform.position - transform.position; d.y = 0f;
            return d.magnitude;
        }

        bool LowHealthBerserk => HasTrait(EnemyTrait.Berserk) && Health01 < 0.35f;

        float PowerNow()
        {
            float p = stats.power;
            if (_rageTimer > 0f) p *= 1.4f;
            if (LowHealthBerserk) p *= 1.5f;
            return p;
        }

        float SpeedNow()
        {
            float s = stats.speed;
            if (LowHealthBerserk) s *= 1.2f;
            return s;
        }

        bool IsInvulnerable()
        {
            if (state == FighterState.GetUp) return true;
            if (state == FighterState.Knockdown) return true;
            if (state == FighterState.Dodge)
            {
                float n = ActionNormalized;
                return n > 0.05f && n < 0.65f;
            }
            return false;
        }

        bool HasSuperArmor()
        {
            if (_rageTimer > 0f) return true;
            if (state == FighterState.Attack && def.passive == PassiveType.IronBody && _atk != null &&
                (_atkIsSpecial || _atk.hitType != HitType.Light)) return true;
            return false;
        }

        // ---------------------------------------------------------------- main loop

        void Update()
        {
            if (def == null || stats == null) return;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            if (isRemote) { RemoteUpdate(dt); return; }

            FighterInput input = default;
            bool acceptsInput = state != FighterState.Frozen && state != FighterState.Dead && state != FighterState.Victory;
            if (acceptsInput && brain != null && opponent != null) input = brain.GetInput(this, dt);

            TickResources(dt);

            switch (state)
            {
                case FighterState.Frozen:
                case FighterState.Victory:
                    FaceOpponent(dt, 540f);
                    anim?.SetLocomotion(Vector2.zero, 1f, dt);
                    break;
                case FighterState.Move: UpdateMove(input, dt); break;
                case FighterState.Attack: UpdateAttack(input, dt); break;
                case FighterState.Block: UpdateBlock(input, dt); break;
                case FighterState.Dodge: UpdateDodge(dt); break;
                case FighterState.Jump: UpdateJump(input, dt); break;
                case FighterState.Recover:
                    _t += dt;
                    if (_t >= _dur) EndAction();
                    break;
                case FighterState.HitStun:
                case FighterState.Stunned:
                    _t += dt;
                    if (_t >= _dur) EndAction();
                    break;
                case FighterState.Knockdown:
                    _t += dt;
                    if (_t >= _dur) StartGetUp();
                    break;
                case FighterState.GetUp:
                    _t += dt;
                    if (_t >= _dur) EndAction();
                    break;
                case FighterState.Dead:
                    break;
            }

            UpdateVertical(dt);
            ApplyPush(dt);
            Constrain();
        }

        void TickResources(float dt)
        {
            if (state == FighterState.Dead) return;

            _staminaDelay -= dt;
            if (_staminaDelay <= 0f && state != FighterState.Attack)
            {
                float regen = state == FighterState.Block ? 6f : 24f;
                stamina = Mathf.Min(stats.maxStamina, stamina + regen * dt);
            }

            if (_rageTimer > 0f) _rageTimer -= dt;

            if (_bleedTimer > 0f)
            {
                _bleedTimer -= dt;
                TakeDirectDamage(_bleedDps * dt, _bleedSource, false);
            }

            if (HasTrait(EnemyTrait.Regeneration) && Time.time - _lastHitTaken > 2.5f && state != FighterState.Frozen)
                health = Mathf.Min(stats.maxHealth, health + stats.maxHealth * 0.012f * dt);
        }

        // ---------------------------------------------------------------- states

        void UpdateMove(FighterInput input, float dt)
        {
            if (input.special && special >= SpecialMax) { StartAttack(def.special, true, -1); return; }
            if (input.jump && _height <= 0f && stamina >= JumpCost) { StartJump(input.move); return; }
            if (input.attack || input.kick) { StartMove(input, 0); return; }
            if (input.dodge && stamina >= DodgeCost()) { StartDodge(input.move); return; }
            if (input.block && stamina > 1f) { EnterBlock(); return; }
            Locomote(input, dt, 1f);
        }

        void Locomote(FighterInput input, float dt, float mult)
        {
            float baseSpeed = def.moveSpeed * SpeedNow();
            Vector3 move = input.move;
            move.y = 0f;
            bool sprint = false;
            if (stamina > 5f && opponent != null && move.sqrMagnitude > 0.1f)
            {
                Vector3 toOpp = opponent.transform.position - transform.position; toOpp.y = 0f;
                float dist = toOpp.magnitude;
                bool toward = Vector3.Dot(move.normalized, toOpp.normalized) > 0.5f;
                // automatic run: when closing a big gap toward the opponent (AI may also request it)
                sprint = toward && (dist > 5f || input.sprint);
            }
            float speed = baseSpeed * mult * (sprint ? 1.7f : 1f);
            if (sprint) { stamina -= 10f * dt; _staminaDelay = 0.3f; }

            Vector3 vel = move * speed;
            transform.position += vel * dt;
            FaceOpponent(dt, 720f);

            Vector3 local = transform.InverseTransformDirection(vel) / Mathf.Max(0.01f, baseSpeed);
            Vector2 blend = new Vector2(local.x, local.z);
            if (sprint) blend = blend.normalized * 2f;
            anim?.SetLocomotion(blend, Mathf.Lerp(1f, SpeedNow(), 0.7f), dt);
        }

        AttackData PickMove(FighterInput input) => def.Move(input.kick && !input.attack, input.dir);

        void StartMove(FighterInput input, int chain)
        {
            var m = PickMove(input);
            if (m == null) return;
            if (stamina < m.staminaCost * 0.5f) { Popup("TIRED"); return; }
            StartAttack(m, false, chain);
        }

        void StartAttack(AttackData atk, bool isSpecial, int chain)
        {
            if (atk == null) return;
            _atk = atk;
            _atkIsSpecial = isSpecial;
            _chain = chain;
            _hitDone = false;
            _queued = null;
            OnMoveStarted?.Invoke(this, atk);
            _t = 0f;
            stamina -= atk.staminaCost;
            _staminaDelay = 0.7f;

            if (isSpecial)
            {
                special = 0f;
                if (def.specialType == SpecialType.Rage) { _rageTimer = 8f; Popup("RAGE!"); }
                else Popup(def.specialText.Split(':')[0].ToUpper() + "!");
            }

            float spd = atk.animSpeed * SpeedNow();
            float d = anim != null ? anim.Play(atk.anim, spd, 0.06f) : 0f;
            if (d <= 0f) d = 0.8f / spd; // animation missing: still works with timing only
            if (atk.maxDuration > 0f) d = Mathf.Min(d, atk.maxDuration);
            _dur = Mathf.Clamp(d, 0.25f, 3f);
            state = FighterState.Attack;
        }

        void UpdateAttack(FighterInput input, float dt)
        {
            _t += dt;
            float n = _t / _dur;

            if (n < _atk.hitStart) FaceOpponent(dt, 400f);

            // lunge forward up to the hit
            if (_atk.lunge > 0f && n < _atk.hitEnd)
            {
                float lungeTime = Mathf.Max(0.05f, _atk.hitEnd * _dur);
                float step = _atk.lunge / lungeTime * dt;
                float minDist = (BodyRadius * 2f) * Mathf.Max(stats.scale, opponent != null ? opponent.stats.scale : 1f) + 0.1f;
                float room = DistanceToOpponent() - minDist;
                if (room > 0f) transform.position += transform.forward * Mathf.Min(step, room);
            }

            if (!_hitDone && n >= _atk.hitStart && n <= _atk.hitEnd) TryHit();

            // combo rhythm: press the next move in the second half of the current one
            if (n >= _atk.comboWindow && (input.attack || input.kick) && !_atkIsSpecial && _chain < MaxChain - 1)
                _queued = PickMove(input);

            if (n >= _atk.cancelTime)
            {
                if (input.dodge && stamina >= DodgeCost()) { StartDodge(input.move); return; }
                if (_queued != null && stamina >= _queued.staminaCost * 0.5f)
                {
                    StartAttack(_queued, false, _chain + 1);
                    return;
                }
            }

            anim?.SetLocomotion(Vector2.zero, 1f, dt);
            if (n >= 1f) EndAction();
        }

        void TryHit()
        {
            if (TryHitWith(_atk, _chain)) _hitDone = true;
        }

        bool TryHitWith(AttackData atk, int comboIndex)
        {
            if (atk == null || opponent == null || opponent.IsDead) return false;
            Vector3 to = opponent.transform.position - transform.position; to.y = 0f;
            float d = to.magnitude;
            float reach = atk.reach * stats.reachMultiplier + BodyRadius * opponent.stats.scale;
            if (d > reach) return false;
            if (d > 0.6f && Vector3.Angle(transform.forward, to) > atk.arc) return false;

            float dmg = atk.damage * PowerNow();
            if (def.passive == PassiveType.ComboMaster && comboIndex > 0) dmg *= 1f + 0.1f * comboIndex;
            if (opponent.isRemote)
            {
                // online: the other player's game decides block / parry / dodge
                OnRemoteHit?.Invoke(atk, dmg);
                special = Mathf.Min(SpecialMax, special + dmg * 0.08f);
                return true;
            }
            opponent.ReceiveHit(this, atk, dmg);
            return true;
        }

        // ---------------------------------------------------------------- jump

        void StartJump(Vector3 move)
        {
            move.y = 0f;
            stamina -= JumpCost;
            _staminaDelay = 0.4f;
            _vy = JumpVelocity;
            _height = 0.001f;
            _airVel = Vector3.ClampMagnitude(move, 1f) * def.moveSpeed * SpeedNow() * 1.15f;
            _airAttacked = false;
            _airHitDone = false;
            _atk = null;
            _t = 0f;
            float airTime = 2f * JumpVelocity / Gravity;
            if (anim != null && anim.Has("Jump")) anim.Play("Jump", Mathf.Clamp(anim.Length("Jump") / airTime, 0.6f, 2.5f), 0.05f);
            state = FighterState.Jump;
        }

        void UpdateJump(FighterInput input, float dt)
        {
            _t += dt;
            transform.position += _airVel * dt;
            FaceOpponent(dt, 360f);

            if (!_airAttacked && (input.attack || input.kick))
            {
                _airAttacked = true;
                _airAttackTime = _t;
                stamina -= _airAtk.staminaCost;
                float spd = SpeedNow() * 1.2f;
                if (anim != null && anim.Play("JumpAttack", spd, 0.05f) <= 0f) anim.Play("HF", spd, 0.05f);
            }
            if (_airAttacked && !_airHitDone && _t - _airAttackTime > 0.08f && TryHitWith(_airAtk, -1)) _airHitDone = true;
        }

        void UpdateVertical(float dt)
        {
            if (_height <= 0f && _vy <= 0f) return;
            _vy -= Gravity * dt;
            _height += _vy * dt;
            if (_height > 0f) return;

            _height = 0f;
            _vy = 0f;
            if (state == FighterState.Jump)
            {
                if (_airAttacked && !_airHitDone && TryHitWith(_airAtk, -1)) _airHitDone = true;
                _airVel = Vector3.zero;
                state = FighterState.Recover;
                _t = 0f;
                _dur = _airAttacked ? 0.35f : 0.12f;
                if (!_airAttacked) anim?.ToLocomotion(0.1f);
            }
        }

        void EnterBlock()
        {
            state = FighterState.Block;
            _blockStart = Time.time;
            _blockHitTimer = 0f;
            anim?.Play("Block", 1f, 0.08f);
        }

        void UpdateBlock(FighterInput input, float dt)
        {
            if (input.special && special >= SpecialMax) { StartAttack(def.special, true, -1); return; }
            if (input.attack || input.kick) { StartMove(input, 0); return; }
            if (input.dodge && stamina >= DodgeCost()) { StartDodge(input.move); return; }
            if (!input.block || stamina <= 0f) { EndAction(); return; }
            if (_blockHitTimer > 0f)
            {
                _blockHitTimer -= dt;
                if (_blockHitTimer <= 0f) anim?.Play("Block", 1f, 0.1f, 0.5f);
            }
            // slow guarded strafe
            Vector3 vel = input.move * def.moveSpeed * 0.35f;
            transform.position += vel * dt;
            FaceOpponent(dt, 540f);
        }

        float DodgeCost() => def.passive == PassiveType.Footwork ? def.dodgeCost * 0.5f : def.dodgeCost;

        void StartDodge(Vector3 move)
        {
            move.y = 0f;
            Vector3 dir = move.sqrMagnitude > 0.05f ? move.normalized : -transform.forward;
            Vector3 local = transform.InverseTransformDirection(dir);
            string key;
            if (Mathf.Abs(local.x) > Mathf.Abs(local.z)) key = local.x > 0 ? "DodgeR" : "DodgeL";
            else key = local.z > 0 ? "DodgeF" : "DodgeB";

            stamina -= DodgeCost();
            _staminaDelay = 0.5f;
            float spd = 1.1f * SpeedNow();
            float d = anim != null ? anim.Play(key, spd, 0.05f) : 0f;
            if (d <= 0f) d = 0.5f;
            _dur = Mathf.Clamp(d, 0.35f, 0.7f);
            _t = 0f;
            _dodgeDir = dir;
            _dodgeDist = def.dodgeDistance;
            _atk = null;
            state = FighterState.Dodge;
        }

        void UpdateDodge(float dt)
        {
            _t += dt;
            float n = Mathf.Clamp01(_t / _dur);
            // ease-out: most distance at the start
            float speed = _dodgeDist / _dur * 2f * (1f - n);
            transform.position += _dodgeDir * speed * dt;
            if (_t >= _dur) EndAction();
        }

        void EndAction()
        {
            if (state == FighterState.Dead) return;
            _atk = null;
            _chain = 0;
            _queued = null;
            state = FighterState.Move;
            anim?.ToLocomotion();
        }

        // ---------------------------------------------------------------- getting hit

        public void ReceiveHit(Fighter attacker, AttackData atk, float dmg)
        {
            if (isRemote) return;
            if (state == FighterState.Dead || state == FighterState.Frozen || state == FighterState.Victory) return;
            if (IsInvulnerable()) { Popup("MISS"); return; }
            // jumping over a ground attack (specials still connect)
            if (_height > 0.75f && attacker.Height < 0.3f && atk != attacker.def.special) { Popup("JUMPED"); return; }

            Vector3 toAtt = attacker.transform.position - transform.position; toAtt.y = 0f;
            Vector3 away = toAtt.sqrMagnitude > 0.001f ? -toAtt.normalized : -transform.forward;
            bool front = Vector3.Angle(transform.forward, toAtt) < 80f;
            bool unblockable = atk.hitType == HitType.Grab;
            float pushMul = attacker.def.passive == PassiveType.LongReach ? 1.8f : 1f;

            if (state == FighterState.Block && front && !unblockable)
            {
                // perfect block = parry
                if (Time.time - _blockStart <= def.parryWindow)
                {
                    Popup("PARRY!");
                    special = Mathf.Min(SpecialMax, special + 15f);
                    attacker.EnterStun(1.1f);
                    OnDamaged?.Invoke(this, 0f, transform.position + Vector3.up * 1.3f, true);
                    return;
                }

                bool heavyHit = atk.hitType == HitType.Heavy || atk.hitType == HitType.Knockdown || atk.hitType == HitType.Stun;
                stamina -= heavyHit ? 35f : 10f + dmg * 0.05f;
                _staminaDelay = 0.8f;
                if (stamina <= 0f)
                {
                    stamina = 0f;
                    Popup("GUARD BREAK");
                    ApplyDamage(attacker, dmg * 0.35f, false);
                    if (!IsDead) EnterStun(1.0f);
                    return;
                }
                ApplyDamage(attacker, dmg * (1f - def.blockReduction), true);
                _push = away * atk.knockback * 0.6f * pushMul / 0.15f;
                if (anim != null && anim.Has("BlockHit")) { anim.Play("BlockHit", 1.2f, 0.05f); _blockHitTimer = 0.35f; }
                return;
            }

            ApplyDamage(attacker, dmg, false);
            if (IsDead) return;

            if (attacker.def.passive == PassiveType.Bleed)
            {
                _bleedTimer = 3f;
                _bleedDps = dmg * 0.25f / 3f;
                _bleedSource = attacker;
            }

            _push = away * atk.knockback * pushMul / 0.15f;
            bool armor = HasSuperArmor();
            switch (atk.hitType)
            {
                case HitType.Light:
                    if (!armor) EnterHitStun(0.4f);
                    break;
                case HitType.Heavy:
                    if (!armor) EnterHitStun(0.7f);
                    break;
                case HitType.Knockdown:
                    if (!armor) EnterKnockdown(); else Popup("ARMOR");
                    break;
                case HitType.Grab:
                    EnterKnockdown();
                    break;
                case HitType.Stun:
                    if (!armor) { EnterStun(1.6f); Popup("STUNNED"); }
                    break;
            }
        }

        void ApplyDamage(Fighter attacker, float amount, bool blocked)
        {
            amount *= 1f - stats.defense;
            if (HasTrait(EnemyTrait.IronSkin)) amount *= 0.75f;
            amount = Mathf.Max(1f, amount);

            health -= amount;
            _lastHitTaken = Time.time;
            special = Mathf.Min(SpecialMax, special + amount * 0.06f);

            if (attacker != null && attacker != this && !attacker.isRemote)
            {
                attacker.special = Mathf.Min(SpecialMax, attacker.special + amount * 0.1f);
                if (attacker.HasTrait(EnemyTrait.Vampiric)) attacker.Heal(amount * 0.25f);
                if (HasTrait(EnemyTrait.Thorns)) attacker.TakeDirectDamage(amount * 0.15f, this, true);
            }

            OnDamaged?.Invoke(this, amount, transform.position + Vector3.up * 1.3f * stats.scale, blocked);
            if (health <= 0f) Die();
        }

        /// <summary>Damage that ignores block / defense (bleed, thorns).</summary>
        public void TakeDirectDamage(float amount, Fighter source, bool notify)
        {
            if (isRemote) return;
            if (IsDead || state == FighterState.Frozen || state == FighterState.Victory) return;
            health -= amount;
            if (notify) OnDamaged?.Invoke(this, amount, transform.position + Vector3.up * 1.3f, false);
            if (health <= 0f) Die();
        }

        public void Heal(float amount)
        {
            if (isRemote || IsDead) return;
            health = Mathf.Min(stats.maxHealth, health + amount);
        }

        void EnterHitStun(float time)
        {
            _atk = null;
            state = FighterState.HitStun;
            _t = 0f;
            _dur = time;
            anim?.Play("Hit", 1.2f, 0.04f);
        }

        public void EnterStun(float time)
        {
            if (isRemote) { OnRemoteCommand?.Invoke("stun", time); return; }
            if (IsDead) return;
            _atk = null;
            state = FighterState.Stunned;
            _t = 0f;
            _dur = time;
            if (anim != null && anim.Play("Stun", 1f, 0.08f) <= 0f) anim.Play("Hit", 0.6f, 0.05f);
        }

        void EnterKnockdown()
        {
            _atk = null;
            state = FighterState.Knockdown;
            _t = 0f;
            float d = anim != null ? anim.Play("HitHeavy", 1f, 0.05f) : 0f;
            _getUpPending = anim != null && anim.Has("GetUp");
            // if there is a separate get-up clip, cut the fall early; otherwise let the fall clip finish
            _dur = _getUpPending ? Mathf.Clamp(d * 0.85f, 0.8f, 1.6f) : Mathf.Clamp(d, 1.0f, 2.4f);
        }

        void StartGetUp()
        {
            if (_getUpPending && anim != null)
            {
                float d = anim.Play("GetUp", 1.3f, 0.1f);
                state = FighterState.GetUp;
                _t = 0f;
                _dur = Mathf.Clamp(d, 0.5f, 1.6f);
                _getUpPending = false;
            }
            else EndAction();
        }

        void Die()
        {
            if (state == FighterState.Dead) return;
            health = 0f;
            _atk = null;
            state = FighterState.Dead;
            _bleedTimer = 0f;
            anim?.Play("Death", 1f, 0.08f);
            OnDeath?.Invoke(this);
        }

        void Popup(string text) => OnPopup?.Invoke(this, text);

        // ---------------------------------------------------------------- online

        public AttackData GetAttackByKey(string key)
        {
            if (def == null || string.IsNullOrEmpty(key)) return null;
            if (key == "Special") return def.special;
            if (key == "JumpAttack") return _airAtk;
            foreach (var a in def.hand) if (a != null && a.anim == key) return a;
            foreach (var a in def.kick) if (a != null && a.anim == key) return a;
            return null;
        }

        public NetState BuildNetState()
        {
            var p = transform.position;
            return new NetState
            {
                x = p.x, y = _height, z = p.z, ry = transform.eulerAngles.y,
                s = (int)state,
                a = anim != null ? anim.current : "Locomotion",
                q = anim != null ? anim.playSeq : 0,
                asp = anim != null ? anim.lastSpeed : 1f,
                mx = anim != null ? anim.lastBlend.x : 0f,
                mz = anim != null ? anim.lastBlend.y : 0f,
                ms = anim != null ? anim.lastMoveSpeed : 1f,
                hp = health, mhp = stats.maxHealth, stm = Stamina01, sp = Special01
            };
        }

        public void ApplyRemoteState(NetMsg m)
        {
            _netPos = new Vector3(m.x, m.y, m.z);
            _netYaw = m.ry;
            if (!_netHasState) { transform.SetPositionAndRotation(_netPos, Quaternion.Euler(0f, _netYaw, 0f)); _netHasState = true; }
            state = (FighterState)m.s;
            _height = m.y;
            health = m.hp;
            stamina = m.stm * stats.maxStamina;
            special = m.sp * SpecialMax;
            if (anim != null)
            {
                anim.lastBlend = new Vector2(m.mx, m.mz);
                anim.lastMoveSpeed = m.ms <= 0f ? 1f : m.ms;
                if (m.q != _netSeq)
                {
                    _netSeq = m.q;
                    if (string.IsNullOrEmpty(m.a) || m.a == "Locomotion") anim.ToLocomotion(0.12f);
                    else anim.Play(m.a, m.asp <= 0f ? 1f : m.asp, 0.06f);
                }
            }
        }

        /// <summary>Puts a remote fighter at its round start position until the next snapshot.</summary>
        public void ResetRemote(Vector3 pos, Quaternion rot)
        {
            ResetForRound(pos, rot);
            _netPos = pos;
            _netYaw = rot.eulerAngles.y;
            _netHasState = true;
        }

        void RemoteUpdate(float dt)
        {
            if (!_netHasState) return;
            float k = 1f - Mathf.Exp(-14f * dt);
            Vector3 p = transform.position;
            p = (p - _netPos).sqrMagnitude > 9f ? _netPos : Vector3.Lerp(p, _netPos, k);
            transform.SetPositionAndRotation(p, Quaternion.Slerp(transform.rotation, Quaternion.Euler(0f, _netYaw, 0f), k));
            if (anim != null && anim.current == "Locomotion") anim.SetLocomotion(anim.lastBlend, anim.lastMoveSpeed, dt);
        }

        // ---------------------------------------------------------------- movement helpers

        void FaceOpponent(float dt, float degPerSec)
        {
            if (opponent == null) return;
            Vector3 d = opponent.transform.position - transform.position; d.y = 0f;
            if (d.sqrMagnitude < 0.0001f) return;
            var target = Quaternion.LookRotation(d.normalized, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, target, degPerSec * dt);
        }

        void ApplyPush(float dt)
        {
            if (_push.sqrMagnitude < 0.001f) return;
            transform.position += _push * dt;
            _push = Vector3.MoveTowards(_push, Vector3.zero, 40f * dt);
        }

        void Constrain()
        {
            Vector3 p = transform.position;
            p.y = _height;
            Vector2 flat = new Vector2(p.x, p.z);
            if (flat.magnitude > ArenaRadius) { flat = flat.normalized * ArenaRadius; p.x = flat.x; p.z = flat.y; }

            // keep bodies apart (each fighter resolves half)
            if (opponent != null && !IsDead && !opponent.IsDead)
            {
                Vector3 o = opponent.transform.position; o.y = 0f;
                Vector3 d = new Vector3(p.x, 0f, p.z) - o;
                float minD = BodyRadius * (stats.scale + opponent.stats.scale);
                float dist = d.magnitude;
                if (dist < minD)
                {
                    Vector3 n = dist > 0.001f ? d / dist : transform.right;
                    p += n * (minD - dist) * 0.5f;
                }
            }
            transform.position = p;
        }
    }
}
