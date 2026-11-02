using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ArvinCombat
{
    /// <summary>
    /// Shop, achievements and cloud save on top of the Arvin Games site API (GamePlatformBridge).
    ///
    /// Shop  (Doc 1 section 4, "method A"): what the player owns is read from getInventory only.
    ///       Buy = purchase -> deliver -> save -> acknowledge. Pending receipts are only acknowledged at start.
    ///       XP packs are consumed on the server right after delivery (leftovers are consumed at the next start).
    /// Achievements (Doc 2): unlock / progress calls are queued, spaced out (60 requests a minute limit)
    ///       and kept in the profile until the site confirms them, so nothing is lost when offline.
    /// Cloud save (Doc 1 section 5): load at start and keep the newer copy (savedAt), save at most every 20 s
    ///       and when the site pauses the game. Guests keep only the local save.
    /// Coins are never given by the game: they come only from the site (purchases / achievement rewards).
    /// </summary>
    public class ArvinServices : MonoBehaviour
    {
        public static ArvinServices Instance { get; private set; }

        GamePlatformBridge _bp;
        PlayerProfile _p;

        /// <summary>Something the UI shows changed (inventory, coins, achievements, cloud data).</summary>
        public event Action Changed;
        /// <summary>A newer save was loaded from the cloud and replaced the local profile.</summary>
        public event Action CloudApplied;
        /// <summary>An achievement was unlocked just now (title in English).</summary>
        public event Action<string> AchievementUnlocked;

        public static ArvinServices Create(GamePlatformBridge bp, PlayerProfile profile, GameObject host)
        {
            var s = host.GetComponent<ArvinServices>();
            if (s == null) s = host.AddComponent<ArvinServices>();
            s.Init(bp, profile);
            return s;
        }

        void Init(GamePlatformBridge bp, PlayerProfile profile)
        {
            Instance = this;
            _bp = bp;
            _p = profile;
            _bp.OnReady += OnReady;
            _bp.OnPaused += () => { if (_dirty) PushCloud(null); };
            _bp.OnCoinsChanged += _ => Changed?.Invoke();
            PlayerProfile.Saved += () => _dirty = true;
            if (_bp.Ready) OnReady();
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        bool _started;

        void OnReady()
        {
            if (_started) return;
            _started = true;
            // one step after the other: keeps the start under the request limit and in the right order
            LoadCloud(() => SyncStore(() => LoadAchievements(() => { _achReady = true; Recheck(); })));
        }

        void Update()
        {
            float now = Time.unscaledTime;
            if (_dirty && !_pushing && now >= _nextPush) PushCloud(null);
            if (!_cloudLoaded && _cloudRetryAt > 0f && now >= _cloudRetryAt) { _cloudRetryAt = -1f; LoadCloud(null); }
            PumpAchievements(now);
        }

        static string SafeKey(string prefix, string raw)
        {
            var sb = new StringBuilder(prefix);
            foreach (char c in raw ?? "")
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_') sb.Append(c);
            if (sb.Length < 8) sb.Append(Guid.NewGuid().ToString("N"));
            return sb.Length > 64 ? sb.ToString(0, 64) : sb.ToString();
        }

        public static string ErrorText(string code)
        {
            switch (code)
            {
                case "SIGN_IN_REQUIRED": return "Sign in on the site to buy items.";
                case "INSUFFICIENT_FUNDS": return "Not enough Arvin Coins.";
                case "CANCELLED": case "USER_CANCELLED": return "Purchase cancelled.";
                case "ALREADY_OWNED": return "You already own this.";
                case "PURCHASE_LIMIT": return "You reached the limit for this item.";
                case "RATE_LIMITED": return "Too many requests - wait a few seconds.";
                case "SESSION_NOT_FOUND": return "Session expired - please refresh the page.";
                case "NOT_AVAILABLE": return "The shop works on the Arvin Games site.";
                case "BUSY": return "Please wait...";
                default: return string.IsNullOrEmpty(code) ? "Something went wrong." : "Could not complete: " + code;
            }
        }

        // ================================================================ cloud save

        int _saveVersion;
        bool _cloudLoaded;
        bool _dirty;
        bool _pushing;
        float _nextPush;
        float _cloudRetryAt = -1f;
        readonly List<Action<bool>> _afterPush = new List<Action<bool>>();

        /// <summary>Logged-in player (or the Editor simulator): progress is kept on the site too.</summary>
        public bool CloudEnabled => _bp != null && _bp.Ready && !_bp.IsGuest;
        public bool CloudSynced => _cloudLoaded;

        void LoadCloud(Action done)
        {
            if (!CloudEnabled) { done?.Invoke(); return; }
            _bp.LoadData(sd =>
            {
                _cloudLoaded = true;
                if (sd != null && !string.IsNullOrEmpty(sd.data))
                {
                    _saveVersion = sd.version;
                    ApplyIfNewer(sd.data);
                }
                else
                {
                    // nothing on the site yet (new player, or a guest who just signed in): upload the local save
                    _saveVersion = 0;
                    _dirty = true;
                    _nextPush = 0f;
                }
                done?.Invoke();
            }, err =>
            {
                // offline / timeout: keep playing on the local save, try again later (never overwrite blindly)
                if (err != "SIGN_IN_REQUIRED") _cloudRetryAt = Time.unscaledTime + 30f;
                done?.Invoke();
            });
        }

        void ApplyIfNewer(string json)
        {
            PlayerProfile remote = null;
            try { remote = JsonUtility.FromJson<PlayerProfile>(json); } catch { }
            if (remote == null) return;
            if (remote.savedAt > _p.savedAt)
            {
                try
                {
                    JsonUtility.FromJsonOverwrite(json, _p);
                    _p.Repair();
                    _p.SaveLocalOnly();
                    CloudApplied?.Invoke();
                    Changed?.Invoke();
                }
                catch (Exception e) { Debug.LogWarning("ArvinCombat: cloud save not applied: " + e.Message); }
            }
            else if (remote.savedAt < _p.savedAt) { _dirty = true; _nextPush = 0f; }
        }

        /// <summary>Uploads the profile now. done(true) only when the site stored it.</summary>
        public void PushCloud(Action<bool> done, bool retryOnConflict = true)
        {
            if (!CloudEnabled || !_cloudLoaded) { done?.Invoke(false); return; }
            if (_pushing) { if (done != null) _afterPush.Add(done); _dirty = true; return; }
            _pushing = true;
            _dirty = false;
            _nextPush = Time.unscaledTime + 20f;
            _bp.SaveData(_p.ToJson(), info =>
            {
                _pushing = false;
                if (info != null) _saveVersion = info.version;
                done?.Invoke(true);
                FlushAfterPush(true);
            }, err =>
            {
                _pushing = false;
                if (err == "SAVE_CONFLICT" && retryOnConflict)
                {
                    // another tab / device saved: load it, keep the newer one, then try once more
                    _bp.LoadData(sd =>
                    {
                        _saveVersion = sd != null ? sd.version : 0;
                        if (sd != null && !string.IsNullOrEmpty(sd.data)) ApplyIfNewer(sd.data);
                        PushCloud(ok => { done?.Invoke(ok); FlushAfterPush(ok); }, false);
                    }, e2 => { _dirty = true; done?.Invoke(false); FlushAfterPush(false); });
                    return;
                }
                _dirty = true;
                if (err == "RATE_LIMITED") _nextPush = Time.unscaledTime + 30f;
                done?.Invoke(false);
                FlushAfterPush(false);
            }, _saveVersion);
        }

        void FlushAfterPush(bool ok)
        {
            if (_afterPush.Count == 0) return;
            var list = _afterPush.ToArray();
            _afterPush.Clear();
            if (_dirty) { PushCloud(null); }
            foreach (var a in list) a?.Invoke(ok);
        }

        // ================================================================ shop

        readonly Dictionary<string, int> _stock = new Dictionary<string, int>();
        readonly HashSet<string> _testOwned = new HashSet<string>();  // Studio preview purchases (not stored by the site)
        int _testContinues;
        bool _buying;

        public bool StoreReady { get; private set; }
        public bool Buying => _buying;
        public int Coins => _bp != null ? _bp.Coins : 0;

        public int Stock(string key) => key != null && _stock.TryGetValue(key, out var n) ? n : 0;
        public bool Owns(string key) => string.IsNullOrEmpty(key) || Stock(key) > 0 || _testOwned.Contains(key);

        public bool WarriorUnlocked(WarriorId w)
        {
            string item = GameContent.WarriorItem(w);
            if (item == null) return true;
            int lvl = GameContent.WarriorFreeAtLevel(w);
            if (lvl >= 0 && _p.LevelCleared(lvl)) return true;
            return Owns(item);
        }

        public bool SkinOwned(WarriorId w, string style) => string.IsNullOrEmpty(style) || Owns(GameContent.SkinItem(w, style));

        /// <summary>Skin to show for the warrior ("" if none or not owned).</summary>
        public string EquippedSkin(WarriorId w)
        {
            string s = _p.equippedSkins[(int)w];
            return !string.IsNullOrEmpty(s) && SkinOwned(w, s) ? s : "";
        }

        public void EquipSkin(WarriorId w, string style)
        {
            if (!SkinOwned(w, style)) return;
            _p.equippedSkins[(int)w] = style ?? "";
            _p.Save();
            Changed?.Invoke();
        }

        public int Continues => Stock(GameContent.ContinueItem) + _testContinues;

        void SyncStore(Action done)
        {
            _bp.GetInventory(items =>
            {
                _stock.Clear();
                foreach (var it in items) if (it != null && !string.IsNullOrEmpty(it.key)) _stock[it.key] = Mathf.Max(0, it.remaining);
                StoreReady = true;
                Changed?.Invoke();
                _bp.GetPendingPurchases(receipts =>
                {
                    // method A: getInventory already counts pending purchases -> deliver leftovers, save, then only acknowledge
                    ConsumeLeftoverXp(new List<string> { "xp_pack_small", "xp_pack_large" }, () =>
                    {
                        if (receipts == null || receipts.Length == 0) { done?.Invoke(); return; }
                        _p.Save();
                        PushCloud(ok =>
                        {
                            if (ok || !CloudEnabled)
                                foreach (var r in receipts)
                                    if (r != null && !string.IsNullOrEmpty(r.receiptId)) _bp.Acknowledge(r.receiptId);
                        });
                        done?.Invoke();
                    });
                });
            }, err =>
            {
                StoreReady = false;
                done?.Invoke();
            });
        }

        void ConsumeLeftoverXp(List<string> keys, Action done)
        {
            if (keys.Count == 0) { done?.Invoke(); return; }
            string key = keys[0];
            keys.RemoveAt(0);
            int n = Stock(key);
            if (n <= 0) { ConsumeLeftoverXp(keys, done); return; }
            string idem = SafeKey("xpl-", Guid.NewGuid().ToString("N"));
            ConsumeWithRetry(key, n, idem, res =>
            {
                if (res != null)
                {
                    _stock[key] = Mathf.Max(0, res.remaining);
                    _p.AddXp(GameContent.XpPackAmount(key) * n);
                    Changed?.Invoke();
                }
                ConsumeLeftoverXp(keys, done);
            });
        }

        /// <summary>consume with one retry using the SAME idempotency key (never takes twice).</summary>
        void ConsumeWithRetry(string key, int count, string idem, Action<ConsumeResult> done, bool retried = false)
        {
            _bp.Consume(key, count, idem, r => done(r ?? new ConsumeResult { key = key, remaining = Mathf.Max(0, Stock(key) - count) }), err =>
            {
                if (!retried && (err == "TIMEOUT" || err == "FAILED" || err == "RATE_LIMITED"))
                {
                    StartCoroutine(After(err == "RATE_LIMITED" ? 5f : 1.5f, () => ConsumeWithRetry(key, count, idem, done, true)));
                    return;
                }
                done(null);
            });
        }

        System.Collections.IEnumerator After(float seconds, Action a)
        {
            yield return new WaitForSecondsRealtime(seconds);
            a?.Invoke();
        }

        /// <summary>
        /// Buys a store item. The site shows its own confirm / sign-in / buy-coins window.
        /// done(true, null) when delivered; done(false, errorCode) otherwise.
        /// </summary>
        public void Buy(string key, Action<bool, string> done)
        {
            var item = GameContent.Get().FindItem(key);
            if (item == null) { done?.Invoke(false, "ITEM_NOT_FOUND"); return; }
            if (item.IsPermanent && Owns(key)) { done?.Invoke(true, null); return; }
            if (_buying) { done?.Invoke(false, "BUSY"); return; }
            _buying = true;
            Changed?.Invoke();

            _bp.Purchase(key, receipt =>
            {
                if (receipt == null) { _buying = false; Changed?.Invoke(); done?.Invoke(false, "FAILED"); return; }
                bool test = receipt.status == "Test"; // Studio preview: nothing stored on the site
                if (test) { if (item.IsPermanent) _testOwned.Add(key); }
                else _stock[key] = Stock(key) + 1;

                Deliver(item, receipt, test, () =>
                {
                    _p.Save();
                    // acknowledge only after the save is stored; otherwise the receipt stays pending
                    // and is acknowledged at the next start (inventory already has it)
                    if (!test)
                        PushCloud(ok => { if (ok || !CloudEnabled) _bp.Acknowledge(receipt.receiptId); });
                    _buying = false;
                    Changed?.Invoke();
                    _bp.TrackEvent("ItemBought", "{\"item\":\"" + key + "\"}");
                    done?.Invoke(true, null);
                });
            }, err =>
            {
                _buying = false;
                Changed?.Invoke();
                if (err == "ALREADY_OWNED") { _stock[key] = Mathf.Max(1, Stock(key)); done?.Invoke(true, null); return; }
                done?.Invoke(false, err);
            });
        }

        void Deliver(GameContent.StoreItem item, PurchaseReceipt receipt, bool test, Action done)
        {
            string key = item.key;
            int xp = GameContent.XpPackAmount(key);
            if (xp > 0)
            {
                if (test) { _p.AddXp(xp); done(); return; }
                ConsumeWithRetry(key, 1, SafeKey("xp-", receipt.receiptId), res =>
                {
                    if (res != null) { _stock[key] = Mathf.Max(0, res.remaining); _p.AddXp(xp); }
                    // if it failed, the pack stays in the inventory and is delivered at the next start
                    done();
                });
                return;
            }
            if (key == GameContent.ContinueItem) { if (test) _testContinues++; done(); return; }
            if (key.StartsWith("skin_"))
            {
                // equip the new skin right away
                for (int w = 0; w < GameContent.WarriorKeys.Length; w++)
                    foreach (var st in GameContent.SkinStyles)
                        if (GameContent.SkinItem((WarriorId)w, st) == key) _p.equippedSkins[w] = st;
            }
            done();
        }

        /// <summary>Uses one campaign continue (consumed on the site). done(true) when it may be used.</summary>
        public void UseContinue(Action<bool> done)
        {
            if (_testContinues > 0) { _testContinues--; Changed?.Invoke(); done?.Invoke(true); return; }
            if (Stock(GameContent.ContinueItem) <= 0) { done?.Invoke(false); return; }
            ConsumeWithRetry(GameContent.ContinueItem, 1, SafeKey("cont-", Guid.NewGuid().ToString("N")), res =>
            {
                if (res != null) _stock[GameContent.ContinueItem] = Mathf.Max(0, res.remaining);
                Changed?.Invoke();
                done?.Invoke(res != null);
            });
        }

        // ================================================================ achievements

        readonly HashSet<string> _unlocked = new HashSet<string>();
        readonly Dictionary<string, int> _progress = new Dictionary<string, int>();
        bool _achReady;
        bool _achBusy;
        float _nextAch;

        public bool IsUnlocked(string key) => _unlocked.Contains(key);
        public int ProgressOf(string key) => _progress.TryGetValue(key, out var v) ? v : 0;
        public int UnlockedCount => _unlocked.Count;

        void LoadAchievements(Action done)
        {
            _bp.GetAchievements(list =>
            {
                foreach (var a in list)
                {
                    if (a == null || string.IsNullOrEmpty(a.key)) continue;
                    if (a.unlocked) _unlocked.Add(a.key);
                    if (a.progress > ProgressOf(a.key)) _progress[a.key] = a.progress;
                }
                Changed?.Invoke();
                done?.Invoke();
            }, err => done?.Invoke());
        }

        /// <summary>Re-reads the site's achievement state (for the achievements screen).</summary>
        public void RefreshAchievements(Action done = null)
        {
            if (_bp == null || !_bp.Ready) { done?.Invoke(); return; }
            LoadAchievements(done);
        }

        public void Unlock(string key)
        {
            if (string.IsNullOrEmpty(key) || _unlocked.Contains(key)) return;
            var def = GameContent.Get().FindAchievement(key);
            if (def != null && !def.FromGame) return;
            Queue(key);
        }

        public void Progress(string key, int value)
        {
            if (string.IsNullOrEmpty(key) || _unlocked.Contains(key) || value <= 0) return;
            if (ProgressOf(key) >= value && _achReady) return;
            var def = GameContent.Get().FindAchievement(key);
            if (def != null && def.Target > 0) value = Mathf.Min(value, def.Target);
            _progress[key] = Mathf.Max(ProgressOf(key), value);
            Queue(key + "=" + value);
        }

        void Queue(string entry)
        {
            // one entry per achievement (a newer progress value replaces the old one)
            string key = entry.Split('=')[0];
            var list = _p.pendingAchievements;
            list.RemoveAll(e => e.Split('=')[0] == key);
            list.Add(entry);
            _p.SaveLocalOnly();
        }

        void PumpAchievements(float now)
        {
            if (!_achReady || _achBusy || now < _nextAch || _bp == null || !_bp.Ready) return;
            var list = _p.pendingAchievements;
            if (list.Count == 0) return;
            string entry = list[0];
            var parts = entry.Split('=');
            string key = parts[0];
            int value = parts.Length > 1 && int.TryParse(parts[1], out var v) ? v : -1;
            _achBusy = true;
            _nextAch = now + 1.2f; // stays well under the request limit

            Action<AchievementResult> ok = r =>
            {
                _achBusy = false;
                list.Remove(entry);
                _p.SaveLocalOnly();
                if (r != null)
                {
                    if (r.progress > ProgressOf(key)) _progress[key] = r.progress;
                    if (r.unlocked || r.already) _unlocked.Add(key);
                    if (r.unlocked && !r.already)
                    {
                        var def = GameContent.Get().FindAchievement(key);
                        AchievementUnlocked?.Invoke(def != null ? def.title.Get("en") : key);
                        _bp.RefreshBalance(); // coin reward is added by the site
                    }
                }
                Changed?.Invoke();
            };
            Action<string> fail = err =>
            {
                _achBusy = false;
                if (err == "ACHIEVEMENT_NOT_FOUND" || err == "ACHIEVEMENT_NOT_SDK" || err == "ACHIEVEMENT_NO_TARGET" || err == "INVALID_KEY" || err == "INVALID_VALUE")
                {
                    list.Remove(entry); // will never work: drop it
                    _p.SaveLocalOnly();
                    Debug.LogWarning("ArvinCombat: achievement " + key + " rejected: " + err);
                }
                else _nextAch = Time.unscaledTime + (err == "RATE_LIMITED" ? 20f : 10f); // keep it, try later
            };
            if (value >= 0) _bp.SetAchievementProgress(key, value, ok, fail);
            else _bp.UnlockAchievement(key, ok, fail);
        }

        // ================================================================ game events -> achievements

        static int Bits(int mask) { int n = 0; while (mask != 0) { n += mask & 1; mask >>= 1; } return n; }

        /// <summary>Checks every achievement that depends only on the saved profile.</summary>
        public void Recheck()
        {
            if (_p.wins >= 1) Unlock("first_win");
            Progress("wins_100", Mathf.Min(_p.wins, 100));
            if (_p.onlineWins >= 1) Unlock("first_online_win");
            Progress("online_wins_10", Mathf.Min(_p.onlineWins, 10));
            if (_p.LevelCleared(4)) Unlock("campaign_level_5");
            if (_p.LevelCleared(LevelDatabase.Levels.Count - 1)) Unlock("campaign_complete");
            Progress("parry_25", Mathf.Min(_p.parries, 25));
            Progress("all_warriors", Bits(_p.warriorWinMask & 63));
            foreach (int r in _p.ranks) if (r >= PlayerProfile.MaxRank) { Unlock("max_stat"); break; }
        }

        /// <summary>Called by the game after every finished fight (offline or online).</summary>
        public void OnFightFinished(MatchResult r, WarriorId warrior, bool online)
        {
            var s = r.stats ?? new FightStats();
            _p.parries += s.parries;
            if (r.playerWon)
            {
                _p.warriorWinMask |= 1 << (int)warrior;
                if (online) _p.onlineWins++;
                if (s.flawlessRound) Unlock("flawless_victory");
                if (s.untouchable) Unlock("untouchable");
                if (s.lostFirstRound) Unlock("comeback");
            }
            if (s.combo4) Unlock("combo_master");
            if (s.specialKO) Unlock("special_finish");
            if (s.airKO) Unlock("air_finish");
            Recheck();
        }
    }
}
