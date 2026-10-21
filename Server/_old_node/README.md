# ArvinCombat server (mm3.arvinplay.com, Linux)

One small Node.js program does everything:
- serves the web game (the Unity WebGL build in `public/`)
- online battles over WebSocket at `/ws`: matchmaking by power rating (upgrade level), rounds, timer, hit checks

No npm packages are needed. Node.js 18 or newer.

## 1. Build the web game (on your PC)
Unity menu **ArvinCombat → Build for Web (WebGL)**.
The build goes straight into `E:\ArvinCombat\Server\public`.

## 2. Upload (first time)
Copy the whole `E:\ArvinCombat\Server` folder to the server as `/opt/arvincombat`
(FileZilla / WinSCP, or from PowerShell):
```
scp -r E:\ArvinCombat\Server root@mm3.arvinplay.com:/opt/arvincombat
```

## 3. Install and start (on the server, once)
```bash
# Node.js (Ubuntu/Debian). Skip if `node -v` already shows v18+
curl -fsSL https://deb.nodesource.com/setup_20.x | sudo bash - && sudo apt-get install -y nodejs

sudo chown -R www-data:www-data /opt/arvincombat
sudo cp /opt/arvincombat/arvincombat.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now arvincombat
sudo ufw allow 8090/tcp          # if the firewall is on
```
Check it: open **http://mm3.arvinplay.com:8090/** → the game loads.
Status: http://mm3.arvinplay.com:8090/status  ·  Logs: `journalctl -u arvincombat -f`

## 4. Optional: normal address with https
With nginx + certbot (see `nginx-arvincombat.conf`) players open **https://mm3.arvinplay.com/**.
The game connects with `wss://` automatically when the page is https.

## Updating the game later
Build again in Unity, then upload only the `public` folder (replace the old one). No restart needed.
If you change `arvincombat-server.js`: `sudo systemctl restart arvincombat`.

## Testing online from the Unity Editor
The Editor connects to `ws://mm3.arvinplay.com:8090/ws` (field *Editor Server Url* on the `OnlineMatch`
component of the `Game` object). Run one copy in the Editor and one in the browser to fight yourself.
