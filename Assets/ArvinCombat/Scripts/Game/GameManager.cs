using System.Collections;
using UnityEngine;

namespace ArvinCombat
{
    public enum PlayMode { Campaign, QuickFight, Online }

    /// <summary>
    /// Game flow: menus -> warrior select -> levels -> fight -> XP reward -> upgrades.
    /// Everything runs in one scene to keep the web build small and loading fast.
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }

        public MatchManager match;
        public GameUI ui;
        public FightCamera fightCamera;
        public OnlineMatch online;

        public PlayerProfile profile { get; private set; }
        public GamePlatformBridge platform { get; private set; }
        /// <summary>Logged in on the Arvin Games site: the name comes from the site and can't be edited.</summary>
        public bool NameFromSite => platform != null && platform.OnSite && !platform.IsGuest && !string.IsNullOrEmpty(platform.DisplayName);
        public PlayMode mode { get; private set; }
        int _currentLevel;
        MatchSetup _lastSetup;
        bool _paused;
        bool _searching;
        bool _upgradesFromLevels;

        void Awake()
        {
            Instance = this;
            profile = PlayerProfile.Load();
            if (string.IsNullOrWhiteSpace(profile.playerName))
            {
                profile.playerName = "Warrior" + Random.Range(1000, 9999);
                profile.Save();
            }
            Application.targetFrameRate = 60;

            // the online component is added at runtime if the scene was generated before it existed
            if (online == null) online = GetComponent<OnlineMatch>();
            if (online == null) online = gameObject.AddComponent<OnlineMatch>();
            online.cam = fightCamera;
            online.fx = match != null ? match.fx : null;
            online.ui = ui;

            platform = GamePlatformBridge.Create();
            platform.OnReady += OnPlatformReady;
            platform.OnResumed += () => online.OnPlatformResumed();
        }

        void OnPlatformReady()
        {
            if (NameFromSite) SetPlayerName(platform.DisplayName);
            else if (platform.OnSite && profile.playerName.StartsWith("Warrior"))
                SetPlayerName("Guest " + Random.Range(1000, 9999)); // guest: temporary name (can be changed)
            if (ui != null && ui.CurrentScreen == "main" && !match.running && !online.InMatch) ui.ShowMainMenu();
        }

        void Start()
        {
            ui.Build(this);
            ui.ShowMainMenu();
            platform.LoadingFinished();
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape) && (match.running || online.InMatch)) SetPaused(!_paused);

            // slow orbit around the arena while in menus
            if (!match.running && match.player == null && !online.HasFighters && fightCamera != null)
            {
                float a = Time.unscaledTime * 6f;
                var pos = new Vector3(Mathf.Sin(a * Mathf.Deg2Rad) * 13f, 5.5f, Mathf.Cos(a * Mathf.Deg2Rad) * 13f);
                fightCamera.transform.position = pos;
                fightCamera.transform.rotation = Quaternion.LookRotation(new Vector3(0, 1f, 0) - pos);
            }
        }

        // ------------------------------------------------------------ menu actions

        public void SetPlayerName(string n)
        {
            n = (n ?? "").Trim();
            if (n.Length == 0) return;
            if (n.Length > 16) n = n.Substring(0, 16);
            profile.playerName = n;
            profile.Save();
        }

        public void ChooseMode(PlayMode m)
        {
            mode = m;
            string sub = m == PlayMode.Campaign ? "Offline campaign: beat 10 unique enemies, each with their own powers."
                       : m == PlayMode.Online ? "Pick the warrior you will take online."
                       : "Quick fight against a random opponent.";
            ui.ShowWarriorSelect(sub);
        }

        public void SelectWarrior(WarriorId id)
        {
            profile.selectedWarrior = (int)id;
            profile.Save();
            switch (mode)
            {
                case PlayMode.Campaign: ui.ShowLevelSelect(); break;
                case PlayMode.QuickFight: StartQuickFight(); break;
                case PlayMode.Online: OpenOnline(); break;
            }
        }

        public void OpenUpgrades(bool fromLevels)
        {
            _upgradesFromLevels = fromLevels;
            ui.ShowUpgrades((WarriorId)profile.selectedWarrior);
        }

        public void BackFromUpgrades()
        {
            if (_upgradesFromLevels) ui.ShowLevelSelect();
            else ui.ShowMainMenu();
        }

        // ------------------------------------------------------------ fights

        public void StartLevel(int index)
        {
            var lvl = LevelDatabase.Levels[Mathf.Clamp(index, 0, LevelDatabase.Levels.Count - 1)];
            _currentLevel = lvl.index;
            mode = PlayMode.Campaign;
            var setup = new MatchSetup
            {
                playerWarrior = (WarriorId)profile.selectedWarrior,
                playerName = profile.playerName,
                playerRanks = profile.GetRanks((WarriorId)profile.selectedWarrior),
                enemyWarrior = lvl.warrior,
                enemyName = lvl.enemyName,
                enemyDifficulty = lvl.difficulty,
                enemyTraits = lvl.traits,
                ai = lvl.ai,
                roundsToWin = lvl.roundsToWin
            };
            Begin(setup);
        }

        public void StartQuickFight()
        {
            mode = PlayMode.QuickFight;
            var enemyId = (WarriorId)Random.Range(0, WarriorDatabase.Count);
            var def = WarriorDatabase.Get(enemyId);
            var setup = new MatchSetup
            {
                playerWarrior = (WarriorId)profile.selectedWarrior,
                playerName = profile.playerName,
                playerRanks = profile.GetRanks((WarriorId)profile.selectedWarrior),
                enemyWarrior = enemyId,
                enemyName = "Rival " + def.displayName,
                enemyDifficulty = 1f,
                ai = new AIProfile { aggression = 0.5f, reactionTime = 0.35f, blockSkill = 0.3f, dodgeSkill = 0.15f, comboSkill = 0.55f, heavyChance = 0.2f },
                roundsToWin = 2
            };
            Begin(setup);
        }

        void Begin(MatchSetup setup)
        {
            _lastSetup = setup;
            _paused = false;
            ui.HideMenus();
            platform.GameplayStart();
            match.StartMatch(setup, OnMatchFinished);
        }

        void OnMatchFinished(MatchResult r)
        {
            int xp;
            string extra = "";
            bool canNext = false;

            if (r.playerWon) profile.wins++; else profile.losses++;
            platform.GameplayStop();

            if (mode == PlayMode.Campaign)
            {
                var lvl = LevelDatabase.Levels[_currentLevel];
                if (r.playerWon)
                {
                    bool firstClear = _currentLevel >= profile.highestLevelUnlocked;
                    xp = lvl.xpReward + (firstClear ? lvl.xpReward / 2 : 0);
                    if (r.playerHealthLeft01 > 0.8f) { xp += 25; extra = "Flawless bonus +25"; }
                    if (firstClear)
                    {
                        profile.highestLevelUnlocked = Mathf.Min(LevelDatabase.Levels.Count - 1, _currentLevel + 1);
                        if (_currentLevel == LevelDatabase.Levels.Count - 1) extra += (extra.Length > 0 ? "\n" : "") + "CAMPAIGN COMPLETE!";
                        else extra += (extra.Length > 0 ? "\n" : "") + "New enemy unlocked!";
                    }
                    canNext = _currentLevel < LevelDatabase.Levels.Count - 1;
                }
                else { xp = 15; extra = "Upgrade your warrior and try again."; }
            }
            else
            {
                xp = r.playerWon ? 40 : 10;
            }

            profile.AddXp(xp); // also saves
            ui.ShowResult(r, xp, canNext, extra);
        }

        public void NextLevel() => StartLevel(_currentLevel + 1);

        public void Retry()
        {
            SetPausedSilent(false);
            if (mode == PlayMode.Online) { ReturnToMenuScene(); OpenOnline(); FindOnlineMatch(); return; }
            if (_lastSetup == null) { BackToMenu(); return; }
            // refresh upgrades in case the player bought some
            _lastSetup.playerRanks = profile.GetRanks(_lastSetup.playerWarrior);
            Begin(_lastSetup);
        }

        public void ReturnToMenuScene()
        {
            SetPausedSilent(false);
            match.StopMatch();
            online.Leave();
            ui.ShowHud(false);
            platform.GameplayStop();
        }

        public void BackToMenu()
        {
            ReturnToMenuScene();
            ui.ShowMainMenu();
        }

        public void SetPaused(bool p)
        {
            SetPausedSilent(p);
            ui.ShowPause(p, mode == PlayMode.Online);
        }

        void SetPausedSilent(bool p)
        {
            _paused = p;
            match.paused = p && mode != PlayMode.Online; // online games never freeze
            if (!p) ui.ShowPause(false);
        }

        // ------------------------------------------------------------ online

        public void OpenOnline()
        {
            mode = PlayMode.Online;
            _searching = false;
            ui.ShowOnline("Press FIND MATCH: you will be paired with an online player of a similar level.", false);
        }

        public void FindOnlineMatch()
        {
            mode = PlayMode.Online;
            _searching = true;
            ui.ShowOnline("Connecting to the server...", true);
            online.FindMatch(profile, platform != null ? platform.PlayerId : "",
                (status, searching) => { _searching = searching; if (!online.InMatch) ui.ShowOnline(status, searching); },
                () => { _searching = false; platform.GameplayStart(); },
                OnOnlineFinished);
        }

        void OnOnlineFinished(MatchResult r, string extra)
        {
            if (r.playerWon) profile.wins++; else profile.losses++;
            platform.GameplayStop();
            platform.TrackEvent("MatchFinished", "{\"place\":" + (r.playerWon ? 1 : 2) + ",\"players\":2}");
            int xp = r.playerWon ? 60 : 15;
            profile.AddXp(xp); // also saves
            ui.ShowResult(r, xp, false, extra);
        }

        public void CancelOnline()
        {
            _searching = false;
            online.Leave();
            ui.ShowMainMenu();
        }
    }
}
