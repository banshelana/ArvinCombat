using System;
using System.Collections;
using UnityEngine;

namespace ArvinCombat
{
    /// <summary>
    /// Online 1v1: connects to the ArvinCombat server, waits for the matchmaker, then runs the fight.
    /// Each game simulates its own fighter and shows the opponent from network snapshots.
    /// Hits are sent to the opponent's game, which decides block / parry / dodge (fair for the defender).
    /// The server runs the rounds and the timer.
    /// </summary>
    public class OnlineMatch : MonoBehaviour
    {
        public const int ProtocolVersion = 2;

        public FightCamera cam;
        public HitFx fx;
        public GameUI ui;

        public Fighter local { get; private set; }
        public Fighter remote { get; private set; }
        public bool InMatch => _phase == Phase.InMatch || _phase == Phase.JoiningRoom;
        public bool Busy => _phase != Phase.Idle;
        public bool HasFighters => local != null;

        enum Phase { Idle, Connecting, Queued, JoiningRoom, InMatch }
        Phase _phase = Phase.Idle;
        NetSocket _sock;
        float _connectStart;
        int _roomPort;
        string _roomPath = "room", _ticket = "";
        float _joinDeadline, _retryAt, _pingTimer;
        bool _roomHello;
        bool _reconnectUsed;      // one automatic reconnect per match
        string _playerId = "";
        float _sendTimer;
        float _timeLeft;
        bool _fighting;
        int _side;
        int _myScore, _oppScore, _need = 2;
        float _roundTime = 75f;
        string _oppName = "";
        PlayerProfile _profile;
        Action<string, bool> _status;
        Action _onStart;
        Action<MatchResult, string> _onFinished;

        static readonly Vector3 StartA = new Vector3(0f, 0f, -2.8f);
        static readonly Vector3 StartB = new Vector3(0f, 0f, 2.8f);
        static readonly Quaternion RotA = Quaternion.identity;
        static readonly Quaternion RotB = Quaternion.Euler(0f, 180f, 0f);

        // full addresses from StreamingAssets/arvincombat.json (relative paths would hit the site's domain)
        static OnlineConfig Cfg => OnlineConfig.Current;
        string MatchUrl => Cfg.WsBase() + "/" + Cfg.matchPath;
        string RoomUrl => Cfg.WsBase() + "/" + (string.IsNullOrEmpty(_roomPath) ? Cfg.roomPath : _roomPath) + "/" + _roomPort;

        static void ReportError(string msg, string detail)
        {
            if (GamePlatformBridge.Instance != null) GamePlatformBridge.Instance.ReportError(msg, detail);
        }

        // ------------------------------------------------------------ public API

        public void FindMatch(PlayerProfile profile, string playerId, Action<string, bool> status, Action onStart, Action<MatchResult, string> onFinished)
        {
            Leave();
            _profile = profile;
            _playerId = playerId ?? "";
            _reconnectUsed = false;
            _status = status;
            _onStart = onStart;
            _onFinished = onFinished;
            _sock = new NetSocket();
            _sock.Connect(MatchUrl);
            _pingTimer = 20f;
            _connectStart = Time.unscaledTime;
            _phase = Phase.Connecting;
            _status?.Invoke("Connecting to the server...", true);
        }

        /// <summary>Cancel search or quit the current fight (counts as a loss).</summary>
        public void Leave()
        {
            StopAllCoroutines();
            if (_sock != null)
            {
                if (_sock.State == 1) Send(new NetMsg { t = InMatch ? "leave" : "cancel" });
                _sock.Close();
                _sock = null;
            }
            _phase = Phase.Idle;
            _fighting = false;
            DestroyFighters();
        }

        void DestroyFighters()
        {
            if (local != null) Destroy(local.gameObject);
            if (remote != null) Destroy(remote.gameObject);
            local = remote = null;
        }

        // ------------------------------------------------------------ loop

        void Update()
        {
            if (_sock == null && _phase != Phase.JoiningRoom) return;
            float now = Time.unscaledTime;

            if (_phase == Phase.JoiningRoom)
            {
                // the room server may need a few seconds to boot: keep retrying until the deadline
                if (_sock == null)
                {
                    if (now >= _retryAt) { _sock = new NetSocket(); _sock.Connect(RoomUrl); _roomHello = false; }
                    return;
                }
                int rs = _sock.State;
                if (rs == 1 && !_roomHello)
                {
                    var ranks = _profile.GetRanks((WarriorId)_profile.selectedWarrior);
                    Send(new NetMsg { t = "hello", ticket = _ticket, playerId = _playerId, name = _profile.playerName, warrior = _profile.selectedWarrior, ranks = ranks, ver = ProtocolVersion });
                    _roomHello = true;
                    _pingTimer = 20f;
                }
                else if (rs == 3)
                {
                    _sock.Close();
                    _sock = null;
                    if (now > _joinDeadline)
                    {
                        ReportError("Room connection failed", "url=" + RoomUrl + (_reconnectUsed ? " reconnect" : ""));
                        FinishLocal(false, _reconnectUsed ? "Connection to the arena was lost." : "Could not join the arena. Please try again.");
                        return;
                    }
                    _retryAt = now + 1f;
                    return;
                }
            }
            else
            {
                int st = _sock.State;
                if (_phase == Phase.Connecting)
                {
                    if (st == 1)
                    {
                        var ranks = _profile.GetRanks((WarriorId)_profile.selectedWarrior);
                        Send(new NetMsg { t = "hello", playerId = _playerId, name = _profile.playerName, warrior = _profile.selectedWarrior, ranks = ranks, ver = ProtocolVersion });
                        Send(new NetMsg { t = "queue" });
                        _phase = Phase.Queued;
                        _status?.Invoke("Searching for an opponent...", true);
                    }
                    else if (st == 3 || now - _connectStart > 10f)
                    {
                        ReportError("Match connection failed", "url=" + MatchUrl);
                        Fail("Could not reach the game server. Check your internet connection and try again.");
                        return;
                    }
                }
                else if (st == 3)
                {
                    if (_phase == Phase.InMatch && !_reconnectUsed)
                    {
                        // one automatic retry: rejoin the same room with the same ticket
                        _reconnectUsed = true;
                        ReportError("Room connection lost", "url=" + RoomUrl + " retrying");
                        _sock.Close();
                        _sock = null;
                        _phase = Phase.JoiningRoom;
                        _joinDeadline = now + 12f;
                        _retryAt = now + 1f;
                        if (ui != null) ui.CenterMessage("RECONNECTING...", 3f);
                        return;
                    }
                    if (_phase == Phase.InMatch) { ReportError("Room connection lost", "url=" + RoomUrl); FinishLocal(false, "Connection to the server was lost."); }
                    else { ReportError("Match connection lost", "url=" + MatchUrl); Fail("Disconnected from the server."); }
                    return;
                }
            }

            // keep-alive: Cloudflare drops sockets that are silent for 30 seconds
            _pingTimer -= Time.unscaledDeltaTime;
            if (_pingTimer <= 0f && _sock != null && _sock.State == 1)
            {
                _pingTimer = 20f;
                Send(new NetMsg { t = "ping" });
            }

            while (_sock != null && _sock.TryReceive(out var json)) Handle(json);

            if (InMatch && local != null && remote != null)
            {
                _sendTimer -= Time.unscaledDeltaTime;
                if (_sendTimer <= 0f && _phase == Phase.InMatch)
                {
                    _sendTimer = 0.05f; // 20 Hz
                    _sock.Send(JsonUtility.ToJson(local.BuildNetState()));
                }
                if (_fighting) _timeLeft = Mathf.Max(0f, _timeLeft - Time.deltaTime);
                if (ui != null) ui.UpdateHud(local, remote, _timeLeft, _myScore, _oppScore, _need);
            }
        }

        void Fail(string msg)
        {
            if (_sock != null) { _sock.Close(); _sock = null; }
            _phase = Phase.Idle;
            DestroyFighters();
            if (ui != null) ui.ShowHud(false);
            _status?.Invoke(msg, false);
        }

        void Send(NetMsg m) { if (_sock != null) _sock.Send(JsonUtility.ToJson(m)); }

        // ------------------------------------------------------------ messages

        void Handle(string json)
        {
            NetMsg m;
            try { m = JsonUtility.FromJson<NetMsg>(json); } catch { return; }
            if (m == null || string.IsNullOrEmpty(m.t)) return;

            switch (m.t)
            {
                case "welcome":
                case "pong":
                    break;
                case "joined":
                    _phase = Phase.InMatch;
                    if (ui != null) ui.CenterMessage(_reconnectUsed ? "RECONNECTED" : "GET READY", 1.2f);
                    break;
                case "sync": OnSync(m); break;
                case "queued":
                    _status?.Invoke($"Searching for an opponent...   ({m.online} players online)", true);
                    break;
                case "err":
                    Fail(m.txt);
                    break;
                case "match": StartOnline(m); break;
                case "round": OnRound(m); break;
                case "fight": OnFight(m); break;
                case "rend": OnRoundEnd(m); break;
                case "mend": OnMatchEnd(m); break;
                case "st":
                    if (remote != null) remote.ApplyRemoteState(m);
                    break;
                case "hit":
                    if (local != null && remote != null && _fighting)
                    {
                        var atk = remote.GetAttackByKey(m.atk);
                        if (atk != null) local.ReceiveHit(remote, atk, m.dmg);
                    }
                    break;
                case "res":
                    if (remote != null && m.amt > 0f) ShowHitOnRemote(m.amt, m.blk);
                    break;
                case "pop":
                    if (remote != null && ui != null && !string.IsNullOrEmpty(m.txt)) ui.Popup(remote, m.txt);
                    break;
                case "cmd":
                    if (local != null && m.txt == "stun") local.EnterStun(m.stun);
                    break;
            }
        }

        void StartOnline(NetMsg m)
        {
            // the match service found an opponent: leave it and join the room server
            _roomPort = m.port;
            _roomPath = string.IsNullOrEmpty(m.roomPath) ? "room" : m.roomPath;
            _ticket = m.ticket;
            if (_sock != null) { _sock.Close(); _sock = null; }
            _phase = Phase.JoiningRoom;
            _joinDeadline = Time.unscaledTime + 25f;
            _retryAt = Time.unscaledTime + 0.5f;
            _side = m.side;
            _need = Mathf.Max(1, m.rounds);
            _roundTime = m.roundTime > 0 ? m.roundTime : 75f;
            _myScore = _oppScore = 0;
            _oppName = string.IsNullOrEmpty(m.oppName) ? "Opponent" : m.oppName;

            var myId = (WarriorId)_profile.selectedWarrior;
            var oppId = (WarriorId)Mathf.Clamp(m.oppWarrior, 0, WarriorDatabase.Count - 1);
            var myDef = WarriorDatabase.Get(myId);
            var oppDef = WarriorDatabase.Get(oppId);
            var myStats = RuntimeStats.Build(myDef, _profile.GetRanks(myId), 1f, null);
            var oppStats = RuntimeStats.Build(oppDef, m.oppRanks, 1f, null);

            DestroyFighters();
            local = FighterFactory.Create(myDef, myStats, null, _profile.playerName, true,
                new PlayerBrain { cameraTransform = cam != null ? cam.transform : null }, MyStart, MyRot);
            remote = FighterFactory.Create(oppDef, oppStats, null, _oppName, false, new IdleBrain(), OppStart, OppRot, true);
            local.opponent = remote;
            remote.opponent = local;

            local.OnRemoteHit = (atk, dmg) => Send(new NetMsg { t = "hit", atk = atk.anim, dmg = dmg });
            local.OnDamaged += (victim, amount, point, blocked) =>
            {
                ShowHitFx(point, amount, true, blocked);
                Send(new NetMsg { t = "res", amt = amount, blk = blocked });
            };
            local.OnPopup += (who, text) =>
            {
                if (ui != null) ui.Popup(who, text);
                Send(new NetMsg { t = "pop", txt = text });
            };
            local.OnDeath += _ => Send(new NetMsg { t = "dead" });
            remote.OnRemoteCommand += (cmd, value) => Send(new NetMsg { t = "cmd", txt = cmd, stun = value });

            if (cam != null) { cam.player = local; cam.target = remote; cam.Snap(); }
            if (ui != null)
            {
                ui.HideMenus();
                ui.ShowHud(true);
                ui.CenterMessage(_profile.playerName + "  VS  " + _oppName, 2.5f);
            }
            _onStart?.Invoke();
        }

        Vector3 MyStart => _side == 0 ? StartA : StartB;
        Quaternion MyRot => _side == 0 ? RotA : RotB;
        Vector3 OppStart => _side == 0 ? StartB : StartA;
        Quaternion OppRot => _side == 0 ? RotB : RotA;

        void Scores(NetMsg m)
        {
            _myScore = _side == 0 ? m.sa : m.sb;
            _oppScore = _side == 0 ? m.sb : m.sa;
        }

        void OnRound(NetMsg m)
        {
            if (local == null) return;
            Scores(m);
            _fighting = false;
            local.ResetForRound(MyStart, MyRot);
            remote.ResetRemote(OppStart, OppRot);
            _timeLeft = _roundTime;
            if (cam != null) cam.Snap();
            if (ui != null) ui.CenterMessage(m.n > 1 && _myScore == _need - 1 && _oppScore == _need - 1 ? "FINAL ROUND" : "ROUND " + m.n, 1.6f);
        }

        /// <summary>The site paused the game (or the tab was hidden) and it is back: ask the room for the current state.</summary>
        public void OnPlatformResumed()
        {
            if (_phase == Phase.InMatch && _sock != null && _sock.State == 1) Send(new NetMsg { t = "resync" });
        }

        // full room state after a resume or a reconnect. s: 0 waiting, 1 countdown, 2 fight, 3 between rounds
        void OnSync(NetMsg m)
        {
            if (local == null) return;
            Scores(m);
            if (m.roundTime > 0f) _timeLeft = m.roundTime;
            bool fight = m.s == 2;
            if (fight && !_fighting) { _fighting = true; local.SetFrozen(false); }
            else if (!fight && _fighting) { _fighting = false; if (!local.IsDead) local.SetFrozen(true); }
        }

        void OnFight(NetMsg m)
        {
            if (local == null) return;
            if (m.roundTime > 0) _timeLeft = m.roundTime;
            _fighting = true;
            local.SetFrozen(false);
            if (ui != null) ui.CenterMessage("FIGHT!", 0.8f);
        }

        void OnRoundEnd(NetMsg m)
        {
            if (local == null) return;
            _fighting = false;
            Scores(m);
            bool iWon = m.w == _side;
            bool draw = m.w < 0;
            if (!local.IsDead) { if (iWon) local.SetVictory(); else local.SetFrozen(true); }
            string first = m.reason == "time" ? "TIME!" : "K.O.";
            string who = draw ? "DRAW" : (iWon ? local.fighterName : remote.fighterName) + " WINS THE ROUND";
            StartCoroutine(TwoMessages(first, who));
        }

        IEnumerator TwoMessages(string a, string b)
        {
            if (ui != null) ui.CenterMessage(a, 1.3f);
            yield return new WaitForSecondsRealtime(1.3f);
            if (ui != null) ui.CenterMessage(b, 1.8f);
        }

        void OnMatchEnd(NetMsg m)
        {
            Scores(m);
            bool won = m.w == _side;
            string extra = m.reason == "left" ? (won ? _oppName + " left the match." : "You left the match.") : "Opponent: " + _oppName;
            FinishLocal(won, extra);
        }

        void FinishLocal(bool won, string extra)
        {
            if (!InMatch) return;
            _phase = Phase.Idle;
            _fighting = false;
            if (_sock != null) { _sock.Close(); _sock = null; }
            if (local != null && !local.IsDead) { if (won) local.SetVictory(); else local.SetFrozen(true); }
            StartCoroutine(FinishRoutine(won, extra));
        }

        IEnumerator FinishRoutine(bool won, string extra)
        {
            StopCoroutine(nameof(TwoMessages));
            if (ui != null) ui.CenterMessage(won ? "VICTORY" : "DEFEAT", 2f);
            yield return new WaitForSecondsRealtime(2.2f);
            if (ui != null) ui.ShowHud(false);
            var r = new MatchResult
            {
                playerWon = won, playerRounds = _myScore, enemyRounds = _oppScore,
                playerHealthLeft01 = local != null ? local.Health01 : 0f
            };
            _onFinished?.Invoke(r, extra);
        }

        void ShowHitOnRemote(float amount, bool blocked)
        {
            Vector3 point = remote.transform.position + Vector3.up * 1.3f * remote.stats.scale;
            ShowHitFx(point, amount, false, blocked);
        }

        void ShowHitFx(Vector3 point, float amount, bool victimIsLocal, bool blocked)
        {
            if (fx != null && amount > 0f) fx.Spawn(point, blocked, amount);
            if (cam != null) cam.Shake(blocked ? 0.25f : Mathf.Clamp(amount / 90f, 0.3f, 1.2f));
            if (ui != null && amount >= 1f) ui.DamageNumber(point, amount, victimIsLocal, blocked);
        }

        void OnDestroy() { if (_sock != null) _sock.Close(); }
    }
}
