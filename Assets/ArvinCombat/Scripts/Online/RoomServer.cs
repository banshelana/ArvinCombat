using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace ArvinCombat
{
    /// <summary>
    /// Room server: runs inside the Linux dedicated server build, one process per match.
    /// Started by the match service with:
    ///   -batchmode -nographics -room -port 7801 -bind 127.0.0.1 -tickets TICKET_A,TICKET_B -publicAddress mm3.arvinplay.com
    /// Players connect to wss://mm3.arvinplay.com/room/7801 (the host proxies it to bind:port),
    /// send hello with their ticket, then the room runs best-of-3 rounds and relays the fight.
    /// The process exits when the match ends (or nobody shows up).
    /// </summary>
    public class RoomServer : MonoBehaviour
    {
        const int RoundsToWin = 2;
        const float RoundTime = 75f;
        const float Countdown = 2.5f;
        const float BetweenRounds = 3.5f;
        const float MaxHitDistance = 5.5f;
        const float MaxHitDamage = 900f;
        const int MaxHitsPerSecond = 8;
        const float JoinTimeout = 45f;     // both players must arrive within this time
        const float SilenceTimeout = 30f;  // paused / silent this long = absent (site pause, hidden tab)
        const float RejoinGrace = 15f;     // a dropped player may reconnect with the same ticket
        const int MaxMessagesPerSecond = 80;
        const int MaxFrameBytes = 16 * 1024;
        const float HeartbeatEvery = 5f;   // report to the match service

        // ------------------------------------------------------------ boot

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (!HasArg("-room")) return;
            // server mode: turn the game, UI and cameras off
            var gm = FindFirstObjectByType<GameManager>();
            if (gm != null) gm.gameObject.SetActive(false);
            foreach (var c in FindObjectsByType<Camera>(FindObjectsSortMode.None)) c.enabled = false;
            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None)) l.enabled = false;
            Application.targetFrameRate = 30;
            QualitySettings.vSyncCount = 0;
            new GameObject("RoomServer").AddComponent<RoomServer>();
        }

        static bool HasArg(string name)
        {
            foreach (var a in Environment.GetCommandLineArgs()) if (a == name) return true;
            return false;
        }

        static string Arg(string name, string def)
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
            return def;
        }

        // ------------------------------------------------------------ connections

        class Conn
        {
            public TcpClient tcp;
            public NetworkStream stream;
            public readonly object writeLock = new object();
            public volatile bool closed;
            public int side = -1;
            public float lastSeen;
            public float x, z, hp = 1f, mhp = 1f;
            public readonly List<float> hitTimes = new List<float>();
            public string name = "";
            public bool away;
            public float rateWindow;
            public int rateCount;
        }

        readonly ConcurrentQueue<Action> _main = new ConcurrentQueue<Action>();
        readonly List<Conn> _conns = new List<Conn>();
        readonly Conn[] _players = new Conn[2];
        TcpListener _listener;
        string[] _tickets = new string[2];
        int _port;

        // room state
        enum Phase { Waiting, Starting, Countdown, Fight, Between, Done }
        Phase _phase = Phase.Waiting;
        readonly int[] _scores = new int[2];
        int _round;
        float _phaseUntil;
        float _startedAt;
        float _quitAt = -1f;
        float _nextPing;
        float _nextHeartbeat;
        readonly float[] _droppedAt = { -1f, -1f };
        int _matchPort;
        string _secret = "";

        void Start()
        {
            _port = int.Parse(Arg("-port", "7800"));
            string bind = Arg("-bind", "0.0.0.0");
            var t = Arg("-tickets", "").Split(',');
            _tickets[0] = t.Length > 0 ? t[0] : "";
            _tickets[1] = t.Length > 1 ? t[1] : "";
            _startedAt = Time.realtimeSinceStartup;
            int.TryParse(Arg("-matchPort", "0"), out _matchPort);
            _secret = Environment.GetEnvironmentVariable("ARVIN_ROOM_SECRET") ?? "";

            if (!IPAddress.TryParse(bind, out var ip)) ip = IPAddress.Any;
            _listener = new TcpListener(ip, _port);
            _listener.Start();
            Log($"room listening on {ip}:{_port} (public {Arg("-publicAddress", "?")})");
            var th = new Thread(AcceptLoop) { IsBackground = true };
            th.Start();
        }

        void Log(string s) => Debug.Log("[Room " + _port + "] " + s);

        void AcceptLoop()
        {
            while (true)
            {
                TcpClient tcp;
                try { tcp = _listener.AcceptTcpClient(); }
                catch { return; }
                var th = new Thread(() => HandleConn(tcp)) { IsBackground = true };
                th.Start();
            }
        }

        static int ReadExact(NetworkStream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) return got;
                got += n;
            }
            return got;
        }

        void HandleConn(TcpClient tcp)
        {
            var c = new Conn { tcp = tcp };
            try
            {
                tcp.NoDelay = true;
                c.stream = tcp.GetStream();

                // --- HTTP upgrade handshake
                var header = new StringBuilder();
                var one = new byte[1];
                while (header.Length < 8192)
                {
                    if (c.stream.Read(one, 0, 1) <= 0) { tcp.Close(); return; }
                    header.Append((char)one[0]);
                    if (header.Length >= 4 && header.ToString(header.Length - 4, 4) == "\r\n\r\n") break;
                }
                string key = null;
                foreach (var line in header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None))
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0 && line.Substring(0, colon).Trim().Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                        key = line.Substring(colon + 1).Trim();
                }
                if (key == null)
                {
                    // plain HTTP (health check)
                    var ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 7\r\nConnection: close\r\n\r\nroom ok");
                    c.stream.Write(ok, 0, ok.Length);
                    tcp.Close();
                    return;
                }
                string accept;
                using (var sha = SHA1.Create())
                    accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                var resp = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
                c.stream.Write(resp, 0, resp.Length);
                _main.Enqueue(() => { c.lastSeen = Time.realtimeSinceStartup; _conns.Add(c); });

                // --- frames
                var hdr = new byte[14];
                var message = new List<byte>();
                while (!c.closed)
                {
                    if (ReadExact(c.stream, hdr, 2) < 2) break;
                    bool fin = (hdr[0] & 0x80) != 0;
                    int opcode = hdr[0] & 0x0F;
                    bool masked = (hdr[1] & 0x80) != 0;
                    long len = hdr[1] & 0x7F;
                    if (len == 126)
                    {
                        if (ReadExact(c.stream, hdr, 2) < 2) break;
                        len = (hdr[0] << 8) | hdr[1];
                    }
                    else if (len == 127)
                    {
                        if (ReadExact(c.stream, hdr, 8) < 8) break;
                        len = 0;
                        for (int i = 0; i < 8; i++) len = (len << 8) | hdr[i];
                    }
                    if (len > MaxFrameBytes) break;
                    var mask = new byte[4];
                    if (masked && ReadExact(c.stream, mask, 4) < 4) break;
                    var payload = new byte[len];
                    if (len > 0 && ReadExact(c.stream, payload, (int)len) < len) break;
                    if (masked) for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];

                    if (opcode == 0x8) break;                                         // close
                    if (opcode == 0x9) { SendFrame(c, 0xA, payload); continue; }       // ping -> pong
                    if (opcode == 0xA) { continue; }                                   // pong
                    if (opcode == 0x1 || opcode == 0x0)
                    {
                        message.AddRange(payload);
                        if (message.Count > MaxFrameBytes) break;
                        if (fin)
                        {
                            string text = Encoding.UTF8.GetString(message.ToArray());
                            message.Clear();
                            _main.Enqueue(() => OnMessage(c, text));
                        }
                    }
                }
            }
            catch (Exception) { }
            Close(c);
        }

        void SendFrame(Conn c, int opcode, byte[] payload)
        {
            if (c.closed || c.stream == null) return;
            int len = payload.Length;
            byte[] header;
            if (len < 126) header = new byte[] { (byte)(0x80 | opcode), (byte)len };
            else if (len < 65536) header = new byte[] { (byte)(0x80 | opcode), 126, (byte)(len >> 8), (byte)len };
            else
            {
                header = new byte[10];
                header[0] = (byte)(0x80 | opcode); header[1] = 127;
                for (int i = 0; i < 8; i++) header[9 - i] = (byte)((long)len >> (8 * i));
            }
            try
            {
                lock (c.writeLock)
                {
                    c.stream.Write(header, 0, header.Length);
                    if (len > 0) c.stream.Write(payload, 0, len);
                }
            }
            catch { Close(c); }
        }

        void SendText(Conn c, string text) => SendFrame(c, 0x1, Encoding.UTF8.GetBytes(text));
        void Send(Conn c, NetMsg m) { if (c != null) SendText(c, JsonUtility.ToJson(m)); }
        void Broadcast(NetMsg m) { var s = JsonUtility.ToJson(m); foreach (var p in _players) if (p != null && !p.closed) SendText(p, s); }

        void Close(Conn c)
        {
            if (c.closed) return;
            c.closed = true;
            try { lock (c.writeLock) { c.stream?.Write(new byte[] { 0x88, 0 }, 0, 2); } } catch { }
            try { c.tcp.Close(); } catch { }
            _main.Enqueue(() => OnClosed(c));
        }

        // ------------------------------------------------------------ room logic (main thread)

        void Update()
        {
            while (_main.TryDequeue(out var a))
            {
                try { a(); } catch (Exception e) { Debug.LogException(e); }
            }

            float now = Time.realtimeSinceStartup;

            if (_quitAt > 0f && now >= _quitAt) { Log("quit"); Application.Quit(); _quitAt = -1f; return; }

            // keep connections alive through proxies (the client also pings every 20s)
            if (now >= _nextPing)
            {
                _nextPing = now + 20f;
                foreach (var c in _conns.ToArray()) SendFrame(c, 0x9, new byte[0]);
            }
            foreach (var c in _conns.ToArray())
                if (!c.closed && now - c.lastSeen > SilenceTimeout) { Log($"player {c.side} silent for {SilenceTimeout}s"); Close(c); }

            // a dropped player did not come back in time
            if (_phase != Phase.Waiting && _phase != Phase.Done)
                for (int i = 0; i < 2; i++)
                    if (_players[i] == null && _droppedAt[i] > 0f && now - _droppedAt[i] > RejoinGrace) { Finish(1 - i, "left"); break; }

            if (_matchPort > 0 && now >= _nextHeartbeat)
            {
                _nextHeartbeat = now + HeartbeatEvery;
                StartCoroutine(Heartbeat());
            }

            switch (_phase)
            {
                case Phase.Waiting:
                    if (now - _startedAt > JoinTimeout)
                    {
                        // opponent never arrived: whoever is here wins, then shut down
                        int here = _players[0] != null ? 0 : _players[1] != null ? 1 : -1;
                        Finish(here, "left");
                    }
                    break;
                case Phase.Starting:
                    if (now >= _phaseUntil) StartRound();
                    break;
                case Phase.Countdown:
                    if (now >= _phaseUntil)
                    {
                        _phase = Phase.Fight;
                        _phaseUntil = now + RoundTime;
                        Broadcast(new NetMsg { t = "fight", roundTime = RoundTime });
                    }
                    break;
                case Phase.Fight:
                    if (now >= _phaseUntil) TimeUp();
                    break;
                case Phase.Between:
                    if (now >= _phaseUntil)
                    {
                        bool over = _scores[0] >= RoundsToWin || _scores[1] >= RoundsToWin || _round >= 7;
                        if (over) Finish(_scores[0] == _scores[1] ? -1 : (_scores[0] > _scores[1] ? 0 : 1), "normal");
                        else StartRound();
                    }
                    break;
            }
        }

        System.Collections.IEnumerator Heartbeat()
        {
            int players = (_players[0] != null ? 1 : 0) + (_players[1] != null ? 1 : 0);
            string body = "{\"port\":" + _port + ",\"players\":" + players + ",\"phase\":\"" + _phase + "\"}";
            using (var req = new UnityWebRequest("http://127.0.0.1:" + _matchPort + "/internal/heartbeat", "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("X-Room-Secret", _secret);
                req.timeout = 4;
                yield return req.SendWebRequest();
            }
        }

        int PhaseCode() => _phase == Phase.Countdown ? 1 : _phase == Phase.Fight ? 2 : _phase == Phase.Between || _phase == Phase.Starting ? 3 : 0;

        void SendSync(Conn c)
        {
            float left = _phase == Phase.Fight ? Mathf.Max(0f, _phaseUntil - Time.realtimeSinceStartup) : RoundTime;
            Send(c, new NetMsg { t = "sync", n = _round, sa = _scores[0], sb = _scores[1], s = PhaseCode(), roundTime = left });
        }

        void Notify(Conn about, string text)
        {
            if (about == null || about.side < 0) return;
            var other = _players[1 - about.side];
            if (other != null) Send(other, new NetMsg { t = "pop", txt = text });
        }

        void OnMessage(Conn c, string text)
        {
            float tnow = Time.realtimeSinceStartup;
            c.lastSeen = tnow;
            // flood protection
            if (tnow - c.rateWindow > 1f) { c.rateWindow = tnow; c.rateCount = 0; }
            if (++c.rateCount > MaxMessagesPerSecond) { Log($"player {c.side} flooding, closed"); Close(c); return; }
            NetMsg m;
            try { m = JsonUtility.FromJson<NetMsg>(text); } catch { return; }
            if (m == null || string.IsNullOrEmpty(m.t)) return;

            switch (m.t)
            {
                case "ping":
                    Send(c, new NetMsg { t = "pong" });
                    return;
                case "hello":
                    OnHello(c, m);
                    return;
                case "away":
                    if (!c.away) { c.away = true; Notify(c, "AWAY"); }
                    return;
            }
            if (c.away) { c.away = false; Notify(c, "BACK"); }
            if (m.t == "resync") { if (c.side >= 0) SendSync(c); return; }
            if (c.side < 0 || _phase == Phase.Done) return;
            var other = _players[1 - c.side];

            switch (m.t)
            {
                case "st":
                    c.x = m.x; c.z = m.z;
                    if (m.mhp > 0f) { c.hp = m.hp; c.mhp = m.mhp; }
                    if (other != null) SendText(other, text);
                    break;
                case "hit":
                    if (other != null && ValidHit(c, other, m)) SendText(other, text);
                    break;
                case "res":
                case "pop":
                case "cmd":
                    if (other != null) SendText(other, text);
                    break;
                case "dead":
                    if (_phase == Phase.Fight) EndRound(1 - c.side, "ko");
                    break;
                case "leave":
                    Finish(1 - c.side, "left");
                    break;
            }
        }

        void OnHello(Conn c, NetMsg m)
        {
            int side = -1;
            if (!string.IsNullOrEmpty(m.ticket))
                for (int i = 0; i < 2; i++) if (m.ticket == _tickets[i]) side = i;
            if (side < 0 || _phase == Phase.Done) { Send(c, new NetMsg { t = "err", txt = "This match is no longer available." }); Close(c); return; }

            bool rejoin = _players[side] != null || _droppedAt[side] > 0f;
            if (_players[side] != null && _players[side] != c) { var old = _players[side]; _players[side] = null; old.side = -1; Close(old); } // reconnect replaces the old socket
            c.side = side;
            string nm = m.name ?? "";
            c.name = nm.Length > 16 ? nm.Substring(0, 16) : nm;
            _players[side] = c;
            _droppedAt[side] = -1f;
            Log($"player {side} {(rejoin ? "rejoined" : "joined")}: {c.name}");
            Send(c, new NetMsg { t = "joined", side = side, sa = _scores[0], sb = _scores[1] });
            if (rejoin && _phase != Phase.Waiting) { SendSync(c); Notify(c, "RECONNECTED"); }

            if (_phase == Phase.Waiting && _players[0] != null && _players[1] != null)
            {
                _phase = Phase.Starting;
                _phaseUntil = Time.realtimeSinceStartup + 1.0f;
            }
        }

        void OnClosed(Conn c)
        {
            _conns.Remove(c);
            if (c.side >= 0 && _players[c.side] == c)
            {
                _players[c.side] = null;
                Log($"player {c.side} disconnected (may rejoin for {RejoinGrace}s)");
                if (_phase != Phase.Waiting && _phase != Phase.Done)
                {
                    _droppedAt[c.side] = Time.realtimeSinceStartup;
                    Notify(c, "DISCONNECTED");
                }
            }
        }

        bool ValidHit(Conn from, Conn to, NetMsg m)
        {
            if (_phase != Phase.Fight) return false;
            float dx = from.x - to.x, dz = from.z - to.z;
            if (Mathf.Sqrt(dx * dx + dz * dz) > MaxHitDistance) return false;
            float now = Time.realtimeSinceStartup;
            from.hitTimes.RemoveAll(t => now - t > 1f);
            if (from.hitTimes.Count >= MaxHitsPerSecond) return false;
            from.hitTimes.Add(now);
            return m.dmg >= 0f && m.dmg <= MaxHitDamage && !float.IsNaN(m.dmg);
        }

        void StartRound()
        {
            _round++;
            _phase = Phase.Countdown;
            _phaseUntil = Time.realtimeSinceStartup + Countdown;
            foreach (var p in _players) if (p != null) p.hp = p.mhp;
            Broadcast(new NetMsg { t = "round", n = _round, sa = _scores[0], sb = _scores[1] });
        }

        void TimeUp()
        {
            float f0 = _players[0] != null && _players[0].mhp > 0 ? _players[0].hp / _players[0].mhp : 0f;
            float f1 = _players[1] != null && _players[1].mhp > 0 ? _players[1].hp / _players[1].mhp : 0f;
            EndRound(Mathf.Approximately(f0, f1) ? -1 : (f0 > f1 ? 0 : 1), "time");
        }

        void EndRound(int winner, string reason)
        {
            _phase = Phase.Between;
            _phaseUntil = Time.realtimeSinceStartup + BetweenRounds;
            if (winner >= 0) _scores[winner]++;
            Broadcast(new NetMsg { t = "rend", w = winner, sa = _scores[0], sb = _scores[1], reason = reason });
        }

        void Finish(int winner, string reason)
        {
            if (_phase == Phase.Done) return;
            _phase = Phase.Done;
            Broadcast(new NetMsg { t = "mend", w = winner, sa = _scores[0], sb = _scores[1], reason = reason });
            Log($"match end {_scores[0]}-{_scores[1]} winner {winner} ({reason})");
            _quitAt = Time.realtimeSinceStartup + 3f;
        }

        void OnApplicationQuit()
        {
            try { _listener?.Stop(); } catch { }
            foreach (var c in _conns.ToArray()) { try { c.tcp.Close(); } catch { } }
        }
    }
}
