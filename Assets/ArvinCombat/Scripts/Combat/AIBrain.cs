using UnityEngine;

namespace ArvinCombat
{
    /// <summary>
    /// Simple but readable fighting AI. Its skill comes from an AIProfile so every
    /// offline level can feel different (careful blockers, aggressive brawlers, dodgers...).
    /// </summary>
    public class AIBrain : IFighterBrain
    {
        enum Intent { Approach, Circle, Retreat, Pressure }

        readonly AIProfile p;
        Intent _intent = Intent.Approach;
        float _intentTimer;
        float _circleSign = 1f;
        float _reactTimer = -1f;
        bool _defending;
        float _blockHold;
        float _attackCooldown;
        bool _decidedForThisAttack;

        public AIBrain(AIProfile profile) { p = profile ?? new AIProfile(); }

        public FighterInput GetInput(Fighter self, float dt)
        {
            var input = new FighterInput();
            var opp = self.opponent;
            if (opp == null || opp.IsDead) return input;

            Vector3 toOpp = opp.transform.position - self.transform.position; toOpp.y = 0f;
            float dist = toOpp.magnitude;
            Vector3 dir = dist > 0.01f ? toOpp / dist : self.transform.forward;
            Vector3 side = Vector3.Cross(Vector3.up, dir) * _circleSign;
            float reach = self.TypicalReach + Fighter.BodyRadius;

            _attackCooldown -= dt;
            _intentTimer -= dt;

            // ---------- defense: react to the opponent's wind-up
            if (opp.IsWindingUp && dist < opp.CurrentAttack.reach * opp.stats.reachMultiplier + 1.2f)
            {
                if (!_decidedForThisAttack)
                {
                    _decidedForThisAttack = true;
                    float roll = Random.value;
                    if (roll < p.dodgeSkill) { _reactTimer = p.reactionTime * Random.Range(0.6f, 1.2f); _defending = false; _pendingDodge = true; }
                    else if (roll < p.dodgeSkill + p.blockSkill && opp.CurrentAttack.hitType != HitType.Grab)
                    { _reactTimer = p.reactionTime * Random.Range(0.6f, 1.2f); _defending = true; _pendingDodge = false; }
                    else { _reactTimer = -1f; _defending = false; _pendingDodge = false; }
                }
            }
            else if (!opp.IsAttacking) _decidedForThisAttack = false;

            if (_reactTimer > 0f)
            {
                _reactTimer -= dt;
                if (_reactTimer <= 0f)
                {
                    if (_pendingDodge)
                    {
                        // sometimes jump over the attack instead of rolling
                        if (Random.value < 0.3f && opp.CurrentAttack != null && opp.CurrentAttack != opp.def.special) input.jump = true;
                        else input.dodge = true;
                        input.move = (Random.value < 0.5f ? side : -dir);
                        _pendingDodge = false;
                        return input;
                    }
                    if (_defending) _blockHold = Random.Range(0.4f, 0.9f);
                }
            }

            if (_blockHold > 0f)
            {
                _blockHold -= dt;
                input.block = true;
                // counter-attack chance right after blocking
                if (_blockHold <= 0f && dist < reach && Random.value < p.aggression) PickMove(ref input, opp, false);
                return input;
            }

            // ---------- low stamina: back off
            if (self.Stamina01 < 0.2f && _intent != Intent.Retreat) { _intent = Intent.Retreat; _intentTimer = 1.2f; }

            // ---------- pick a new intent
            if (_intentTimer <= 0f)
            {
                float r = Random.value;
                if (r < p.aggression) _intent = Intent.Pressure;
                else if (r < p.aggression + 0.25f) _intent = Intent.Circle;
                else _intent = dist > reach * 1.5f ? Intent.Approach : Intent.Circle;
                if (Random.value < 0.3f) _circleSign = -_circleSign;
                _intentTimer = Random.Range(0.5f, 1.4f);
            }

            // ---------- movement
            switch (_intent)
            {
                case Intent.Approach:
                case Intent.Pressure:
                    if (dist > reach * 0.85f) input.move = dir;
                    else input.move = side * 0.4f;
                    input.sprint = dist > 6f;
                    break;
                case Intent.Circle:
                    input.move = (side + dir * (dist > reach * 1.6f ? 0.6f : dist < reach ? -0.4f : 0f)).normalized * 0.8f;
                    break;
                case Intent.Retreat:
                    input.move = (-dir + side * 0.5f).normalized;
                    break;
            }

            // ---------- offense
            if (self.CanAct && dist < reach && _attackCooldown <= 0f && _intent != Intent.Retreat)
            {
                if (self.Special01 >= 1f && Random.value < 0.5f) input.special = true;
                else PickMove(ref input, opp, Random.value < p.heavyChance);
                _attackCooldown = Mathf.Lerp(1.3f, 0.35f, p.aggression) * Random.Range(0.7f, 1.3f);
            }
            // jump attack: strike on the way down, sometimes open with a jump
            if (self.IsJumping && dist < reach + 1.2f) input.attack = true;
            if (self.CanAct && dist > reach && dist < reach + 2.5f && _attackCooldown <= 0f && Random.value < p.aggression * 0.01f)
            {
                input.jump = true;
                input.move = dir;
            }
            // keep the combo going
            if (self.IsAttacking && Random.value < p.comboSkill * dt * 8f) PickMove(ref input, opp, false);

            // special from range for dash type specials
            if (self.CanAct && self.Special01 >= 1f && dist < 5f && dist > reach &&
                (self.def.specialType == SpecialType.IaiDash || self.def.specialType == SpecialType.FlyingKick) && Random.value < 0.02f)
                input.special = true;

            return input;
        }

        bool _pendingDodge;

        /// <summary>Chooses one of the 8 moves: back moves break guard, kicks/Q moves hit harder.</summary>
        void PickMove(ref FighterInput input, Fighter opp, bool wantHeavy)
        {
            bool q = Random.value < 0.4f;
            if (opp.IsBlocking && Random.value < 0.6f) wantHeavy = true;
            if (wantHeavy) input.dir = AttackDir.Back;
            else
            {
                float r = Random.value;
                input.dir = r < 0.35f ? AttackDir.Left : r < 0.7f ? AttackDir.Right : AttackDir.Front;
            }
            if (q) input.kick = true; else input.attack = true;
        }
    }
}
