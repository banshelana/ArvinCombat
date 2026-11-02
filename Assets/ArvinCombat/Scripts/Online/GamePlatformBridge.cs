using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace ArvinCombat
{
    /// <summary>Server address, read from StreamingAssets/arvincombat.json (change it without rebuilding).</summary>
    [Serializable]
    public class OnlineConfig
    {
        public string serverUrl = "https://mm3.arvinplay.com";
        public string matchPath = "match";
        public string roomPath = "room";

        public static OnlineConfig Current = new OnlineConfig();
        public static bool Loaded;

        public string WsBase()
        {
            string b = (serverUrl ?? "").Trim().TrimEnd('/');
            if (b.StartsWith("https://")) return "wss://" + b.Substring(8);
            if (b.StartsWith("http://")) return "ws://" + b.Substring(7);
            return b;
        }
    }

    // ---------------------------------------------------------------- SDK data (Doc 1 / Doc 2)

    [Serializable] public class PlatformPlayer
    {
        public string playerId, username, displayName, avatarUrl, countryCode;
        public int level, totalXp, levelXp, nextLevelXp, coins, achievementsUnlocked, achievementsTotal;
        public bool isPreview;
    }
    [Serializable] public class PurchaseReceipt { public string receiptId, key, kind, status; public int price, balance; }
    [Serializable] public class InventoryItem { public string key, kind; public int count, remaining; }
    [Serializable] public class ConsumeResult { public string key; public int remaining; public bool test; }
    [Serializable] public class AchievementResult { public string key; public bool unlocked, already; public int progress, target; }
    [Serializable] public class AchievementInfo { public string key, title; public bool unlocked; public int progress, target; }
    [Serializable] public class SaveInfo { public string slot, savedAt; public int version, sizeBytes; }
    [Serializable] public class SaveData { public string slot, savedAt, data; public int version, sizeBytes; }

    /// <summary>
    /// Talks to the Arvin Games site (window.GamePlatform). Outside the site (Editor, local build)
    /// a simulator stands in: a test player with 1000 coins, local purchases, inventory, saves and achievements,
    /// so the shop and achievements can be tested without uploading.
    /// </summary>
    public class GamePlatformBridge : MonoBehaviour
    {
        public const string ObjectName = "GamePlatformBridge";
        public static GamePlatformBridge Instance { get; private set; }

        public bool OnSite { get; private set; }
        public bool Ready { get; private set; }
        public string PlayerId { get; private set; } = "";
        public string DisplayName { get; private set; } = "";
        public string Locale { get; private set; } = "en";
        public bool IsPreview { get; private set; }
        public int Coins { get; private set; }
        /// <summary>Guest on the site (no account): no purchases, no cloud save.</summary>
        public bool IsGuest => OnSite && string.IsNullOrEmpty(PlayerId);
        public bool Simulated => !OnSite;

        public event Action OnReady;
        public event Action OnResumed;
        public event Action OnPaused;
        public event Action<int> OnCoinsChanged;

        bool _gameplay;

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern int ArvinGP_Available();
        [DllImport("__Internal")] static extern void ArvinGP_Init(string goName);
        [DllImport("__Internal")] static extern void ArvinGP_Request(int id, string method, string arg);
        [DllImport("__Internal")] static extern void ArvinGP_Call(string method);
        [DllImport("__Internal")] static extern void ArvinGP_ReportError(string msg, string detail);
        [DllImport("__Internal")] static extern void ArvinGP_TrackEvent(string name, string json);
#endif

        public static GamePlatformBridge Create()
        {
            if (Instance != null) return Instance;
            var go = new GameObject(ObjectName);
            DontDestroyOnLoad(go);
            return go.AddComponent<GamePlatformBridge>();
        }

        void Awake()
        {
            Instance = this;
            gameObject.name = ObjectName; // SendMessage target
            StartCoroutine(LoadConfig());
#if UNITY_WEBGL && !UNITY_EDITOR
            ArvinGP_Init(ObjectName);
#else
            OnPlatformReady("{\"platform\":false}");
#endif
        }

        IEnumerator LoadConfig()
        {
            string path = Application.streamingAssetsPath + "/arvincombat.json";
            string json = null;
            if (path.Contains("://"))
            {
                using (var req = UnityWebRequest.Get(path + "?v=" + DateTime.UtcNow.Ticks))
                {
                    yield return req.SendWebRequest();
                    if (req.result == UnityWebRequest.Result.Success) json = req.downloadHandler.text;
                    else Debug.LogWarning("ArvinCombat: could not read " + path + " (" + req.error + "), using defaults");
                }
            }
            else if (System.IO.File.Exists(path)) json = System.IO.File.ReadAllText(path);

            if (!string.IsNullOrEmpty(json))
            {
                try { JsonUtility.FromJsonOverwrite(json, OnlineConfig.Current); }
                catch (Exception e) { Debug.LogWarning("ArvinCombat: bad arvincombat.json: " + e.Message); }
            }
            OnlineConfig.Loaded = true;
        }

        // ---------------------------------------------------------------- callbacks from JS

        [Serializable] class ReadyMsg { public bool platform; public string locale; public string playerId; public string displayName; public int coins; public bool isPreview; }

        public void OnPlatformReady(string json)
        {
            ReadyMsg m = null;
            try { m = JsonUtility.FromJson<ReadyMsg>(json); } catch { }
            OnSite = m != null && m.platform;
            if (OnSite)
            {
                PlayerId = m.playerId ?? "";
                DisplayName = m.displayName ?? "";
                if (!string.IsNullOrEmpty(m.locale)) Locale = m.locale;
                IsPreview = m.isPreview;
                SetCoins(m.coins);
            }
            else
            {
                Sim.Init();
                PlayerId = "editor-test-player";
                DisplayName = "";
                SetCoins(Sim.Coins);
            }
            Ready = true;
            OnReady?.Invoke();
        }

        public void OnPlatformPause(string _) => OnPaused?.Invoke();

        public void OnPlatformResume(string _)
        {
            RefreshBalance();
            OnResumed?.Invoke();
        }

        void SetCoins(int c)
        {
            Coins = Mathf.Max(0, c);
            OnCoinsChanged?.Invoke(Coins);
        }

        // ---------------------------------------------------------------- requests with replies

        [Serializable] class Reply { public int id; public bool ok; public string json; public string error; }
        [Serializable] class Box<T> { public bool isNull; public T value; public double number; public T[] items; }
        [Serializable] class ProgressArg { public string key; public int value; }
        [Serializable] class SaveArg { public string data, slot; public int baseVersion; }
        [Serializable] class ConsumeArg { public string key, idempotencyKey; public int count; }

        readonly Dictionary<int, Action<bool, string, string>> _pending = new Dictionary<int, Action<bool, string, string>>();
        int _nextId = 1;

        void Request(string method, string arg, Action<bool, string, string> done)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (OnSite)
            {
                int id = _nextId++;
                _pending[id] = done;
                ArvinGP_Request(id, method, arg ?? "");
                return;
            }
#endif
            // simulator answers right away (same JSON shape as the site)
            Sim.Handle(method, arg, done);
        }

        public void OnReply(string message)
        {
            Reply r;
            try { r = JsonUtility.FromJson<Reply>(message); } catch { return; }
            if (_pending.TryGetValue(r.id, out var done))
            {
                _pending.Remove(r.id);
                done(r.ok, r.json, r.error);
            }
        }

        void Value<T>(string method, string arg, Action<T> onDone, Action<string> onError) where T : class =>
            Request(method, arg, (ok, json, error) =>
            {
                if (!ok) { onError?.Invoke(error); return; }
                Box<T> box = null;
                try { box = JsonUtility.FromJson<Box<T>>(json); } catch { }
                onDone?.Invoke(box == null || box.isNull ? null : box.value);
            });

        void Items<T>(string method, Action<T[]> onDone, Action<string> onError) =>
            Request(method, null, (ok, json, error) =>
            {
                if (!ok) { if (onError != null) onError(error); else onDone?.Invoke(new T[0]); return; }
                Box<T> box = null;
                try { box = JsonUtility.FromJson<Box<T>>(json); } catch { }
                onDone?.Invoke(box?.items ?? new T[0]);
            });

        // ---------------------------------------------------------------- player / wallet

        public void GetPlayer(Action<PlatformPlayer> onDone, Action<string> onError = null) => Value("getPlayer", null, onDone, onError);

        public void RefreshBalance(Action<int> onDone = null) =>
            Request("getBalance", null, (ok, json, _) =>
            {
                if (ok)
                {
                    try { SetCoins((int)JsonUtility.FromJson<Box<PlatformPlayer>>(json).number); } catch { }
                }
                onDone?.Invoke(Coins);
            });

        // ---------------------------------------------------------------- store

        public void Purchase(string key, Action<PurchaseReceipt> onDone, Action<string> onError) =>
            Value<PurchaseReceipt>("purchase", key, r => { if (r != null) SetCoins(r.balance); onDone?.Invoke(r); }, onError);

        public void Acknowledge(string receiptId, Action<bool> onDone = null) =>
            Request("acknowledge", receiptId, (ok, _, __) => onDone?.Invoke(ok));

        public void GetPendingPurchases(Action<PurchaseReceipt[]> onDone) => Items("getPendingPurchases", onDone, null);
        public void GetInventory(Action<InventoryItem[]> onDone, Action<string> onError = null) => Items("getInventory", onDone, onError);

        public void Consume(string key, int count, string idempotencyKey, Action<ConsumeResult> onDone, Action<string> onError) =>
            Value("consume", JsonUtility.ToJson(new ConsumeArg { key = key, count = count, idempotencyKey = idempotencyKey }), onDone, onError);

        // ---------------------------------------------------------------- cloud save

        public void SaveData(string json, Action<SaveInfo> onDone, Action<string> onError, int baseVersion = -1, string slot = "main") =>
            Value("saveData", JsonUtility.ToJson(new SaveArg { data = json, slot = slot, baseVersion = baseVersion }), onDone, onError);

        public void LoadData(Action<SaveData> onDone, Action<string> onError, string slot = "main") => Value("loadData", slot, onDone, onError);

        // ---------------------------------------------------------------- achievements

        public void UnlockAchievement(string key, Action<AchievementResult> onDone = null, Action<string> onError = null) =>
            Value("unlockAchievement", key, onDone, onError);

        public void SetAchievementProgress(string key, int value, Action<AchievementResult> onDone = null, Action<string> onError = null) =>
            Value("setAchievementProgress", JsonUtility.ToJson(new ProgressArg { key = key, value = value }), onDone, onError);

        public void GetAchievements(Action<AchievementInfo[]> onDone, Action<string> onError = null) => Items("getAchievements", onDone, onError);

        // ---------------------------------------------------------------- fire-and-forget

        void Call(string method)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            ArvinGP_Call(method);
#else
            Debug.Log("[GamePlatform sim] " + method + "()");
#endif
        }

        public void LoadingFinished() => Call("gameLoadingFinished");

        public void GameplayStart()
        {
            if (_gameplay) return;
            _gameplay = true;
            Call("gameplayStart");
        }

        public void GameplayStop()
        {
            if (!_gameplay) return;
            _gameplay = false;
            Call("gameplayStop");
        }

        /// <summary>Keep the message constant; put changing values in detail.</summary>
        public void ReportError(string message, string detail)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            ArvinGP_ReportError(message, detail ?? "");
#else
            Debug.LogWarning("[GamePlatform sim] reportError: " + message + " | " + detail);
#endif
        }

        public void TrackEvent(string name, string json)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            ArvinGP_TrackEvent(name, json ?? "");
#else
            Debug.Log("[GamePlatform sim] trackEvent " + name + " " + json);
#endif
        }

        // ================================================================ simulator (Editor / outside the site)

        static class Sim
        {
            const string KCoins = "ArvinSim.coins", KInv = "ArvinSim.inventory", KAch = "ArvinSim.achievements", KSave = "ArvinSim.save.";

            [Serializable] class InvList { public List<InventoryItem> items = new List<InventoryItem>(); }
            [Serializable] class AchState { public string key; public bool unlocked; public int progress; }
            [Serializable] class AchList { public List<AchState> items = new List<AchState>(); }

            public static int Coins => PlayerPrefs.GetInt(KCoins, 1000);
            public static void Init() { if (!PlayerPrefs.HasKey(KCoins)) PlayerPrefs.SetInt(KCoins, 1000); }

            static InvList Inv() { try { return JsonUtility.FromJson<InvList>(PlayerPrefs.GetString(KInv, "")) ?? new InvList(); } catch { return new InvList(); } }
            static void SaveInv(InvList l) { PlayerPrefs.SetString(KInv, JsonUtility.ToJson(l)); PlayerPrefs.Save(); }
            static AchList Ach() { try { return JsonUtility.FromJson<AchList>(PlayerPrefs.GetString(KAch, "")) ?? new AchList(); } catch { return new AchList(); } }
            static void SaveAch(AchList l) { PlayerPrefs.SetString(KAch, JsonUtility.ToJson(l)); PlayerPrefs.Save(); }

            static string Ok<T>(T value) => JsonUtility.ToJson(new Box<T> { value = value, isNull = value == null });
            static string OkItems<T>(T[] items) => JsonUtility.ToJson(new Box<T> { items = items, isNull = false });
            static string OkNumber(double n) => JsonUtility.ToJson(new Box<PlatformPlayer> { number = n, isNull = false });

            public static void Handle(string method, string arg, Action<bool, string, string> done)
            {
                try { HandleInner(method, arg, done); }
                catch (Exception e) { done(false, null, "FAILED " + e.Message); }
            }

            static void HandleInner(string method, string arg, Action<bool, string, string> done)
            {
                switch (method)
                {
                    case "getPlayer":
                        done(true, Ok(new PlatformPlayer { playerId = "editor-test-player", displayName = "Tester", coins = Coins, isPreview = true }), null);
                        return;
                    case "getBalance":
                        done(true, OkNumber(Coins), null);
                        return;
                    case "purchase":
                    {
                        var item = GameContent.Get().FindItem(arg);
                        if (item == null) { done(false, null, "ITEM_NOT_FOUND"); return; }
                        var inv = Inv();
                        var have = inv.items.Find(i => i.key == arg);
                        if (item.IsPermanent && have != null && have.count > 0) { done(false, null, "ALREADY_OWNED"); return; }
                        if (item.maxPerUser > 0 && have != null && have.count >= item.maxPerUser) { done(false, null, "PURCHASE_LIMIT"); return; }
                        if (Coins < item.price) { done(false, null, "INSUFFICIENT_FUNDS"); return; }
                        PlayerPrefs.SetInt(KCoins, Coins - item.price);
                        if (have == null) { have = new InventoryItem { key = arg, kind = item.IsPermanent ? "Permanent" : "Consumable" }; inv.items.Add(have); }
                        have.count++; have.remaining++;
                        SaveInv(inv);
                        Debug.Log($"[GamePlatform sim] bought {arg} for {item.price} coins (left {Coins})");
                        done(true, Ok(new PurchaseReceipt { receiptId = Guid.NewGuid().ToString(), key = arg, kind = have.kind, status = "Test", price = item.price, balance = Coins }), null);
                        return;
                    }
                    case "acknowledge": done(true, Ok<PlatformPlayer>(null), null); return;
                    case "getPendingPurchases": done(true, OkItems(new PurchaseReceipt[0]), null); return;
                    case "getInventory": done(true, OkItems(Inv().items.ToArray()), null); return;
                    case "consume":
                    {
                        var a = JsonUtility.FromJson<ConsumeArg>(arg);
                        var inv = Inv();
                        var have = inv.items.Find(i => i.key == a.key);
                        int n = Mathf.Max(1, a.count);
                        if (have == null || have.remaining < n) { done(false, null, "INSUFFICIENT_ITEMS"); return; }
                        if (have.kind == "Permanent") { done(false, null, "NOT_CONSUMABLE"); return; }
                        have.remaining -= n;
                        SaveInv(inv);
                        done(true, Ok(new ConsumeResult { key = a.key, remaining = have.remaining, test = true }), null);
                        return;
                    }
                    case "saveData":
                    {
                        var a = JsonUtility.FromJson<SaveArg>(arg);
                        string slot = string.IsNullOrEmpty(a.slot) ? "main" : a.slot;
                        int ver = PlayerPrefs.GetInt(KSave + slot + ".v", 0);
                        if (a.baseVersion >= 0 && a.baseVersion != ver) { done(false, null, "SAVE_CONFLICT"); return; }
                        PlayerPrefs.SetString(KSave + slot, a.data);
                        PlayerPrefs.SetInt(KSave + slot + ".v", ver + 1);
                        PlayerPrefs.Save();
                        done(true, Ok(new SaveInfo { slot = slot, version = ver + 1, sizeBytes = a.data.Length, savedAt = DateTime.UtcNow.ToString("o") }), null);
                        return;
                    }
                    case "loadData":
                    {
                        string slot = string.IsNullOrEmpty(arg) ? "main" : arg;
                        if (!PlayerPrefs.HasKey(KSave + slot)) { done(true, Ok<SaveData>(null), null); return; }
                        done(true, Ok(new SaveData { slot = slot, data = PlayerPrefs.GetString(KSave + slot), version = PlayerPrefs.GetInt(KSave + slot + ".v", 0) }), null);
                        return;
                    }
                    case "unlockAchievement":
                    case "setAchievementProgress":
                    {
                        string key = arg; int value = 0;
                        if (method == "setAchievementProgress") { var p = JsonUtility.FromJson<ProgressArg>(arg); key = p.key; value = p.value; }
                        var def = GameContent.Get().FindAchievement(key);
                        if (def == null) { done(false, null, "ACHIEVEMENT_NOT_FOUND"); return; }
                        if (method == "setAchievementProgress" && def.Target <= 0) { done(false, null, "ACHIEVEMENT_NO_TARGET"); return; }
                        var list = Ach();
                        var st = list.items.Find(x => x.key == key);
                        if (st == null) { st = new AchState { key = key }; list.items.Add(st); }
                        bool already = st.unlocked;
                        st.progress = Mathf.Max(st.progress, value);
                        bool now = !already && (method == "unlockAchievement" || st.progress >= def.Target);
                        if (now) { st.unlocked = true; Debug.Log($"[GamePlatform sim] ACHIEVEMENT UNLOCKED: {def.title.en} (+{def.points} XP, +{def.coinReward} coins)"); PlayerPrefs.SetInt(KCoins, Coins + def.coinReward); }
                        SaveAch(list);
                        done(true, Ok(new AchievementResult { key = key, unlocked = now, already = already, progress = st.progress, target = def.Target }), null);
                        return;
                    }
                    case "getAchievements":
                    {
                        var list = Ach();
                        var outList = new List<AchievementInfo>();
                        foreach (var d in GameContent.Get().achievements)
                        {
                            if (!d.active) continue;
                            var st = list.items.Find(x => x.key == d.key);
                            bool un = st != null && st.unlocked;
                            outList.Add(new AchievementInfo { key = d.key, title = d.hidden && !un ? "" : d.title.en, unlocked = un, progress = st != null ? st.progress : 0, target = d.Target });
                        }
                        done(true, OkItems(outList.ToArray()), null);
                        return;
                    }
                    default:
                        done(false, null, "NOT_SUPPORTED");
                        return;
                }
            }
        }
    }
}
