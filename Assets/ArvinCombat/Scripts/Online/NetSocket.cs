using System;
using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#else
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
#endif

namespace ArvinCombat
{
    /// <summary>
    /// Text WebSocket that works in the browser (WebGL, via ArvinWebSocket.jslib)
    /// and in the Editor / desktop builds (System.Net.WebSockets). Poll it from Update.
    /// State: 0 connecting, 1 open, 3 closed.
    /// </summary>
    public class NetSocket
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern int ArvinWS_Connect(string url);
        [DllImport("__Internal")] static extern int ArvinWS_State(int id);
        [DllImport("__Internal")] static extern int ArvinWS_Send(int id, string msg);
        [DllImport("__Internal")] static extern string ArvinWS_Poll(int id);
        [DllImport("__Internal")] static extern void ArvinWS_Close(int id);
        [DllImport("__Internal")] static extern string ArvinWS_PageUrl();

        int _id = -1;

        public static string PageUrl() { try { return ArvinWS_PageUrl(); } catch { return ""; } }
        public void Connect(string url) { _id = ArvinWS_Connect(url); }
        public int State => _id < 0 ? 3 : ArvinWS_State(_id);
        public void Send(string text) { if (_id >= 0) ArvinWS_Send(_id, text); }
        public bool TryReceive(out string text)
        {
            text = _id < 0 ? null : ArvinWS_Poll(_id);
            return text != null;
        }
        public void Close() { if (_id >= 0) ArvinWS_Close(_id); _id = -1; }
#else
        ClientWebSocket _ws;
        CancellationTokenSource _cts;
        readonly ConcurrentQueue<string> _in = new ConcurrentQueue<string>();
        readonly ConcurrentQueue<string> _out = new ConcurrentQueue<string>();
        volatile int _state = 3;

        public static string PageUrl() => "";
        public int State => _state;

        public async void Connect(string url)
        {
            _state = 0;
            _ws = new ClientWebSocket();
            _cts = new CancellationTokenSource();
            try
            {
                await _ws.ConnectAsync(new Uri(url), _cts.Token);
                _state = 1;
                _ = ReceiveLoop(_ws, _cts.Token);
                _ = SendLoop(_ws, _cts.Token);
            }
            catch (Exception e)
            {
                Debug.LogWarning("ArvinCombat: connect failed " + url + " : " + e.Message);
                _state = 3;
            }
        }

        async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
        {
            var buf = new byte[16384];
            var ms = new MemoryStream();
            try
            {
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buf, 0, r.Count);
                    if (r.EndOfMessage)
                    {
                        if (r.MessageType == WebSocketMessageType.Text) _in.Enqueue(Encoding.UTF8.GetString(ms.ToArray()));
                        ms.SetLength(0);
                    }
                }
            }
            catch (Exception) { }
            _state = 3;
        }

        async Task SendLoop(ClientWebSocket ws, CancellationToken ct)
        {
            try
            {
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    if (_out.TryDequeue(out var s))
                    {
                        var bytes = Encoding.UTF8.GetBytes(s);
                        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
                    }
                    else await Task.Delay(4, ct);
                }
            }
            catch (Exception) { }
        }

        public void Send(string text) { if (_state == 1) _out.Enqueue(text); }
        public bool TryReceive(out string text) => _in.TryDequeue(out text);

        public void Close()
        {
            _state = 3;
            try { _cts?.Cancel(); } catch { }
            try { _ws?.Abort(); _ws?.Dispose(); } catch { }
            _ws = null;
        }
#endif
    }

    /// <summary>All messages share one shape (JsonUtility friendly). Unused fields are ignored.</summary>
    [Serializable]
    public class NetMsg
    {
        public string t;
        // hello / match
        public string name;
        public int warrior;
        public int[] ranks;
        public int ver;
        public int side;
        public string oppName;
        public int oppWarrior;
        public int[] oppRanks;
        public int oppRating;
        public int rounds;
        public float roundTime;
        public int online;
        public int port;          // room server port (from the match service)
        public string ticket;     // one-time ticket for the room
        public string playerId;   // Arvin Games player id (display / stats only, not verified)
        public string roomPath;   // "room"  ->  wss://domain/room/{port}
        // rounds
        public int n;
        public int sa, sb;
        public int w;
        public string reason;
        // fighter state
        public float x, y, z, ry;
        public int s;
        public string a;
        public int q;
        public float asp, mx, mz, ms;
        public float hp, mhp, stm, sp;
        // hits
        public string atk;
        public float dmg;
        public float amt;
        public bool blk;
        public string txt;
        public float stun;
    }

    /// <summary>Compact snapshot sent 20x per second.</summary>
    [Serializable]
    public class NetState
    {
        public string t = "st";
        public float x, y, z, ry;
        public int s;
        public string a;
        public int q;
        public float asp, mx, mz, ms;
        public float hp, mhp, stm, sp;
    }
}
