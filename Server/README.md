# ArvinCombat – online server for Arvin Games (game3, own Linux server)

Follows **Arvin Games – Doc 3, Multiplayer Guide**, hosting option *own server* (Linux, `mm3.arvinplay.com`).

```
Browser (iframe https://playsite.arvinpro.com)
 ├─ window.GamePlatform  → name, playerId, pause/resume, gameplayStart/Stop, reportError
 └─ wss://mm3.arvinplay.com/match       → matchmaker  (/arvinplay/games/game3/matchmaker, .NET 8)
    wss://mm3.arvinplay.com/room/{port} → room server (/arvinplay/games/game3/server, Unity Linux dedicated server)
```

## 1. Build (Windows PC)
| Part | How | Output |
|---|---|---|
| Web client (Studio ZIP) | Unity: **ArvinCombat → Build for Web (WebGL)** | `Builds\ArvinCombat-WebGL.zip` |
| Room server | Unity: **ArvinCombat → Build Room Server (Linux Dedicated Server)** | `Builds\RoomServer\` |
| Matchmaker | run `Server\MatchService\publish-match-service.bat` | `Builds\MatchService\` |

The server address is **not in the code**: it is in `StreamingAssets/arvincombat.json` inside the web build
(`Builds/WebGL/StreamingAssets/arvincombat.json`), so it can be changed without rebuilding:
```json
{ "serverUrl": "https://mm3.arvinplay.com", "matchPath": "match", "roomPath": "room" }
```

## 2. Install on the Linux server
```bash
# ASP.NET Core 8 runtime (Ubuntu example)
sudo apt-get install -y aspnetcore-runtime-8.0          # check: dotnet --list-runtimes

# copy Builds/MatchService/* -> /arvinplay/games/game3/matchmaker/
# copy Builds/RoomServer/*   -> /arvinplay/games/game3/server/
sudo mkdir -p /arvinplay/games/game3/logs
sudo chmod +x /arvinplay/games/game3/server/ArvinCombatRoom.x86_64

# service (edit the RoomSecret line first: a long random string, e.g. `openssl rand -hex 24`)
sudo cp linux/arvincombat-matchmaker.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now arvincombat-matchmaker
journalctl -u arvincombat-matchmaker -f

# web proxy (TLS on 443 by nginx/certbot or Cloudflare): see linux/nginx-arvincombat.conf
sudo nginx -t && sudo systemctl reload nginx
```
Check: `https://mm3.arvinplay.com/status` → `"configured": true`.

If `dotnet` is not in `/usr/bin`, fix the `ExecStart` line (`which dotnet`). If the server runs the service as
another user, give it write access to the logs folder and execute access to the room server.

## 3. appsettings.json (section `ArvinCombat`)
| Key | Meaning | Value for game3 |
|---|---|---|
| `RoomServerPath` | room server executable | `/arvinplay/games/game3/server/ArvinCombatRoom.x86_64` |
| `LogFolder` | matchmaker log + one `room-<port>.log` per room (older than `LogKeepDays` deleted) | `/arvinplay/games/game3/logs` |
| `InternalAddress` | address rooms listen on (plain `ws://`, no TLS) | `127.0.0.1` |
| `InternalPort` | matchmaker's local port, rooms send heartbeats to `http://127.0.0.1:<InternalPort>` | `5080` (same as `Urls`) |
| `RoomPortRange` | continuous room port range | `7800-7899` |
| `MaxRooms` | max rooms at the same time; beyond it players get "All arenas are busy" | `40` |
| `AllowedOrigin` | CORS + WebSocket Origin check (comma separated) | `https://playsite.arvinpro.com` |
| `PublicAddress` | public host name (logs / info only, clients connect by domain) | `mm3.arvinplay.com` |
| `RoomPath` | rooms path → `wss://<domain>/room/{port}` | `room` |
| `RoomSecret` | shared secret matchmaker ↔ rooms, **required, no default** (set it in the service file) | — |
| `MaxConnectionsPerIp` | parallel matchmaker connections per IP | `4` |
| `MaxRoomMinutes` | safety kill for a room running too long | `20` |
| `LogKeepDays` | days to keep room logs | `7` |
| `Urls` (top level) | matchmaker listen address | `http://127.0.0.1:5080` |

## 4. Behaviour (guide checklist)
- Full `https://` / `wss://` addresses from the config file; no cookies (ticket + playerId travel in the messages).
- Player identity from `GamePlatform.getPlayer()`: logged-in players use their site name (`playerId` is sent for
  display/stats only, it is not trusted). **Guests** get a temporary name `Guest 1234`.
- `gameLoadingFinished()` at the main menu, `gameplayStart()` / `gameplayStop()` around every fight, `trackEvent('MatchFinished')`,
  `reportError()` for: match connection failed / lost, room connection failed / lost.
- **Pause / hidden tab:** the page sends `away` from JavaScript; the opponent sees *AWAY*. On resume the game asks the room
  for a full `resync`. A player silent for **30 s** counts as absent, then has **15 s** to reconnect, otherwise the opponent wins.
- **Reconnect:** one automatic retry with the same ticket into the same room; the room sends the full state back.
- Ping `{"t":"ping"}` every **20 s** on both sockets (servers also send WebSocket pings).
- No `permessage-deflate`: neither server negotiates compression (nginx strips the header too).
- Rooms: start with arguments built only from config; report a heartbeat every 5 s with the secret; the matchmaker kills a room
  that never reports (40 s), stops reporting (20 s) or runs longer than `MaxRoomMinutes`. Empty / abandoned rooms exit by themselves
  (nobody joins within 45 s, or both players gone).
- Limits: name 16 chars, message ≤ 4 KB / 10 per s (matchmaker), ≤ 16 KB / 80 per s (room), one queue request per 2 s,
  `MaxConnectionsPerIp`, hits checked by the room (distance, damage cap, hits per second).
- Version check: client sends protocol version 2; an old page gets *"Game version is out of date. Please refresh the page."*
- Matchmaker restart closes running matches: players see a clear message and return to the menu.

## 5. Spec sheet (برگه مشخصات)
| مورد | مقدار |
|---|---|
| نام بازی و نامک (slug) | Arvin Combat — `game3` |
| موتور و نسخه | Unity 6000.0.39f1 |
| کتابخانه شبکه و Transport | Own WebSocket (text/JSON), no Unity Transport |
| محل میزبانی سرور | Own server (Linux) |
| آدرس عمومی سرور بازی | `https://mm3.arvinplay.com` |
| مسیر اتصال اتاق | `/room/{port}` (matchmaker: `/match`) |
| بازه پورت داخلی اتاق‌ها | 7800–7899 |
| حداکثر بازیکن در هر اتاق | 2 |
| RAM و CPU هر اتاق | ~150 MB RAM, ~3% of one CPU core (headless, 30 ticks/s) — to be confirmed on the server |
| `MaxRooms` پیشنهادی | 40 (≈ 6 GB RAM) |
| آدرس وضعیت | `https://mm3.arvinplay.com/status` |
| رفتار با مهمان | Temporary name (`Guest 1234`), can play online |
| مهلت «غایب» پیش از حذف بازیکن | 30 s silent + 15 s to reconnect |
| امکان برگشت به همان اتاق | Yes (one automatic reconnect with the room ticket) |
| دستگاه هدف | Desktop only (keyboard + mouse) |
| نیاز به هویت تأییدشده از سایت | No (upgrades/XP are saved in the browser; online fairness relies on room checks) |

`_old_node/` = earlier all-in-one Node.js server, not used.
