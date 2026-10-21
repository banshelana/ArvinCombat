using System.Collections.Generic;

namespace ArvinCombat
{
    public class AIProfile
    {
        public float aggression = 0.5f;   // 0 = careful, 1 = always attacking
        public float reactionTime = 0.35f; // seconds before reacting to an attack
        public float blockSkill = 0.3f;    // chance to block an incoming attack
        public float dodgeSkill = 0.15f;   // chance to dodge an incoming attack
        public float comboSkill = 0.5f;    // chance to continue a combo
        public float heavyChance = 0.2f;
    }

    public class LevelDef
    {
        public int index;
        public string enemyName;
        public string title;
        public WarriorId warrior;
        public float difficulty = 1f;     // health / power scaling
        public EnemyTrait[] traits = new EnemyTrait[0];
        public AIProfile ai = new AIProfile();
        public int xpReward = 100;
        public int roundsToWin = 2;        // best of 3
    }

    /// <summary>Offline campaign: each level has a unique enemy with its own powers.</summary>
    public static class LevelDatabase
    {
        static List<LevelDef> _levels;
        public static IReadOnlyList<LevelDef> Levels { get { Init(); return _levels; } }

        static void Add(string name, string title, WarriorId w, float diff, AIProfile ai, int xp, params EnemyTrait[] traits)
        {
            _levels.Add(new LevelDef
            {
                index = _levels.Count, enemyName = name, title = title, warrior = w,
                difficulty = diff, ai = ai, xpReward = xp, traits = traits
            });
        }

        static AIProfile P(float aggr, float react, float block, float dodge, float combo, float heavy)
        {
            return new AIProfile { aggression = aggr, reactionTime = react, blockSkill = block, dodgeSkill = dodge, comboSkill = combo, heavyChance = heavy };
        }

        static void Init()
        {
            if (_levels != null) return;
            _levels = new List<LevelDef>();
            Add("Kenji", "The Rookie", WarriorId.Karate, 0.75f, P(0.35f, 0.6f, 0.10f, 0.05f, 0.3f, 0.10f), 80);
            Add("Bruno", "Iron Fist", WarriorId.Boxer, 0.85f, P(0.50f, 0.5f, 0.20f, 0.10f, 0.4f, 0.15f), 100, EnemyTrait.IronSkin);
            Add("Dusk", "Spear of Dusk", WarriorId.Spearman, 0.95f, P(0.45f, 0.45f, 0.25f, 0.10f, 0.5f, 0.20f), 120, EnemyTrait.LongReach);
            Add("Brother Hald", "The Wall", WarriorId.Knight, 1.0f, P(0.40f, 0.40f, 0.45f, 0.05f, 0.5f, 0.20f), 140, EnemyTrait.Regeneration);
            Add("Tor", "Grizzly", WarriorId.Wrestler, 1.05f, P(0.60f, 0.40f, 0.20f, 0.05f, 0.5f, 0.30f), 160, EnemyTrait.Berserk);
            Add("Ryo", "Shadow Blade", WarriorId.Samurai, 1.1f, P(0.55f, 0.32f, 0.30f, 0.25f, 0.6f, 0.25f), 190, EnemyTrait.Swift);
            Add("Lin", "The Viper", WarriorId.Karate, 1.15f, P(0.65f, 0.30f, 0.30f, 0.30f, 0.7f, 0.20f), 220, EnemyTrait.Vampiric, EnemyTrait.Swift);
            Add("Gor", "The Mountain", WarriorId.Wrestler, 1.25f, P(0.55f, 0.30f, 0.30f, 0.05f, 0.6f, 0.35f), 260, EnemyTrait.Giant, EnemyTrait.IronSkin);
            Add("Black Knight", "Oathbreaker", WarriorId.Knight, 1.3f, P(0.55f, 0.25f, 0.55f, 0.15f, 0.7f, 0.30f), 300, EnemyTrait.Thorns, EnemyTrait.Regeneration);
            Add("Shogun Takeda", "Final Boss", WarriorId.Samurai, 1.45f, P(0.70f, 0.22f, 0.45f, 0.30f, 0.8f, 0.35f), 400, EnemyTrait.Berserk, EnemyTrait.Vampiric, EnemyTrait.Swift);
        }
    }
}
