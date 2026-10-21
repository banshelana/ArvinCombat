using System;
using System.Collections;
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

    /// <summary>
    /// Talks to the Arvin Games site (window.GamePlatform) when running inside it, and simulates it
    /// everywhere else (Editor / local build): no player, no pause events, calls are just logged.
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
        public bool IsGuest => string.IsNullOrEmpty(PlayerId);

        public event Action OnReady;
        public event Action OnResumed;

        bool _gameplay;

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void ArvinGP_Init(string goName);
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
            // simulator
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
            Debug.Log("ArvinCombat: server " + OnlineConfig.Current.serverUrl);
        }

        [Serializable]
        class ReadyMsg { public bool platform; public string locale; public string playerId; public string displayName; }

        // called from JS (SendMessage)
        public void OnPlatformReady(string json)
        {
            ReadyMsg m = null;
            try { m = JsonUtility.FromJson<ReadyMsg>(json); } catch { }
            OnSite = m != null && m.platform;
            if (m != null)
            {
                PlayerId = m.playerId ?? "";
                DisplayName = m.displayName ?? "";
                if (!string.IsNullOrEmpty(m.locale)) Locale = m.locale;
            }
            Ready = true;
            OnReady?.Invoke();
        }

        // called from JS after the site resumes the game or the tab becomes visible again
        public void OnPlatformResume(string _)
        {
            OnResumed?.Invoke();
        }

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
            ArvinGP_TrackEvent(name, json ?? "{}");
#else
            Debug.Log("[GamePlatform sim] trackEvent " + name + " " + json);
#endif
        }
    }
}
