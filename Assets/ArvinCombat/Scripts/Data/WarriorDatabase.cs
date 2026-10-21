using System.Collections.Generic;
using UnityEngine;

namespace ArvinCombat
{
    /// <summary>
    /// All playable warriors and their 8 moves:
    ///   Left mouse + A / D / W / S  -> hand[Left, Right, Front, Back]
    ///   Q          + A / D / W / S  -> kick[Left, Right, Front, Back]
    /// Rhythm rule for every warrior:
    ///   Left / Right = fast and light, Front = longer reach, Back = slow and heavy (breaks guard).
    ///   Q moves are slower and stronger than the mouse moves.
    /// Defined in code so the online server can share the same numbers later.
    /// </summary>
    public static class WarriorDatabase
    {
        static Dictionary<WarriorId, WarriorDef> _defs;

        public static IEnumerable<WarriorDef> All { get { Init(); return _defs.Values; } }
        public static int Count => 6;

        public static WarriorDef Get(WarriorId id) { Init(); return _defs[id]; }

        static AttackData M(string anim, string name, float dmg, float reach, float hitStart, float hitEnd,
            HitType type = HitType.Light, float lunge = 0.5f, float stamina = 8f, float speed = 1f, float arc = 60f)
        {
            return new AttackData
            {
                anim = anim, displayName = name, damage = dmg, reach = reach, hitStart = hitStart, hitEnd = hitEnd,
                hitType = type, lunge = lunge, staminaCost = stamina, animSpeed = speed, arc = arc,
                knockback = type == HitType.Light ? 0.4f : 1.2f,
                comboWindow = 0.4f, cancelTime = type == HitType.Light ? 0.62f : 0.72f
            };
        }

        static void Init()
        {
            if (_defs != null) return;
            _defs = new Dictionary<WarriorId, WarriorDef>();

            // ---------------- SAMURAI ----------------
            _defs[WarriorId.Samurai] = new WarriorDef
            {
                id = WarriorId.Samurai,
                displayName = "Samurai",
                description = "Two-handed katana master. Slow but deadly, every cut bleeds.",
                baseStats = new StatBlock(1000, 1.15f, 0.95f, 0.10f, 100),
                moveSpeed = 3.0f,
                blockReduction = 0.75f,
                handSetName = "Katana Cuts",
                kickSetName = "Sword Arts",
                hand = new[]
                {
                    M("HL", "Left Cut", 55, 2.3f, 0.30f, 0.50f, HitType.Light, 0.6f, 9),
                    M("HR", "Right Cut", 55, 2.3f, 0.30f, 0.50f, HitType.Light, 0.6f, 9),
                    M("HF", "Thrust", 70, 2.6f, 0.30f, 0.52f, HitType.Light, 0.8f, 10, 1f, 35),
                    M("HB", "Rising Slash", 120, 2.4f, 0.35f, 0.55f, HitType.Heavy, 0.6f, 22, 1f, 70),
                },
                kick = new[]
                {
                    M("KL", "Whirling Cut", 75, 2.4f, 0.32f, 0.55f, HitType.Light, 0.7f, 12, 0.95f, 80),
                    M("KR", "Crescent Cut", 75, 2.4f, 0.32f, 0.55f, HitType.Light, 0.7f, 12, 0.95f, 80),
                    M("KF", "Lunging Strike", 95, 2.7f, 0.33f, 0.56f, HitType.Heavy, 1.2f, 16, 0.95f, 40),
                    M("KB", "Executioner", 150, 2.4f, 0.40f, 0.62f, HitType.Knockdown, 0.8f, 26, 0.9f, 70),
                },
                special = M("Special", "Iai Dash", 220, 2.6f, 0.25f, 0.55f, HitType.Knockdown, 5.0f, 0, 1.1f, 50),
                specialType = SpecialType.IaiDash,
                passive = PassiveType.Bleed,
                passiveText = "Bleed: hits keep hurting the enemy for 3 seconds.",
                specialText = "Iai Dash: blinding dash-cut across the arena.",
                uiColor = new Color(0.85f, 0.2f, 0.25f)
            };

            // ---------------- KARATE ----------------
            _defs[WarriorId.Karate] = new WarriorDef
            {
                id = WarriorId.Karate,
                displayName = "Karate Master",
                description = "Fastest fighter. Chains punches and high kicks.",
                baseStats = new StatBlock(900, 0.9f, 1.2f, 0.05f, 110),
                moveSpeed = 3.6f,
                blockReduction = 0.7f,
                handSetName = "Punches",
                kickSetName = "Kicks",
                hand = new[]
                {
                    M("HL", "Left Punch", 32, 1.7f, 0.28f, 0.48f, HitType.Light, 0.4f, 6, 1.15f),
                    M("HR", "Right Punch", 34, 1.7f, 0.28f, 0.48f, HitType.Light, 0.4f, 6, 1.15f),
                    M("HF", "Straight Punch", 42, 1.9f, 0.28f, 0.50f, HitType.Light, 0.6f, 7, 1.15f, 40),
                    M("HB", "Power Strike", 85, 1.9f, 0.35f, 0.55f, HitType.Heavy, 0.5f, 18, 1.1f),
                },
                kick = new[]
                {
                    M("KL", "Left High Kick", 50, 2.1f, 0.30f, 0.52f, HitType.Light, 0.4f, 10, 1.1f, 70),
                    M("KR", "Right High Kick", 50, 2.1f, 0.30f, 0.52f, HitType.Light, 0.4f, 10, 1.1f, 70),
                    M("KF", "Front Kick", 65, 2.3f, 0.32f, 0.55f, HitType.Heavy, 0.9f, 13, 1.05f, 40),
                    M("KB", "Spinning Kick", 105, 2.2f, 0.36f, 0.58f, HitType.Knockdown, 0.5f, 22, 1.0f, 90),
                },
                special = M("Special", "Flying Kick", 170, 2.2f, 0.35f, 0.60f, HitType.Knockdown, 4.0f, 0, 1f, 55),
                specialType = SpecialType.FlyingKick,
                passive = PassiveType.ComboMaster,
                passiveText = "Combo Master: each chained move does +10% damage.",
                specialText = "Flying Kick: leaps forward and knocks the enemy down.",
                uiColor = new Color(0.95f, 0.85f, 0.3f)
            };

            // ---------------- SPEARMAN ----------------
            _defs[WarriorId.Spearman] = new WarriorDef
            {
                id = WarriorId.Spearman,
                displayName = "Spearman",
                description = "Keeps enemies at distance with the longest reach in the game.",
                baseStats = new StatBlock(950, 1.0f, 1.0f, 0.08f, 100),
                moveSpeed = 3.1f,
                blockReduction = 0.72f,
                handSetName = "Spear Strikes",
                kickSetName = "Spear Arts",
                hand = new[]
                {
                    M("HL", "Left Swing", 45, 2.9f, 0.30f, 0.50f, HitType.Light, 0.3f, 8, 1f, 70),
                    M("HR", "Right Swing", 45, 2.9f, 0.30f, 0.50f, HitType.Light, 0.3f, 8, 1f, 70),
                    M("HF", "Long Thrust", 55, 3.3f, 0.30f, 0.50f, HitType.Light, 0.4f, 9, 1f, 25),
                    M("HB", "Overhead Smash", 110, 3.0f, 0.36f, 0.56f, HitType.Heavy, 0.4f, 20, 1f, 45),
                },
                kick = new[]
                {
                    M("KL", "Spear Kick", 60, 2.0f, 0.30f, 0.52f, HitType.Light, 0.4f, 10, 1f, 60),
                    M("KR", "Reaping Sweep", 65, 3.0f, 0.32f, 0.55f, HitType.Light, 0.3f, 11, 1f, 90),
                    M("KF", "Charging Thrust", 85, 3.3f, 0.32f, 0.56f, HitType.Heavy, 1.5f, 15, 1f, 30),
                    M("KB", "Ground Sweep", 120, 3.1f, 0.38f, 0.60f, HitType.Knockdown, 0.3f, 24, 0.85f, 100),
                },
                special = M("Special", "Whirlwind", 160, 3.3f, 0.30f, 0.70f, HitType.Knockdown, 0.2f, 0, 1f, 180),
                specialType = SpecialType.Whirlwind,
                passive = PassiveType.LongReach,
                passiveText = "Long Reach: hits push enemies far back.",
                specialText = "Whirlwind: 360 degree spin that knocks down everyone close.",
                uiColor = new Color(0.3f, 0.75f, 0.4f)
            };

            // ---------------- KNIGHT ----------------
            _defs[WarriorId.Knight] = new WarriorDef
            {
                id = WarriorId.Knight,
                displayName = "Knight",
                description = "Sword and shield. Best defense and the easiest parries.",
                baseStats = new StatBlock(1100, 1.0f, 0.95f, 0.15f, 120),
                moveSpeed = 3.0f,
                blockReduction = 0.9f,
                parryWindow = 0.3f,
                handSetName = "Sword",
                kickSetName = "Shield Arts",
                hand = new[]
                {
                    M("HL", "Left Slash", 45, 2.1f, 0.30f, 0.50f, HitType.Light, 0.5f, 8),
                    M("HR", "Right Slash", 48, 2.1f, 0.30f, 0.50f, HitType.Light, 0.5f, 8),
                    M("HF", "Stab", 55, 2.3f, 0.30f, 0.50f, HitType.Light, 0.6f, 9, 1f, 35),
                    M("HB", "Crushing Blow", 115, 2.2f, 0.38f, 0.58f, HitType.Heavy, 0.6f, 22),
                },
                kick = new[]
                {
                    M("KL", "Side Step Cut", 60, 2.1f, 0.30f, 0.52f, HitType.Light, 0.6f, 11, 1f, 70),
                    M("KR", "Counter Slash", 60, 2.1f, 0.30f, 0.52f, HitType.Light, 0.6f, 11, 1f, 70),
                    M("KF", "Shield Charge", 80, 2.2f, 0.32f, 0.55f, HitType.Heavy, 1.6f, 15, 1f, 50),
                    M("KB", "Overhead Smash", 135, 2.2f, 0.40f, 0.62f, HitType.Knockdown, 0.6f, 26, 0.9f, 60),
                },
                special = M("Special", "Rage Strike", 150, 2.3f, 0.40f, 0.62f, HitType.Knockdown, 1.0f, 0, 1f, 70),
                specialType = SpecialType.Rage,
                passive = PassiveType.ParryMaster,
                passiveText = "Parry Master: blocks 90% damage, wide perfect-block window.",
                specialText = "Rage Strike: crushing blow, then +40% power and no flinch for 8s.",
                uiColor = new Color(0.4f, 0.6f, 0.95f)
            };

            // ---------------- BOXER ----------------
            _defs[WarriorId.Boxer] = new WarriorDef
            {
                id = WarriorId.Boxer,
                displayName = "Boxer",
                description = "Fast hands, quick feet. Dodges in and out of danger.",
                baseStats = new StatBlock(950, 0.95f, 1.15f, 0.08f, 115),
                moveSpeed = 3.7f,
                blockReduction = 0.75f,
                dodgeDistance = 3.0f,
                dodgeCost = 11f,
                handSetName = "Punches",
                kickSetName = "Power Punches",
                hand = new[]
                {
                    M("HL", "Left Hook", 32, 1.6f, 0.26f, 0.46f, HitType.Light, 0.4f, 5, 1.2f, 70),
                    M("HR", "Right Hook", 34, 1.6f, 0.26f, 0.46f, HitType.Light, 0.4f, 5, 1.2f, 70),
                    M("HF", "Jab", 30, 1.8f, 0.22f, 0.42f, HitType.Light, 0.5f, 4, 1.3f, 35),
                    M("HB", "Uppercut", 80, 1.6f, 0.33f, 0.53f, HitType.Heavy, 0.4f, 15, 1.1f),
                },
                kick = new[]
                {
                    M("KL", "Heavy Left Hook", 55, 1.7f, 0.30f, 0.50f, HitType.Light, 0.5f, 9, 1.1f, 75),
                    M("KR", "Heavy Right Hook", 55, 1.7f, 0.30f, 0.50f, HitType.Light, 0.5f, 9, 1.1f, 75),
                    M("KF", "Overhand", 70, 1.8f, 0.32f, 0.54f, HitType.Heavy, 0.7f, 12, 1.05f, 45),
                    M("KB", "Counter Hook", 95, 1.7f, 0.34f, 0.56f, HitType.Knockdown, 0.4f, 18, 1.0f, 70),
                },
                special = M("Special", "Uppercut Combo", 140, 1.8f, 0.35f, 0.58f, HitType.Stun, 0.8f, 0, 1f),
                specialType = SpecialType.StunUppercut,
                passive = PassiveType.Footwork,
                passiveText = "Footwork: dodges cost half stamina.",
                specialText = "Uppercut Combo: lifts the enemy off their feet and stuns them.",
                uiColor = new Color(0.9f, 0.45f, 0.2f)
            };

            // ---------------- WRESTLER ----------------
            _defs[WarriorId.Wrestler] = new WarriorDef
            {
                id = WarriorId.Wrestler,
                displayName = "Wrestler",
                description = "Huge and tough. Shrugs off hits and slams enemies to the ground.",
                baseStats = new StatBlock(1300, 1.1f, 0.85f, 0.12f, 100),
                moveSpeed = 2.8f,
                blockReduction = 0.7f,
                dodgeDistance = 2.4f,
                dodgeCost = 28f,
                handSetName = "Strikes",
                kickSetName = "MMA Kicks",
                hand = new[]
                {
                    M("HL", "Left Hook", 45, 1.7f, 0.30f, 0.50f, HitType.Light, 0.4f, 8, 1f, 70),
                    M("HR", "Right Hook", 48, 1.7f, 0.30f, 0.50f, HitType.Light, 0.4f, 8, 1f, 70),
                    M("HF", "Straight Punch", 50, 1.9f, 0.30f, 0.50f, HitType.Light, 0.5f, 8, 1f, 40),
                    M("HB", "Body Slam", 130, 1.7f, 0.38f, 0.58f, HitType.Knockdown, 0.6f, 24, 1f, 60),
                },
                kick = new[]
                {
                    M("KL", "MMA Kick", 65, 2.0f, 0.32f, 0.54f, HitType.Light, 0.4f, 11, 1f, 70),
                    M("KR", "Reverse Kick", 65, 2.0f, 0.32f, 0.54f, HitType.Light, 0.4f, 11, 1f, 70),
                    M("KF", "Flying Knee Kick", 85, 2.2f, 0.34f, 0.58f, HitType.Heavy, 1.4f, 15, 1f, 45),
                    M("KB", "Finisher Kick", 140, 2.1f, 0.40f, 0.62f, HitType.Knockdown, 0.6f, 26, 0.95f, 60),
                },
                special = M("Special", "Takedown", 230, 1.8f, 0.25f, 0.45f, HitType.Grab, 1.4f, 0, 1f, 50),
                specialType = SpecialType.Takedown,
                passive = PassiveType.IronBody,
                passiveText = "Iron Body: heavy moves and specials can't be interrupted.",
                specialText = "Takedown: unblockable grab that slams the enemy.",
                uiColor = new Color(0.6f, 0.35f, 0.75f)
            };
        }
    }
}
