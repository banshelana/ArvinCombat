using System;
using UnityEngine;

namespace ArvinCombat
{
    /// <summary>
    /// Saved player data: name, XP power, campaign progress and warrior upgrades.
    /// Stored in PlayerPrefs (works in WebGL through IndexedDB).
    /// For online mode this will later be mirrored on the server so nobody can cheat upgrades.
    /// </summary>
    [Serializable]
    public class PlayerProfile
    {
        public const int StatCount = 5;
        public const int MaxRank = 10;
        const string Key = "ArvinCombat.Profile.v1";

        public string playerName = "";
        public int xp = 0;
        public int totalXpEarned = 0;
        public int highestLevelUnlocked = 0; // index into LevelDatabase
        public int selectedWarrior = 0;
        public int wins = 0;
        public int losses = 0;
        public int[] ranks = new int[6 * StatCount]; // [warrior * StatCount + stat]

        public static PlayerProfile Load()
        {
            PlayerProfile p = null;
            try
            {
                var json = PlayerPrefs.GetString(Key, "");
                if (!string.IsNullOrEmpty(json)) p = JsonUtility.FromJson<PlayerProfile>(json);
            }
            catch (Exception e) { Debug.LogWarning("Profile load failed: " + e.Message); }
            if (p == null) p = new PlayerProfile();
            if (p.ranks == null || p.ranks.Length != 6 * StatCount)
            {
                var r = new int[6 * StatCount];
                if (p.ranks != null) Array.Copy(p.ranks, r, Mathf.Min(p.ranks.Length, r.Length));
                p.ranks = r;
            }
            return p;
        }

        public void Save()
        {
            PlayerPrefs.SetString(Key, JsonUtility.ToJson(this));
            PlayerPrefs.Save();
        }

        public int GetRank(WarriorId w, UpgradeStat s) => ranks[(int)w * StatCount + (int)s];

        public int[] GetRanks(WarriorId w)
        {
            var r = new int[StatCount];
            for (int i = 0; i < StatCount; i++) r[i] = ranks[(int)w * StatCount + i];
            return r;
        }

        /// <summary>Power rating used by the matchmaker to pair similar players.</summary>
        public int PowerRating(WarriorId w)
        {
            int sum = 0;
            for (int i = 0; i < StatCount; i++) sum += ranks[(int)w * StatCount + i];
            return sum;
        }

        public static int UpgradeCost(int currentRank) => 60 + 45 * currentRank;

        public bool CanUpgrade(WarriorId w, UpgradeStat s)
        {
            int r = GetRank(w, s);
            return r < MaxRank && xp >= UpgradeCost(r);
        }

        public bool TryUpgrade(WarriorId w, UpgradeStat s)
        {
            if (!CanUpgrade(w, s)) return false;
            int r = GetRank(w, s);
            xp -= UpgradeCost(r);
            ranks[(int)w * StatCount + (int)s] = r + 1;
            Save();
            return true;
        }

        public void AddXp(int amount)
        {
            xp += amount;
            totalXpEarned += amount;
            Save();
        }
    }
}
