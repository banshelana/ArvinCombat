using System;
using UnityEngine;

namespace ArvinCombat
{
    public enum WarriorId { Samurai = 0, Karate = 1, Spearman = 2, Knight = 3, Boxer = 4, Wrestler = 5 }

    /// <summary>How a successful hit affects the victim.</summary>
    public enum HitType
    {
        Light,      // short flinch
        Heavy,      // long flinch + push, breaks guard
        Knockdown,  // falls down, gets up
        Stun,       // dizzy, can't act
        Grab        // unblockable throw -> knockdown
    }

    public enum PassiveType
    {
        Bleed,        // Samurai: hits make the enemy bleed over time
        ComboMaster,  // Karate: every chained hit does more damage
        LongReach,    // Spearman: longest reach + strong push back
        ParryMaster,  // Knight: strong guard and wide parry window
        Footwork,     // Boxer: cheap/fast dodges, quick feet
        IronBody      // Wrestler: heavy attacks can't be interrupted
    }

    public enum SpecialType { IaiDash, FlyingKick, Whirlwind, Rage, StunUppercut, Takedown }

    /// <summary>Extra rules an offline enemy can have.</summary>
    public enum EnemyTrait
    {
        None,
        IronSkin,     // takes 25% less damage
        LongReach,    // +20% reach
        Regeneration, // heals when not hit for a while
        Berserk,      // stronger and faster under 35% health
        Swift,        // +25% speed
        Vampiric,     // heals 25% of damage dealt
        Giant,        // bigger, more health and power, a bit slower
        Thorns        // attacker takes 15% of the damage back
    }

    /// <summary>Direction key held when attacking (relative to the enemy: W = toward him).</summary>
    public enum AttackDir { Left = 0, Right = 1, Front = 2, Back = 3 }

    public enum UpgradeStat { Power = 0, Speed = 1, Health = 2, Defense = 3, Stamina = 4 }

    [Serializable]
    public struct StatBlock
    {
        public float health;   // hit points
        public float power;    // damage multiplier
        public float speed;    // move + attack speed multiplier
        public float defense;  // 0..0.6 damage reduction
        public float stamina;  // stamina pool

        public StatBlock(float health, float power, float speed, float defense, float stamina)
        {
            this.health = health; this.power = power; this.speed = speed; this.defense = defense; this.stamina = stamina;
        }
    }

    /// <summary>One attack move. Times are normalized (0..1) over the animation.</summary>
    [Serializable]
    public class AttackData
    {
        public string anim;            // animator state key: HL HR HF HB (mouse), KL KR KF KB (Q), Special, JumpAttack
        public string displayName = "";
        public float damage = 50f;
        public float reach = 1.8f;     // meters from attacker center
        public float arc = 60f;        // half angle in degrees
        public float hitStart = 0.30f;
        public float hitEnd = 0.50f;
        public float lunge = 0.5f;     // meters moved forward before the hit
        public float staminaCost = 8f;
        public HitType hitType = HitType.Light;
        public float animSpeed = 1f;
        public float comboWindow = 0.45f; // after this, pressing attack queues the next combo hit
        public float cancelTime = 0.70f;  // queued next attack starts here
        public float knockback = 0.6f;    // meters pushed on hit
        public float maxDuration = 0f;    // optional cap (seconds) for long clips; 0 = clip length

        public AttackData Clone() { return (AttackData)MemberwiseClone(); }
    }

    public class WarriorDef
    {
        public WarriorId id;
        public string displayName;
        public string description;
        public StatBlock baseStats;
        public float moveSpeed = 3.2f;
        public float blockReduction = 0.75f;
        public float parryWindow = 0.15f;
        public float dodgeDistance = 3.2f;
        public float dodgeCost = 22f;
        public AttackData[] hand = new AttackData[4];  // left mouse + A / D / W / S
        public AttackData[] kick = new AttackData[4];  // Q + A / D / W / S
        public string handSetName = "Strikes";
        public string kickSetName = "Kicks";            // warriors without kicks have their own Q set
        public AttackData special;

        public AttackData Move(bool q, AttackDir dir) => (q ? kick : hand)[(int)dir];
        public SpecialType specialType;
        public PassiveType passive;
        public string passiveText;
        public string specialText;
        public Color uiColor = Color.white;
    }

    /// <summary>Final numbers used during a fight (base stats + upgrades + level modifiers + traits).</summary>
    public class RuntimeStats
    {
        public float maxHealth;
        public float power;
        public float speed;
        public float defense;
        public float maxStamina;
        public float reachMultiplier = 1f;
        public float scale = 1f;

        public static RuntimeStats Build(WarriorDef def, int[] upgradeRanks, float difficulty, EnemyTrait[] traits)
        {
            var s = new RuntimeStats();
            int r(UpgradeStat st) => upgradeRanks != null && upgradeRanks.Length > (int)st ? upgradeRanks[(int)st] : 0;

            s.maxHealth = def.baseStats.health * (1f + 0.08f * r(UpgradeStat.Health));
            s.power = def.baseStats.power * (1f + 0.06f * r(UpgradeStat.Power));
            s.speed = Mathf.Min(1.6f, def.baseStats.speed * (1f + 0.03f * r(UpgradeStat.Speed)));
            s.defense = Mathf.Min(0.6f, def.baseStats.defense + 0.03f * r(UpgradeStat.Defense));
            s.maxStamina = def.baseStats.stamina * (1f + 0.08f * r(UpgradeStat.Stamina));

            // offline level scaling
            s.maxHealth *= difficulty;
            s.power *= Mathf.Lerp(1f, difficulty, 0.6f);

            if (traits != null)
            {
                foreach (var t in traits)
                {
                    switch (t)
                    {
                        case EnemyTrait.Swift: s.speed *= 1.25f; break;
                        case EnemyTrait.LongReach: s.reachMultiplier *= 1.2f; break;
                        case EnemyTrait.Giant:
                            s.scale *= 1.25f; s.maxHealth *= 1.4f; s.power *= 1.2f; s.speed *= 0.9f; s.reachMultiplier *= 1.15f;
                            break;
                    }
                }
            }
            return s;
        }
    }

    public static class TraitInfo
    {
        public static string Describe(EnemyTrait t)
        {
            switch (t)
            {
                case EnemyTrait.IronSkin: return "Iron Skin: takes 25% less damage";
                case EnemyTrait.LongReach: return "Long Reach: +20% attack range";
                case EnemyTrait.Regeneration: return "Regeneration: heals when not hit";
                case EnemyTrait.Berserk: return "Berserk: stronger & faster when low health";
                case EnemyTrait.Swift: return "Swift: +25% speed";
                case EnemyTrait.Vampiric: return "Vampiric: heals from damage dealt";
                case EnemyTrait.Giant: return "Giant: bigger, tougher, hits harder";
                case EnemyTrait.Thorns: return "Thorns: you take 15% of your damage back";
                default: return "";
            }
        }
    }
}
