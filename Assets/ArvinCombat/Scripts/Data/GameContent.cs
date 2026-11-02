using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArvinCombat
{
    /// <summary>
    /// Achievements and store items, read from Resources/ArvinCombat/game-content.json.
    /// The SAME file is imported into Arvin Games Studio (format "arvinplay.game-content"),
    /// so keys, prices and titles always match. The site is still the authority for prices and rewards.
    /// </summary>
    [Serializable]
    public class GameContent
    {
        [Serializable] public class Loc
        {
            public string fa, en;
            public string Get(string locale) => locale == "fa" && !string.IsNullOrEmpty(fa) ? fa : (en ?? fa ?? "");
        }

        [Serializable] public class Unlock { public string type; public int target; public int minutes; public int count; public string @event; }

        [Serializable] public class Achievement
        {
            public string key;
            public Loc title = new Loc(), description = new Loc();
            public string rarity;
            public int points, coinReward;
            public bool hidden;
            public bool active = true;
            public Unlock unlock = new Unlock();
            public int Target => unlock != null && unlock.type == "sdk" ? unlock.target : 0;
            public bool FromGame => unlock == null || string.IsNullOrEmpty(unlock.type) || unlock.type == "sdk";
        }

        [Serializable] public class StoreItem
        {
            public string key;
            public Loc title = new Loc(), description = new Loc();
            public int price;
            public string type;          // "consumable" | "permanent"
            public int maxPerUser;
            public bool active = true;
            public bool IsPermanent => type == "permanent";
        }

        public string format;
        public int version;
        public List<Achievement> achievements = new List<Achievement>();
        public List<StoreItem> storeItems = new List<StoreItem>();

        static GameContent _loaded;

        public static GameContent Get()
        {
            if (_loaded != null) return _loaded;
            var ta = Resources.Load<TextAsset>("ArvinCombat/game-content");
            try { _loaded = ta != null ? JsonUtility.FromJson<GameContent>(ta.text) : null; }
            catch (Exception e) { Debug.LogWarning("ArvinCombat: bad game-content.json: " + e.Message); }
            if (_loaded == null) _loaded = new GameContent();
            return _loaded;
        }

        public StoreItem FindItem(string key) => storeItems.Find(i => i.key == key);
        public Achievement FindAchievement(string key) => achievements.Find(a => a.key == key);

        // ---------------------------------------------------------------- game rules tied to keys

        public static readonly string[] WarriorKeys = { "samurai", "karate", "spearman", "knight", "boxer", "wrestler" };
        public static string WarriorKey(WarriorId w) => WarriorKeys[(int)w];

        /// <summary>Store key that unlocks a warrior, or null if the warrior is free.</summary>
        public static string WarriorItem(WarriorId w)
        {
            switch (w)
            {
                case WarriorId.Samurai: return "warrior_samurai";
                case WarriorId.Spearman: return "warrior_spearman";
                case WarriorId.Wrestler: return "warrior_wrestler";
                default: return null; // Karate, Boxer, Knight are free
            }
        }

        /// <summary>Campaign level (0-based index) whose first clear unlocks the warrior for free; -1 = none.</summary>
        public static int WarriorFreeAtLevel(WarriorId w)
        {
            switch (w)
            {
                case WarriorId.Spearman: return 5;   // beat level 6 (Ryo)
                case WarriorId.Samurai: return 7;    // beat level 8 (Gor)
                case WarriorId.Wrestler: return 9;   // beat level 10 (Shogun Takeda)
                default: return -1;
            }
        }

        public static string SkinItem(WarriorId w, string style) => "skin_" + WarriorKey(w) + "_" + style;
        public static readonly string[] SkinStyles = { "gold", "shadow" };

        public static int XpPackAmount(string key) => key == "xp_pack_small" ? 150 : key == "xp_pack_large" ? 750 : 0;
        public const string ContinueItem = "continue";
    }
}
