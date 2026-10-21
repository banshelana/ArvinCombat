// ArvinCombat match service (ASP.NET Core, .NET 8) for Arvin Games.
//  GET  /status             -> JSON status (no login)
//  WS   /match              -> matchmaking: hello + queue, paired by power rating, then a room server
//                              (Unity Linux dedicated server) is started and both players get its port + ticket
//  POST /internal/heartbeat -> rooms report every 5 s (loopback only + shared secret)
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArvinCombat.MatchService;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration.GetSection("ArvinCombat").Get<MatchConfig>() ?? new MatchConfig();
builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton<FileLog>();
builder.Services.AddSingleton<RoomLauncher>();
builder.Services.AddSingleton<MatchHub>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MatchHub>());

var origins = cfg.Origins();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (origins.Length == 0) p.AllowAnyOrigin(); else p.WithOrigins(origins);
    p.AllowAnyHeader().AllowAnyMethod();   // no credentials: the game does not use cookies
}));

var app = builder.Build();
var log = app.Services.GetRequiredService<FileLog>();
app.UseCors();

// Origin check for WebSockets: only the game page (https://playsite.arvinpro.com). No compression (not negotiated).
var wsOptions = new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) };
foreach (var o in origins) wsOptions.AllowedOrigins.Add(o);
app.UseWebSockets(wsOptions);

app.MapGet("/status", (MatchHub hub) => Results.Json(hub.Status()));
app.MapGet("/", () => Results.Text("ArvinCombat match service is running."));

app.Map("/match", async (HttpContext ctx, MatchHub hub) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    string ip = MatchHub.ClientIp(ctx);
    if (!hub.TryAddIp(ip)) { ctx.Response.StatusCode = 429; return; }
    try
    {
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        await hub.HandleClient(ws, ip, ctx.RequestAborted);
    }
    finally { hub.RemoveIp(ip); }
});

app.MapPost("/internal/heartbeat", async (HttpContext ctx, RoomLauncher rooms) =>
{
    var remote = ctx.Connection.RemoteIpAddress;
    if (remote == null || !IPAddress.IsLoopback(remote)) return Results.StatusCode(403);
    if (string.IsNullOrEmpty(cfg.RoomSecret) || ctx.Request.Headers["X-Room-Secret"].ToString() != cfg.RoomSecret) return Results.StatusCode(403);
    JsonNode body;
    try { body = await JsonNode.ParseAsync(ctx.Request.Body); } catch { return Results.BadRequest(); }
    int port = body?["port"]?.GetValue<int>() ?? 0;
    int players = body?["players"]?.GetValue<int>() ?? 0;
    rooms.Heartbeat(port, players);
    return Results.Ok();
});

if (string.IsNullOrEmpty(cfg.RoomSecret)) log.Write("WARNING: ArvinCombat:RoomSecret is empty - no rooms will be started until it is set.");
log.Write($"match service started. room server: {cfg.RoomServerPath}  ports {cfg.RoomPortRange}  max rooms {cfg.MaxRooms}  origin {cfg.AllowedOrigin}");
app.Run();

namespace ArvinCombat.MatchService
{
    public class MatchConfig
    {
        public string RoomServerPath { get; set; } = "";
        public string LogFolder { get; set; } = "logs";
        public string InternalAddress { get; set; } = "127.0.0.1";
        public string RoomPortRange { get; set; } = "7800-7899";
        public string AllowedOrigin { get; set; } = "https://playsite.arvinpro.com";
        public string PublicAddress { get; set; } = "";
        public string RoomPath { get; set; } = "room";
        public int MaxRooms { get; set; } = 40;
        public string RoomSecret { get; set; } = "";       // required, no default
        public int InternalPort { get; set; } = 5080;      // this service's local port (rooms send heartbeats here)
        public int MaxConnectionsPerIp { get; set; } = 4;
        public int MaxRoomMinutes { get; set; } = 20;
        public int LogKeepDays { get; set; } = 7;

        public string[] Origins()
        {
            if (string.IsNullOrWhiteSpace(AllowedOrigin) || AllowedOrigin.Trim() == "*") return Array.Empty<string>();
            return AllowedOrigin.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        public (int min, int max) Ports()
        {
            var parts = RoomPortRange.Split('-', StringSplitOptions.TrimEntries);
            int min = int.Parse(parts[0]);
            int max = parts.Length > 1 ? int.Parse(parts[1]) : min;
            return (min, max);
        }
    }

    public class FileLog
    {
        readonly string _file;
        readonly object _lock = new object();
        public FileLog(MatchConfig cfg)
        {
            try { Directory.CreateDirectory(cfg.LogFolder); } catch { }
            _file = Path.Combine(cfg.LogFolder, "match-service.log");
        }
        public void Write(string line)
        {
            line = DateTime.UtcNow.ToString("u") + " " + line;
            Console.WriteLine(line);
            lock (_lock) { try { File.AppendAllText(_file, line + Environment.NewLine); } catch { } }
        }
    }

    public class RoomInfo
    {
        public Process Proc;
        public DateTime Started;
        public DateTime LastHeartbeat;
        public int Players;
    }

    /// <summary>Starts / tracks room server processes (one per match, one port each).</summary>
    public class RoomLauncher
    {
        readonly MatchConfig _cfg;
        readonly FileLog _log;
        readonly ConcurrentDictionary<int, RoomInfo> _rooms = new ConcurrentDictionary<int, RoomInfo>();
        DateTime _lastLogCleanup = DateTime.MinValue;

        public RoomLauncher(MatchConfig cfg, FileLog log) { _cfg = cfg; _log = log; }
        public int Count => _rooms.Count;
        public int PlayersInRooms => _rooms.Values.Sum(r => r.Players);

        /// <summary>Returns the port, -1 = failed, -2 = MaxRooms reached.</summary>
        public int Launch(string ticketA, string ticketB)
        {
            if (string.IsNullOrEmpty(_cfg.RoomSecret) || string.IsNullOrEmpty(_cfg.RoomServerPath)) { _log.Write("room not started: RoomSecret or RoomServerPath missing"); return -1; }
            if (_rooms.Count >= _cfg.MaxRooms) return -2;
            var (min, max) = _cfg.Ports();
            for (int port = min; port <= max; port++)
            {
                if (_rooms.ContainsKey(port) || !PortFree(port)) continue;
                var logFile = Path.Combine(Path.GetFullPath(_cfg.LogFolder), $"room-{port}.log");
                var psi = new ProcessStartInfo
                {
                    FileName = _cfg.RoomServerPath,
                    WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(_cfg.RoomServerPath)) ?? "/",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                // arguments are built only from configuration and generated values, never from player input
                foreach (var a in new[] { "-batchmode", "-nographics", "-logFile", logFile, "-room",
                                          "-port", port.ToString(), "-bind", _cfg.InternalAddress,
                                          "-tickets", ticketA + "," + ticketB, "-matchPort", _cfg.InternalPort.ToString(),
                                          "-publicAddress", _cfg.PublicAddress ?? "" })
                    psi.ArgumentList.Add(a);
                psi.Environment["ARVIN_ROOM_SECRET"] = _cfg.RoomSecret;

                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                int myPort = port;
                p.Exited += (_, __) => { _rooms.TryRemove(myPort, out _); _log.Write($"room {myPort} exited"); };
                var info = new RoomInfo { Proc = p, Started = DateTime.UtcNow, LastHeartbeat = DateTime.MinValue };
                if (!_rooms.TryAdd(port, info)) continue;
                try
                {
                    p.Start();
                    _log.Write($"room {port} started (pid {p.Id})");
                    return port;
                }
                catch (Exception e)
                {
                    _rooms.TryRemove(port, out _);
                    _log.Write($"room start failed: {e.Message}");
                    return -1;
                }
            }
            _log.Write("no free room port");
            return -2;
        }

        bool PortFree(int port)
        {
            try
            {
                var ip = IPAddress.TryParse(_cfg.InternalAddress, out var a) ? a : IPAddress.Loopback;
                var l = new System.Net.Sockets.TcpListener(ip, port);
                l.Start();
                l.Stop();
                return true;
            }
            catch { return false; }
        }

        public void Heartbeat(int port, int players)
        {
            if (_rooms.TryGetValue(port, out var r)) { r.LastHeartbeat = DateTime.UtcNow; r.Players = Math.Clamp(players, 0, 2); }
        }

        /// <summary>Closes rooms that never came up, stopped reporting, or run far too long. Deletes old logs.</summary>
        public void Cleanup()
        {
            var now = DateTime.UtcNow;
            foreach (var kv in _rooms)
            {
                var r = kv.Value;
                string why = null;
                try
                {
                    if (r.Proc == null || r.Proc.HasExited) { _rooms.TryRemove(kv.Key, out _); continue; }
                    if (r.LastHeartbeat == DateTime.MinValue && now - r.Started > TimeSpan.FromSeconds(40)) why = "never reported";
                    else if (r.LastHeartbeat != DateTime.MinValue && now - r.LastHeartbeat > TimeSpan.FromSeconds(20)) why = "stopped reporting";
                    else if (now - r.Started > TimeSpan.FromMinutes(_cfg.MaxRoomMinutes)) why = "too old";
                    if (why != null) { r.Proc.Kill(true); _rooms.TryRemove(kv.Key, out _); _log.Write($"room {kv.Key} killed ({why})"); }
                }
                catch { }
            }

            if (now - _lastLogCleanup > TimeSpan.FromHours(1))
            {
                _lastLogCleanup = now;
                try
                {
                    foreach (var f in Directory.GetFiles(_cfg.LogFolder, "room-*.log"))
                        if (now - File.GetLastWriteTimeUtc(f) > TimeSpan.FromDays(_cfg.LogKeepDays)) File.Delete(f);
                }
                catch { }
            }
        }
    }

    public class Player
    {
        public WebSocket Ws;
        public string Ip = "";
        public readonly SemaphoreSlim SendLock = new SemaphoreSlim(1, 1);
        public string PlayerId = "";
        public string Name = "Player";
        public int Warrior;
        public int[] Ranks = new int[5];
        public int Rating;
        public bool Hello;
        public DateTime Since;
        public DateTime LastQueue = DateTime.MinValue;
        public bool Matched;
        public DateTime RateWindow;
        public int RateCount;
    }

    public class MatchHub : BackgroundService
    {
        public const int ProtocolVersion = 2;
        const int RoundsToWin = 2;
        const float RoundTime = 75f;
        const int MaxMessageBytes = 4096;
        const int MaxMessagesPerSecond = 10;

        readonly MatchConfig _cfg;
        readonly FileLog _log;
        readonly RoomLauncher _rooms;
        readonly ConcurrentDictionary<Player, byte> _online = new ConcurrentDictionary<Player, byte>();
        readonly ConcurrentDictionary<string, int> _ipCount = new ConcurrentDictionary<string, int>();
        readonly List<Player> _queue = new List<Player>();
        readonly object _qLock = new object();

        public MatchHub(MatchConfig cfg, FileLog log, RoomLauncher rooms) { _cfg = cfg; _log = log; _rooms = rooms; }

        public static string ClientIp(HttpContext ctx)
        {
            // behind a proxy / Cloudflare the real address is in these headers
            string ip = ctx.Request.Headers["CF-Connecting-IP"].ToString();
            if (string.IsNullOrEmpty(ip)) ip = ctx.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim();
            if (string.IsNullOrEmpty(ip)) ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            return ip;
        }

        public bool TryAddIp(string ip)
        {
            int n = _ipCount.AddOrUpdate(ip, 1, (_, v) => v + 1);
            if (n > _cfg.MaxConnectionsPerIp) { RemoveIp(ip); return false; }
            return true;
        }

        public void RemoveIp(string ip)
        {
            _ipCount.AddOrUpdate(ip, 0, (_, v) => Math.Max(0, v - 1));
            if (_ipCount.TryGetValue(ip, out var v2) && v2 == 0) _ipCount.TryRemove(ip, out _);
        }

        public object Status()
        {
            int queued; lock (_qLock) queued = _queue.Count;
            return new
            {
                ok = true, version = ProtocolVersion,
                online = _online.Count, queued, rooms = _rooms.Count, maxRooms = _cfg.MaxRooms,
                playersInRooms = _rooms.PlayersInRooms, ports = _cfg.RoomPortRange, roomPath = _cfg.RoomPath,
                configured = !string.IsNullOrEmpty(_cfg.RoomSecret) && File.Exists(_cfg.RoomServerPath)
            };
        }

        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try { Matchmake(); _rooms.Cleanup(); } catch (Exception e) { _log.Write("matchmake error: " + e.Message); }
                await Task.Delay(1000, stop).ContinueWith(_ => { });
            }
        }

        static int Tolerance(double waitSeconds) => waitSeconds > 25 ? int.MaxValue : 3 + (int)(waitSeconds / 5) * 4;

        void Matchmake()
        {
            var pairs = new List<(Player a, Player b)>();
            lock (_qLock)
            {
                _queue.RemoveAll(p => p.Ws.State != WebSocketState.Open);
                var now = DateTime.UtcNow;
                bool found = true;
                while (found && _queue.Count >= 2)
                {
                    found = false;
                    _queue.Sort((x, y) => x.Since.CompareTo(y.Since));
                    for (int i = 0; i < _queue.Count && !found; i++)
                    {
                        var a = _queue[i];
                        int tolA = Tolerance((now - a.Since).TotalSeconds);
                        Player best = null; int bestDiff = int.MaxValue;
                        foreach (var b in _queue)
                        {
                            if (b == a) continue;
                            if (!string.IsNullOrEmpty(a.PlayerId) && a.PlayerId == b.PlayerId) continue; // not against yourself
                            int diff = Math.Abs(a.Rating - b.Rating);
                            int tol = Math.Max(tolA, Tolerance((now - b.Since).TotalSeconds));
                            if (diff <= tol && diff < bestDiff) { best = b; bestDiff = diff; }
                        }
                        if (best != null)
                        {
                            _queue.Remove(a); _queue.Remove(best);
                            pairs.Add((a, best));
                            found = true;
                        }
                    }
                }
            }
            foreach (var (a, b) in pairs) _ = StartMatch(a, b);
        }

        async Task StartMatch(Player a, Player b)
        {
            string ta = Guid.NewGuid().ToString("N");
            string tb = Guid.NewGuid().ToString("N");
            int port = _rooms.Launch(ta, tb);
            if (port < 0)
            {
                string msg = port == -2 ? "All arenas are busy right now. Please try again in a minute."
                                        : "The game server is not ready. Please try again later.";
                await Send(a, new { t = "err", txt = msg });
                await Send(b, new { t = "err", txt = msg });
                return;
            }
            a.Matched = b.Matched = true;
            _log.Write($"match {a.Name}({a.Rating}) vs {b.Name}({b.Rating}) -> room {port}");
            await Send(a, MatchMsg(0, b, port, ta));
            await Send(b, MatchMsg(1, a, port, tb));
        }

        object MatchMsg(int side, Player opp, int port, string ticket) => new
        {
            t = "match", side, oppName = opp.Name, oppWarrior = opp.Warrior, oppRanks = opp.Ranks, oppRating = opp.Rating,
            rounds = RoundsToWin, roundTime = RoundTime, port, ticket, roomPath = _cfg.RoomPath
        };

        async Task Send(Player p, object msg)
        {
            if (p.Ws.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg));
            await p.SendLock.WaitAsync();
            try { await p.Ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); }
            catch { }
            finally { p.SendLock.Release(); }
        }

        static string Clean(string n, int max, string fallback)
        {
            n = new string((n ?? "").Where(c => c >= ' ' && c != '<' && c != '>' && c != '"' && c != '\\').ToArray()).Trim();
            if (n.Length == 0) n = fallback;
            return n.Length > max ? n.Substring(0, max) : n;
        }

        public async Task HandleClient(WebSocket ws, string ip, CancellationToken ct)
        {
            var p = new Player { Ws = ws, Ip = ip, RateWindow = DateTime.UtcNow };
            _online[p] = 0;
            var buf = new byte[MaxMessageBytes];
            var ms = new MemoryStream();
            try
            {
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buf, 0, r.Count);
                    if (ms.Length > MaxMessageBytes) break;               // message too big
                    if (!r.EndOfMessage) continue;
                    var now = DateTime.UtcNow;
                    if ((now - p.RateWindow).TotalSeconds > 1) { p.RateWindow = now; p.RateCount = 0; }
                    if (++p.RateCount > MaxMessagesPerSecond) break;       // flooding
                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    ms.SetLength(0);
                    await OnMessage(p, text);
                }
            }
            catch { }
            finally
            {
                lock (_qLock) _queue.Remove(p);
                _online.TryRemove(p, out _);
                try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            }
        }

        async Task OnMessage(Player p, string text)
        {
            JsonNode m;
            try { m = JsonNode.Parse(text); } catch { return; }
            string t = null;
            try { t = m?["t"]?.GetValue<string>(); } catch { }
            switch (t)
            {
                case "ping":
                    await Send(p, new { t = "pong" });
                    break;
                case "hello":
                    int ver = 0;
                    try { ver = m["ver"]?.GetValue<int>() ?? 0; } catch { }
                    if (ver != ProtocolVersion) { await Send(p, new { t = "err", txt = "Game version is out of date. Please refresh the page." }); return; }
                    try
                    {
                        p.Name = Clean(m["name"]?.GetValue<string>(), 16, "Player");
                        p.PlayerId = Clean(m["playerId"]?.GetValue<string>(), 64, "");
                        p.Warrior = Math.Clamp(m["warrior"]?.GetValue<int>() ?? 0, 0, 5);
                        var ranks = new int[5];
                        if (m["ranks"] is JsonArray arr)
                            for (int i = 0; i < 5 && i < arr.Count; i++) ranks[i] = Math.Clamp(arr[i]?.GetValue<int>() ?? 0, 0, 10);
                        p.Ranks = ranks;
                        p.Rating = ranks.Sum();
                    }
                    catch { await Send(p, new { t = "err", txt = "Bad hello message." }); return; }
                    p.Hello = true;
                    await Send(p, new { t = "welcome", online = _online.Count });
                    break;
                case "queue":
                    if (!p.Hello || p.Matched) return;
                    if ((DateTime.UtcNow - p.LastQueue).TotalSeconds < 2) return; // one request per 2 s
                    p.LastQueue = DateTime.UtcNow;
                    lock (_qLock)
                    {
                        _queue.Remove(p);
                        p.Since = DateTime.UtcNow;
                        _queue.Add(p);
                    }
                    await Send(p, new { t = "queued", online = _online.Count });
                    break;
                case "cancel":
                    lock (_qLock) _queue.Remove(p);
                    break;
            }
        }
    }
}
