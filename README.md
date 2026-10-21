# Arvin Combat

3D 1v1 arena fighting game (Unity 6000.0.39f1, Built-in render pipeline, WebGL-friendly).

## First run
1. Unity Hub → **Add project from disk** → `E:\ArvinCombat` → open with **6000.0.39f1**.
2. On first open the project sets itself up automatically (menu **ArvinCombat → Setup Project** does the same thing any time):
   - builds an Animator Controller + prefab for each warrior from the animation packs
   - creates `Assets/ArvinCombat/Scenes/Main.unity` (arena) and adds it to Build Settings
   - shrinks textures for WebGL, sets Brotli compression and code stripping
3. Open `Assets/ArvinCombat/Scenes/Main.unity` and press **Play**.

## Controls
| Action | Input |
|---|---|
| Move (runs automatically when closing in) | W A S D |
| Left / Right / Front / Back attack | Left click + A / D / W / S (no key = front) |
| Left / Right / Front / Back kick (or the warrior's own Q set) | Q + A / D / W / S |
| Block / Parry (press just before the hit) | Hold right click |
| Dodge roll (toward WASD direction, backwards if none) | Double right click |
| Special (when the blue bar is full) | Left + right click together |
| Jump (attack in the air = jump attack) | Space |
| Pause | Esc |

**Rhythm:** left/right moves are fast and light, front moves reach further, back moves are slow and break guard.
Q moves are slower and stronger. Press the next move in the second half of the current one to chain up to 4 moves.
The move list in the bottom-left corner of the fight screen shows your warrior's 8 moves and lights up the one you are doing.

| Warrior | Left click set | Q set |
|---|---|---|
| Samurai | Katana Cuts | Sword Arts |
| Karate Master | Punches | Kicks (high left/right, front kick, spinning kick) |
| Spearman | Spear Strikes | Spear Arts |
| Knight | Sword | Shield Arts |
| Boxer | Punches (hooks, jab, uppercut) | Power Punches |
| Wrestler | Strikes + Body Slam | MMA Kicks |

## Warriors
| Warrior | Passive | Special |
|---|---|---|
| Samurai | Bleed: hits hurt over 3s | Iai Dash: dash-cut, knockdown |
| Karate Master | Combo Master: +10% per chained hit | Flying Kick: leap, knockdown |
| Spearman | Long Reach: strong push back | Whirlwind: 360° knockdown |
| Knight | Parry Master: blocks 90%, wide parry | Rage Strike: +40% power, no flinch 8s |
| Boxer | Footwork: half-cost dodges | Uppercut: stun |
| Wrestler | Iron Body: heavy/special can't be interrupted | Takedown: unblockable grab |

Numbers (damage, reach, hit timing, speed) are in `Scripts/Data/WarriorDatabase.cs`.
Campaign enemies and their traits (Iron Skin, Berserk, Vampiric, Giant, Thorns...) are in `Scripts/Data/LevelDatabase.cs`.

## XP & upgrades
Winning gives **XP Power**. Spend it in *Upgrade Warriors* on Power, Speed, Health, Defense, Stamina (10 ranks each, per warrior).
Saved in PlayerPrefs (works in the browser). The sum of ranks is the warrior's **power rating**, used later by the online matchmaker.

## Code layout
```
Scripts/Data     warriors, levels, stats, saved profile
Scripts/Combat   Fighter (combat simulation), AIBrain, player input, camera, hit FX
Scripts/Game     GameManager (flow), MatchManager (rounds, timer, KO)
Scripts/UI       all menus + HUD built from code (no UI textures)
Editor           one-click setup (animators, prefabs, scene, WebGL settings)
```
Fighters never read the keyboard directly: a *brain* fills a `FighterInput` each frame
(`PlayerBrain`, `AIBrain`, and later a `NetworkBrain`), which is what makes the online mode straightforward.

## Web launch + online mode (Arvin Games, game3)
Built for the Arvin Games site (game runs in an iframe on `https://playsite.arvinpro.com`) with its own Linux server
`mm3.arvinplay.com`. Full delivery guide, settings and the filled spec sheet: **`Server/README.md`**.
- **Web client** - **ArvinCombat → Build for Web (WebGL)** → `Builds/ArvinCombat-WebGL.zip` (upload in Studio).
  Server address: `StreamingAssets/arvincombat.json` (change without rebuilding).
- **Room server** - **ArvinCombat → Build Room Server (Linux Dedicated Server)** → `/arvinplay/games/game3/server/`.
- **Matchmaker** - `Server/MatchService/publish-match-service.bat` → `/arvinplay/games/game3/matchmaker/`.
- Uses `window.GamePlatform` for the player name, pause/resume and gameplay events; works without it in the Editor.
Win online: +60 XP, loss: +15 XP. Leaving a match counts as a loss.
"# ArvinCombat" 
