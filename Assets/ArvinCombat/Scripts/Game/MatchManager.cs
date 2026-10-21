using System;
using System.Collections;
using UnityEngine;

namespace ArvinCombat
{
    public class MatchSetup
    {
        public WarriorId playerWarrior;
        public string playerName = "Player";
        public int[] playerRanks;
        public WarriorId enemyWarrior;
        public string enemyName = "Enemy";
        public int[] enemyRanks;
        public float enemyDifficulty = 1f;
        public EnemyTrait[] enemyTraits = new EnemyTrait[0];
        public AIProfile ai = new AIProfile();
        public int roundsToWin = 2;
        public float roundTime = 75f;
    }

    public class MatchResult
    {
        public bool playerWon;
        public int playerRounds, enemyRounds;
        public float playerHealthLeft01;
    }

    /// <summary>Runs one fight: spawns both fighters, plays the rounds, reports the winner.</summary>
    public class MatchManager : MonoBehaviour
    {
        public FightCamera cam;
        public HitFx fx;
        public GameUI ui;

        public Fighter player { get; private set; }
        public Fighter enemy { get; private set; }
        public int playerRounds { get; private set; }
        public int enemyRounds { get; private set; }
        public int round { get; private set; }
        public float timeLeft { get; private set; }
        public bool running { get; private set; }
        public bool paused;

        MatchSetup _setup;
        Action<MatchResult> _onFinished;
        float _hitStopUntil;
        bool _slowMo;
        bool _roundActive;

        static readonly Vector3 PlayerStart = new Vector3(0f, 0f, -2.8f);
        static readonly Vector3 EnemyStart = new Vector3(0f, 0f, 2.8f);

        public void StartMatch(MatchSetup setup, Action<MatchResult> onFinished)
        {
            StopMatch();
            _setup = setup;
            _onFinished = onFinished;

            var pDef = WarriorDatabase.Get(setup.playerWarrior);
            var eDef = WarriorDatabase.Get(setup.enemyWarrior);
            var pStats = RuntimeStats.Build(pDef, setup.playerRanks, 1f, null);
            var eStats = RuntimeStats.Build(eDef, setup.enemyRanks, setup.enemyDifficulty, setup.enemyTraits);

            player = Spawn(pDef, pStats, null, setup.playerName, true, new PlayerBrain { cameraTransform = cam != null ? cam.transform : null }, PlayerStart, Quaternion.identity);
            enemy = Spawn(eDef, eStats, setup.enemyTraits, setup.enemyName, false, new AIBrain(setup.ai), EnemyStart, Quaternion.Euler(0f, 180f, 0f));
            player.opponent = enemy;
            enemy.opponent = player;

            if (cam != null) { cam.player = player; cam.target = enemy; cam.Snap(); }
            running = true;
            StartCoroutine(MatchFlow());
        }

        public void StopMatch()
        {
            StopAllCoroutines();
            running = false;
            _roundActive = false;
            paused = false;
            _slowMo = false;
            Time.timeScale = 1f;
            if (player != null) Destroy(player.gameObject);
            if (enemy != null) Destroy(enemy.gameObject);
            player = enemy = null;
        }

        Fighter Spawn(WarriorDef def, RuntimeStats stats, EnemyTrait[] traits, string displayName, bool isPlayer,
            IFighterBrain brain, Vector3 pos, Quaternion rot)
        {
            var f = FighterFactory.Create(def, stats, traits, displayName, isPlayer, brain, pos, rot);
            f.OnDamaged += HandleDamaged;
            f.OnPopup += (who, text) => { if (ui != null) ui.Popup(who, text); };
            return f;
        }

        void HandleDamaged(Fighter victim, float amount, Vector3 point, bool blocked)
        {
            if (fx != null && amount > 0f) fx.Spawn(point, blocked, amount);
            if (cam != null) cam.Shake(blocked ? 0.25f : Mathf.Clamp(amount / 90f, 0.3f, 1.2f));
            if (ui != null && amount >= 1f) ui.DamageNumber(point, amount, victim.isPlayer, blocked);
            if (!blocked && amount >= 45f) _hitStopUntil = Time.unscaledTime + Mathf.Clamp(amount / 2000f, 0.03f, 0.09f);
        }

        void Update()
        {
            if (!running) return;
            float ts = 1f;
            if (paused) ts = 0f;
            else if (Time.unscaledTime < _hitStopUntil) ts = 0.05f;
            else if (_slowMo) ts = 0.3f;
            Time.timeScale = ts;

            if (_roundActive && !paused) timeLeft = Mathf.Max(0f, timeLeft - Time.deltaTime);
            if (ui != null && player != null && enemy != null)
                ui.UpdateHud(player, enemy, timeLeft, playerRounds, enemyRounds, _setup.roundsToWin);
        }

        IEnumerator MatchFlow()
        {
            playerRounds = enemyRounds = 0;
            round = 0;
            int need = Mathf.Max(1, _setup.roundsToWin);
            if (ui != null) ui.ShowHud(true);

            while (playerRounds < need && enemyRounds < need && round < 9)
            {
                round++;
                player.ResetForRound(PlayerStart, Quaternion.identity);
                enemy.ResetForRound(EnemyStart, Quaternion.Euler(0f, 180f, 0f));
                if (cam != null) cam.Snap();
                timeLeft = _setup.roundTime;

                if (ui != null) ui.CenterMessage(round == need * 2 - 1 && round > 1 ? "FINAL ROUND" : "ROUND " + round, 1.3f);
                yield return new WaitForSeconds(1.4f);
                if (ui != null) ui.CenterMessage("FIGHT!", 0.8f);
                player.SetFrozen(false);
                enemy.SetFrozen(false);
                _roundActive = true;

                while (!player.IsDead && !enemy.IsDead && timeLeft > 0f) yield return null;
                _roundActive = false;

                // decide the round
                bool timeOut = !player.IsDead && !enemy.IsDead;
                Fighter winner = null;
                if (player.IsDead && !enemy.IsDead) winner = enemy;
                else if (enemy.IsDead && !player.IsDead) winner = player;
                else if (timeOut) winner = player.Health01 >= enemy.Health01 ? player : enemy;

                if (winner == player) playerRounds++;
                else if (winner == enemy) enemyRounds++;

                _slowMo = !timeOut;
                if (ui != null) ui.CenterMessage(timeOut ? "TIME!" : (winner == null ? "DOUBLE K.O." : "K.O."), 1.6f);
                yield return new WaitForSecondsRealtime(1.2f);
                _slowMo = false;

                if (winner != null)
                {
                    winner.SetVictory();
                    var loser = winner == player ? enemy : player;
                    if (!loser.IsDead) loser.SetFrozen(true);
                    if (ui != null) ui.CenterMessage(winner.fighterName + " WINS THE ROUND", 1.5f);
                }
                yield return new WaitForSecondsRealtime(1.8f);
            }

            bool won = playerRounds > enemyRounds;
            if (ui != null) ui.CenterMessage(won ? "VICTORY" : "DEFEAT", 2f);
            yield return new WaitForSecondsRealtime(2.2f);

            var result = new MatchResult
            {
                playerWon = won, playerRounds = playerRounds, enemyRounds = enemyRounds,
                playerHealthLeft01 = player != null ? player.Health01 : 0f
            };
            running = false;
            Time.timeScale = 1f;
            if (ui != null) ui.ShowHud(false);
            _onFinished?.Invoke(result);
        }
    }
}
