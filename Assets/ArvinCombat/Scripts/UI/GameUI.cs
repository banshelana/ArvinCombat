using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ArvinCombat
{
    /// <summary>
    /// All screens (menus, warrior select, levels, upgrades, online, result, pause) and the fight HUD.
    /// Built entirely from code so the web build has no UI textures to download.
    /// </summary>
    public class GameUI : MonoBehaviour
    {
        GameManager gm;
        Canvas _menuCanvas, _hudCanvas, _fxCanvas;
        RectTransform _menuRoot, _hudRoot, _fxRoot;
        readonly Dictionary<string, RectTransform> _screens = new Dictionary<string, RectTransform>();

        // HUD
        class SideHud
        {
            public Text name, info, specialText;
            public RectTransform hp, hpTrail, stamina, special;
            public Image[] pips;
            public float trail = 1f;
        }
        SideHud _left, _right;
        Text _timer, _center, _hint;
        Text[] _moveRows;
        AttackData[] _moveData;
        int _moveDefId = -1;
        float _centerUntil;
        readonly List<Text> _floating = new List<Text>();
        readonly List<float> _floatingLife = new List<float>();
        readonly List<Vector3> _floatingWorld = new List<Vector3>();

        // ------------------------------------------------------------ build

        public void Build(GameManager manager)
        {
            gm = manager;
            UIKit.EnsureEventSystem();

            _hudCanvas = UIKit.CreateCanvas("HUD Canvas", 5);
            _hudCanvas.transform.SetParent(transform, false);
            _hudRoot = (RectTransform)_hudCanvas.transform;

            _menuCanvas = UIKit.CreateCanvas("Menu Canvas", 10);
            _menuCanvas.transform.SetParent(transform, false);
            _menuRoot = (RectTransform)_menuCanvas.transform;

            _fxCanvas = UIKit.CreateCanvas("FX Canvas", 20);
            _fxCanvas.transform.SetParent(transform, false);
            _fxRoot = (RectTransform)_fxCanvas.transform;
            _fxCanvas.GetComponent<GraphicRaycaster>().enabled = false;

            BuildHud();
            _center = UIKit.MakeLabel(_fxRoot, "", 110, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            _center.rectTransform.Place(new Vector2(0.5f, 0.6f), new Vector2(1800, 200), Vector2.zero);
            var outline = _center.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0, 0, 0, 0.8f);
            outline.effectDistance = new Vector2(4, -4);
            ShowHud(false);
        }

        public string CurrentScreen { get; private set; } = "";

        RectTransform NewScreen(string id, bool darkBg = true)
        {
            CurrentScreen = id;
            if (_screens.TryGetValue(id, out var old)) Destroy(old.gameObject);
            var r = UIKit.Rect(_menuRoot, "Screen_" + id).Stretch();
            if (darkBg) r.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.55f);
            _screens[id] = r;
            foreach (var kv in _screens) kv.Value.gameObject.SetActive(kv.Key == id);
            return r;
        }

        public void HideMenus()
        {
            CurrentScreen = "";
            foreach (var kv in _screens) kv.Value.gameObject.SetActive(false);
        }

        RectTransform Window(RectTransform screen, Vector2 size, string title)
        {
            var w = UIKit.MakeImage(screen, "Window", UIKit.Panel).rectTransform.Place(new Vector2(0.5f, 0.5f), size, Vector2.zero);
            var t = UIKit.MakeLabel(w, title, 54, TextAnchor.MiddleCenter, UIKit.Accent, FontStyle.Bold);
            t.rectTransform.anchorMin = new Vector2(0, 1); t.rectTransform.anchorMax = new Vector2(1, 1);
            t.rectTransform.pivot = new Vector2(0.5f, 1f);
            t.rectTransform.sizeDelta = new Vector2(0, 90);
            t.rectTransform.anchoredPosition = new Vector2(0, -10);
            return w;
        }

        RectTransform Body(RectTransform window, float top = 110, float bottom = 30)
        {
            var b = UIKit.Rect(window, "Body").Stretch(40, top, 40, bottom);
            return b;
        }

        // ------------------------------------------------------------ main menu

        public void ShowMainMenu()
        {
            var s = NewScreen("main", false);
            var title = UIKit.MakeLabel(s, "ARVIN COMBAT", 120, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            title.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(1600, 180), new Vector2(0, -70));
            title.gameObject.AddComponent<Outline>().effectDistance = new Vector2(5, -5);
            var sub = UIKit.MakeLabel(s, "Choose your warrior. Win fights. Grow stronger.", 34, TextAnchor.MiddleCenter, UIKit.Muted);
            sub.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(1600, 60), new Vector2(0, -240));

            var panel = UIKit.MakeImage(s, "Menu", UIKit.Panel).rectTransform.Place(new Vector2(0.5f, 0.42f), new Vector2(640, 600), Vector2.zero);
            UIKit.VLayout(panel.gameObject, 16, 30);

            var nameLbl = UIKit.MakeLabel(panel, "Your fighter name", 26, TextAnchor.MiddleCenter, UIKit.Muted);
            UIKit.Size(nameLbl, 34);
            var input = UIKit.MakeInput(panel, "Enter your name...", gm.profile.playerName, 34);
            UIKit.Size(input, 70);
            input.onEndEdit.AddListener(v => gm.SetPlayerName(v));
            input.interactable = !gm.NameFromSite; // logged-in players use their site name

            var xp = UIKit.MakeLabel(panel, $"XP Power: <color=#{UIKit.Hex(UIKit.Accent)}>{gm.profile.xp}</color>     Wins: {gm.profile.wins}", 28, TextAnchor.MiddleCenter);
            UIKit.Size(xp, 40);

            UIKit.Size(UIKit.MakeButton(panel, "OFFLINE  -  CAMPAIGN", () => { gm.SetPlayerName(input.text); gm.ChooseMode(PlayMode.Campaign); }, UIKit.Accent * 0.8f), 76);
            UIKit.Size(UIKit.MakeButton(panel, "ONLINE  -  1 VS 1", () => { gm.SetPlayerName(input.text); gm.OpenOnline(); }), 76);
            UIKit.Size(UIKit.MakeButton(panel, "QUICK FIGHT", () => { gm.SetPlayerName(input.text); gm.ChooseMode(PlayMode.QuickFight); }), 76);
            UIKit.Size(UIKit.MakeButton(panel, "UPGRADE WARRIORS", () => { gm.SetPlayerName(input.text); gm.OpenUpgrades(false); }), 76);
        }

        // ------------------------------------------------------------ warrior select

        public void ShowWarriorSelect(string subtitle)
        {
            var s = NewScreen("select");
            var w = Window(s, new Vector2(1820, 1000), "CHOOSE YOUR WARRIOR");
            var sub = UIKit.MakeLabel(w, subtitle, 28, TextAnchor.MiddleCenter, UIKit.Muted);
            sub.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(1600, 40), new Vector2(0, -95));

            var grid = UIKit.Rect(w, "Grid").Stretch(30, 150, 30, 110);
            var g = grid.gameObject.AddComponent<GridLayoutGroup>();
            g.cellSize = new Vector2(575, 360);
            g.spacing = new Vector2(18, 18);
            g.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            g.constraintCount = 3;
            g.childAlignment = TextAnchor.UpperCenter;

            foreach (var def in WarriorDatabase.All)
            {
                var d = def;
                var card = UIKit.MakeImage(grid, d.displayName, UIKit.PanelLight);
                var btn = card.gameObject.AddComponent<Button>();
                btn.targetGraphic = card;
                btn.onClick.AddListener(() => gm.SelectWarrior(d.id));
                bool selected = (int)d.id == gm.profile.selectedWarrior;
                if (selected) card.color = new Color(0.25f, 0.2f, 0.12f, 1f);

                var stripe = UIKit.MakeImage(card.transform, "Stripe", d.uiColor);
                stripe.rectTransform.anchorMin = new Vector2(0, 1); stripe.rectTransform.anchorMax = new Vector2(1, 1);
                stripe.rectTransform.pivot = new Vector2(0.5f, 1); stripe.rectTransform.sizeDelta = new Vector2(0, 8);
                stripe.raycastTarget = false;

                int rating = gm.profile.PowerRating(d.id);
                var name = UIKit.MakeLabel(card.transform, $"{d.displayName.ToUpper()}   <size=22><color=#{UIKit.Hex(UIKit.Muted)}>upgrades {rating}/{PlayerProfile.StatCount * PlayerProfile.MaxRank}</color></size>", 36, TextAnchor.UpperLeft, d.uiColor, FontStyle.Bold);
                name.rectTransform.Stretch(20, 18, 20, 0);
                var desc = UIKit.MakeLabel(card.transform,
                    $"{d.description}\n<b>Click</b> - {d.handSetName}   <b>Q</b> - {d.kickSetName}\n<b>Passive</b> - {d.passiveText}\n<b>Special</b> - {d.specialText}", 21, TextAnchor.UpperLeft, UIKit.TextColor);
                desc.rectTransform.Stretch(20, 70, 20, 90);

                var stats = RuntimeStats.Build(d, gm.profile.GetRanks(d.id), 1f, null);
                var statRow = UIKit.Rect(card.transform, "Stats");
                statRow.anchorMin = new Vector2(0, 0); statRow.anchorMax = new Vector2(1, 0); statRow.pivot = new Vector2(0.5f, 0);
                statRow.sizeDelta = new Vector2(-40, 80); statRow.anchoredPosition = new Vector2(0, 14);
                UIKit.HLayout(statRow.gameObject, 12, 0);
                StatMini(statRow, "HEALTH", stats.maxHealth / 1800f);
                StatMini(statRow, "POWER", stats.power / 1.8f);
                StatMini(statRow, "SPEED", stats.speed / 1.6f);
                StatMini(statRow, "DEFENSE", stats.defense / 0.6f);
            }

            var back = UIKit.MakeButton(w, "BACK", () => ShowMainMenu());
            back.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0f), new Vector2(300, 70), new Vector2(0, 25));
        }

        void StatMini(Transform parent, string label, float v)
        {
            var col = UIKit.Rect(parent, label);
            var t = UIKit.MakeLabel(col, label, 18, TextAnchor.UpperCenter, UIKit.Muted);
            t.rectTransform.Stretch(0, 0, 0, 40);
            var fill = UIKit.MakeBar(col, "bar", new Color(0, 0, 0, 0.6f), UIKit.Accent, out var back);
            back.anchorMin = new Vector2(0, 0); back.anchorMax = new Vector2(1, 0); back.pivot = new Vector2(0.5f, 0);
            back.sizeDelta = new Vector2(0, 18); back.anchoredPosition = new Vector2(0, 10);
            back.GetComponent<Image>().raycastTarget = false;
            fill.GetComponent<Image>().raycastTarget = false;
            UIKit.SetBar(fill, v);
        }

        // ------------------------------------------------------------ level select

        public void ShowLevelSelect()
        {
            var s = NewScreen("levels");
            var w = Window(s, new Vector2(1500, 1000), "CAMPAIGN");
            var warrior = WarriorDatabase.Get((WarriorId)gm.profile.selectedWarrior);
            var info = UIKit.MakeLabel(w, $"Fighting as <color=#{UIKit.Hex(warrior.uiColor)}><b>{warrior.displayName}</b></color>   -   XP Power: {gm.profile.xp}", 28, TextAnchor.MiddleCenter);
            info.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(1400, 44), new Vector2(0, -95));

            var list = UIKit.Rect(w, "List").Stretch(40, 150, 40, 120);
            UIKit.VLayout(list.gameObject, 8, 0);

            foreach (var lvl in LevelDatabase.Levels)
            {
                var L = lvl;
                bool unlocked = L.index <= gm.profile.highestLevelUnlocked;
                bool cleared = L.index < gm.profile.highestLevelUnlocked;
                var ew = WarriorDatabase.Get(L.warrior);
                var row = UIKit.MakeImage(list, "Level" + L.index, unlocked ? UIKit.PanelLight : new Color(0.08f, 0.08f, 0.1f, 1f));
                UIKit.Size(row, 62);
                var b = row.gameObject.AddComponent<Button>();
                b.targetGraphic = row;
                b.interactable = unlocked;
                b.onClick.AddListener(() => gm.StartLevel(L.index));

                string traits = "";
                foreach (var t in L.traits) traits += (traits.Length > 0 ? ", " : "") + t;
                string status = cleared ? "<color=#7CFC00>CLEARED</color>" : unlocked ? $"<color=#{UIKit.Hex(UIKit.Accent)}>FIGHT</color>" : "LOCKED";
                var txt = UIKit.MakeLabel(row.transform,
                    $"<b>{L.index + 1}.  {L.enemyName}</b>  <color=#{UIKit.Hex(UIKit.Muted)}>\"{L.title}\"</color>   -   <color=#{UIKit.Hex(ew.uiColor)}>{ew.displayName}</color>" +
                    (traits.Length > 0 ? $"   <size=20><color=#{UIKit.Hex(UIKit.Muted)}>[{traits}]</color></size>" : ""),
                    26, TextAnchor.MiddleLeft, unlocked ? UIKit.TextColor : UIKit.Muted);
                txt.rectTransform.Stretch(20, 0, 260, 0);
                var st = UIKit.MakeLabel(row.transform, $"{status}   <size=20>+{L.xpReward} XP</size>", 24, TextAnchor.MiddleRight);
                st.rectTransform.Stretch(900, 0, 20, 0);
            }

            var bar = UIKit.Rect(w, "Buttons");
            bar.Place(new Vector2(0.5f, 0f), new Vector2(1000, 76), new Vector2(0, 25));
            UIKit.HLayout(bar.gameObject, 20, 0);
            UIKit.MakeButton(bar, "CHANGE WARRIOR", () => gm.ChooseMode(PlayMode.Campaign));
            UIKit.MakeButton(bar, "UPGRADES", () => gm.OpenUpgrades(true));
            UIKit.MakeButton(bar, "MENU", () => ShowMainMenu());
        }

        // ------------------------------------------------------------ upgrades

        public void ShowUpgrades(WarriorId id)
        {
            var s = NewScreen("upgrades");
            var w = Window(s, new Vector2(1500, 960), "UPGRADE WARRIORS");
            var def = WarriorDatabase.Get(id);

            var xp = UIKit.MakeLabel(w, $"XP Power available: <color=#{UIKit.Hex(UIKit.Accent)}><b>{gm.profile.xp}</b></color>", 32, TextAnchor.MiddleCenter);
            xp.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(1400, 50), new Vector2(0, -95));

            var tabs = UIKit.Rect(w, "Tabs");
            tabs.Place(new Vector2(0.5f, 1f), new Vector2(1420, 64), new Vector2(0, -160));
            UIKit.HLayout(tabs.gameObject, 10, 0);
            foreach (var d in WarriorDatabase.All)
            {
                var dd = d;
                var tb = UIKit.MakeButton(tabs, d.displayName, () => ShowUpgrades(dd.id), d.id == id ? dd.uiColor * 0.8f : UIKit.PanelLight, 24);
            }

            var list = UIKit.Rect(w, "Stats").Stretch(60, 260, 60, 130);
            UIKit.VLayout(list.gameObject, 14, 0);

            var cur = RuntimeStats.Build(def, gm.profile.GetRanks(id), 1f, null);
            string[] names = { "POWER", "SPEED", "HEALTH (blood)", "DEFENSE", "STAMINA" };
            string[] values =
            {
                $"x{cur.power:0.00} damage", $"x{cur.speed:0.00} speed", $"{cur.maxHealth:0} HP", $"{cur.defense * 100f:0}% less damage", $"{cur.maxStamina:0} stamina"
            };
            for (int i = 0; i < PlayerProfile.StatCount; i++)
            {
                var stat = (UpgradeStat)i;
                int rank = gm.profile.GetRank(id, stat);
                var row = UIKit.MakeImage(list, names[i], UIKit.PanelLight);
                UIKit.Size(row, 92);
                var n = UIKit.MakeLabel(row.transform, $"<b>{names[i]}</b>\n<size=22><color=#{UIKit.Hex(UIKit.Muted)}>{values[i]}</color></size>", 30, TextAnchor.MiddleLeft);
                n.rectTransform.Stretch(24, 0, 1000, 0);

                var pips = UIKit.Rect(row.transform, "Pips");
                pips.anchorMin = new Vector2(0, 0.5f); pips.anchorMax = new Vector2(0, 0.5f); pips.pivot = new Vector2(0, 0.5f);
                pips.sizeDelta = new Vector2(520, 30); pips.anchoredPosition = new Vector2(380, 0);
                UIKit.HLayout(pips.gameObject, 6, 0);
                for (int p = 0; p < PlayerProfile.MaxRank; p++)
                    UIKit.MakeImage(pips, "pip", p < rank ? def.uiColor : new Color(0, 0, 0, 0.5f)).raycastTarget = false;

                bool max = rank >= PlayerProfile.MaxRank;
                int cost = PlayerProfile.UpgradeCost(rank);
                var b = UIKit.MakeButton(row.transform, max ? "MAX" : $"+  ({cost} XP)", () =>
                {
                    if (gm.profile.TryUpgrade(id, stat)) ShowUpgrades(id);
                }, UIKit.Accent * 0.8f, 26);
                b.interactable = !max && gm.profile.CanUpgrade(id, stat);
                b.GetComponent<RectTransform>().Place(new Vector2(1f, 0.5f), new Vector2(300, 66), new Vector2(-20, 0));
            }

            var back = UIKit.MakeButton(w, "BACK", () => gm.BackFromUpgrades());
            back.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0f), new Vector2(300, 70), new Vector2(0, 30));
        }

        // ------------------------------------------------------------ online

        public void ShowOnline(string status, bool searching)
        {
            var s = NewScreen("online");
            var w = Window(s, new Vector2(1100, 700), "ONLINE  1 VS 1");
            var body = Body(w, 120, 140);
            UIKit.VLayout(body.gameObject, 20, 10);
            var warrior = WarriorDatabase.Get((WarriorId)gm.profile.selectedWarrior);
            UIKit.MakeLabel(body, $"Name: <b>{gm.profile.playerName}</b>\nWarrior: <color=#{UIKit.Hex(warrior.uiColor)}>{warrior.displayName}</color>   Power rating: {gm.profile.PowerRating(warrior.id)}", 32, TextAnchor.MiddleCenter);
            UIKit.MakeLabel(body, status, 28, TextAnchor.MiddleCenter, searching ? UIKit.Accent : UIKit.Muted);

            var bar = UIKit.Rect(w, "Buttons");
            bar.Place(new Vector2(0.5f, 0f), new Vector2(900, 76), new Vector2(0, 30));
            UIKit.HLayout(bar.gameObject, 20, 0);
            if (!searching) UIKit.MakeButton(bar, "FIND MATCH", () => gm.FindOnlineMatch(), UIKit.Accent * 0.8f);
            UIKit.MakeButton(bar, "CHANGE WARRIOR", () => gm.ChooseMode(PlayMode.Online));
            UIKit.MakeButton(bar, searching ? "CANCEL" : "BACK", () => gm.CancelOnline());
        }

        // ------------------------------------------------------------ result / pause

        public void ShowResult(MatchResult r, int xpGained, bool canNext, string extra)
        {
            var s = NewScreen("result");
            var w = Window(s, new Vector2(1000, 640), r.playerWon ? "VICTORY" : "DEFEAT");
            var body = Body(w, 120, 140);
            UIKit.VLayout(body.gameObject, 16, 10);
            UIKit.MakeLabel(body, $"Rounds  {r.playerRounds} - {r.enemyRounds}", 40, TextAnchor.MiddleCenter, UIKit.TextColor, FontStyle.Bold);
            UIKit.MakeLabel(body, $"+{xpGained} XP Power", 52, TextAnchor.MiddleCenter, UIKit.Accent, FontStyle.Bold);
            UIKit.MakeLabel(body, $"Total XP Power: {gm.profile.xp}\n{extra}", 28, TextAnchor.MiddleCenter, UIKit.Muted);

            var bar = UIKit.Rect(w, "Buttons");
            bar.Place(new Vector2(0.5f, 0f), new Vector2(920, 76), new Vector2(0, 30));
            UIKit.HLayout(bar.gameObject, 16, 0);
            if (canNext) UIKit.MakeButton(bar, "NEXT LEVEL", () => gm.NextLevel(), UIKit.Accent * 0.8f);
            UIKit.MakeButton(bar, r.playerWon ? "FIGHT AGAIN" : "RETRY", () => gm.Retry());
            UIKit.MakeButton(bar, "UPGRADES", () => { gm.ReturnToMenuScene(); gm.OpenUpgrades(gm.mode == PlayMode.Campaign); });
            UIKit.MakeButton(bar, "MENU", () => gm.BackToMenu());
        }

        public void ShowPause(bool show, bool online = false)
        {
            if (!show) { if (_screens.TryGetValue("pause", out var p)) p.gameObject.SetActive(false); return; }
            var s = NewScreen("pause");
            var w = Window(s, new Vector2(700, 520), online ? "MENU" : "PAUSED");
            var body = Body(w, 130, 40);
            UIKit.VLayout(body.gameObject, 18, 10);
            UIKit.Size(UIKit.MakeButton(body, "RESUME", () => gm.SetPaused(false), UIKit.Accent * 0.8f), 76);
            if (!online) UIKit.Size(UIKit.MakeButton(body, "RESTART FIGHT", () => gm.Retry()), 76);
            UIKit.Size(UIKit.MakeButton(body, online ? "LEAVE MATCH (counts as a loss)" : "QUIT TO MENU", () => gm.BackToMenu()), 76);
        }

        // ------------------------------------------------------------ HUD

        void BuildHud()
        {
            _left = BuildSide(false);
            _right = BuildSide(true);

            _timer = UIKit.MakeLabel(_hudRoot, "75", 72, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            _timer.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(200, 100), new Vector2(0, -20));
            _timer.gameObject.AddComponent<Outline>().effectDistance = new Vector2(3, -3);

            _hint = UIKit.MakeLabel(_hudRoot,
                "WASD  move     CLICK + A/D/W/S  attacks     Q + A/D/W/S  kicks     HOLD RIGHT  block     DOUBLE RIGHT  dodge     LEFT + RIGHT  special     SPACE  jump",
                22, TextAnchor.MiddleCenter, new Color(1, 1, 1, 0.65f));
            _hint.rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(1900, 40), new Vector2(0, 12));

            // move list (bottom-left)
            var panel = UIKit.MakeImage(_hudRoot, "MoveList", new Color(0, 0, 0, 0.45f)).rectTransform;
            panel.Place(new Vector2(0f, 0f), new Vector2(430, 390), new Vector2(20, 60));
            panel.GetComponent<Image>().raycastTarget = false;
            UIKit.VLayout(panel.gameObject, 2, 12, TextAnchor.UpperLeft);
            _moveRows = new Text[10];
            _moveData = new AttackData[10];
            for (int i = 0; i < _moveRows.Length; i++)
            {
                bool header = i == 0 || i == 5;
                _moveRows[i] = UIKit.MakeLabel(panel, "", header ? 22 : 21, TextAnchor.MiddleLeft, header ? UIKit.Accent : UIKit.TextColor, header ? FontStyle.Bold : FontStyle.Normal);
                UIKit.Size(_moveRows[i], header ? 36 : 31);
            }
        }

        static readonly string[] DirKeys = { "A", "D", "W", "S" };
        static readonly string[] DirNames = { "left", "right", "front", "back" };

        void UpdateMoveList(Fighter p)
        {
            if (_moveRows == null || p == null || p.def == null) return;
            if (_moveDefId != (int)p.def.id)
            {
                _moveDefId = (int)p.def.id;
                _moveRows[0].text = "LEFT CLICK  -  " + p.def.handSetName.ToUpper();
                _moveRows[5].text = "Q  -  " + p.def.kickSetName.ToUpper();
                for (int d = 0; d < 4; d++)
                {
                    _moveData[1 + d] = p.def.hand[d];
                    _moveData[6 + d] = p.def.kick[d];
                    _moveRows[1 + d].text = MoveLine(DirKeys[d], DirNames[d], p.def.hand[d]);
                    _moveRows[6 + d].text = MoveLine(DirKeys[d], DirNames[d], p.def.kick[d]);
                }
            }
            var cur = p.IsAttacking ? p.CurrentAttack : null;
            for (int i = 0; i < _moveRows.Length; i++)
            {
                if (i == 0 || i == 5) continue;
                bool on = cur != null && _moveData[i] == cur;
                _moveRows[i].color = on ? UIKit.Accent : UIKit.TextColor;
                _moveRows[i].fontStyle = on ? FontStyle.Bold : FontStyle.Normal;
            }
        }

        static string MoveLine(string key, string dirName, AttackData a)
        {
            if (a == null) return "";
            string tag = a.hitType == HitType.Light ? "" : a.hitType == HitType.Heavy ? "  <size=16><color=#ffb060>GUARD BREAK</color></size>"
                       : "  <size=16><color=#ff7070>KNOCKDOWN</color></size>";
            return $"<b>{key}</b> <size=16><color=#9aa0ad>{dirName}</color></size>   {a.displayName}{tag}";
        }

        SideHud BuildSide(bool right)
        {
            var h = new SideHud();
            float ax = right ? 1f : 0f;
            float sx = right ? -1f : 1f;
            var root = UIKit.Rect(_hudRoot, right ? "RightHud" : "LeftHud");
            root.Place(new Vector2(ax, 1f), new Vector2(760, 200), new Vector2(40 * sx, -24));

            h.name = UIKit.MakeLabel(root, "Name", 34, right ? TextAnchor.UpperRight : TextAnchor.UpperLeft, Color.white, FontStyle.Bold);
            h.name.rectTransform.Place(new Vector2(ax, 1f), new Vector2(760, 44), Vector2.zero);
            h.name.gameObject.AddComponent<Outline>();

            h.hpTrail = UIKit.MakeBar(root, "HPTrail", new Color(0, 0, 0, 0.7f), new Color(1f, 1f, 1f, 0.85f), out var hpBack, right);
            hpBack.Place(new Vector2(ax, 1f), new Vector2(760, 40), new Vector2(0, -48));
            var hpFill = UIKit.MakeImage(hpBack, "HP", new Color(0.85f, 0.12f, 0.12f));
            h.hp = hpFill.rectTransform;
            h.hp.offsetMin = new Vector2(3, 3); h.hp.offsetMax = new Vector2(-3, -3);
            h.hp.pivot = new Vector2(right ? 1f : 0f, 0.5f);

            h.stamina = UIKit.MakeBar(root, "Stamina", new Color(0, 0, 0, 0.6f), new Color(0.95f, 0.8f, 0.2f), out var stBack, right);
            stBack.Place(new Vector2(ax, 1f), new Vector2(560, 16), new Vector2(0, -94));

            h.special = UIKit.MakeBar(root, "Special", new Color(0, 0, 0, 0.6f), new Color(0.25f, 0.6f, 1f), out var spBack, right);
            spBack.Place(new Vector2(ax, 1f), new Vector2(560, 16), new Vector2(0, -116));

            h.specialText = UIKit.MakeLabel(root, "", 22, right ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft, new Color(0.5f, 0.8f, 1f), FontStyle.Bold);
            h.specialText.rectTransform.Place(new Vector2(ax, 1f), new Vector2(760, 30), new Vector2(0, -136));

            h.info = UIKit.MakeLabel(root, "", 22, right ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft, UIKit.Muted);
            h.info.rectTransform.Place(new Vector2(ax, 1f), new Vector2(760, 30), new Vector2(0, -166));

            var pipRow = UIKit.Rect(root, "Pips");
            pipRow.Place(new Vector2(right ? 0f : 1f, 1f), new Vector2(120, 24), new Vector2(0, -94));
            UIKit.HLayout(pipRow.gameObject, 8, 0, right ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight);
            h.pips = new Image[3];
            for (int i = 0; i < 3; i++) h.pips[i] = UIKit.MakeImage(pipRow, "pip", new Color(0, 0, 0, 0.6f));
            return h;
        }

        public void ShowHud(bool show)
        {
            _hudRoot.gameObject.SetActive(show);
            if (!show && _center != null) _center.text = "";
        }

        public void UpdateHud(Fighter p, Fighter e, float timeLeft, int pr, int er, int need)
        {
            UpdateSide(_left, p, pr, need, false);
            UpdateMoveList(p);
            UpdateSide(_right, e, er, need, true);
            _timer.text = Mathf.CeilToInt(timeLeft).ToString();
        }

        void UpdateSide(SideHud h, Fighter f, int rounds, int need, bool right)
        {
            h.name.text = right ? $"<size=24><color=#{UIKit.Hex(f.def.uiColor)}>{f.def.displayName}</color></size>  {f.fighterName}"
                                : $"{f.fighterName}  <size=24><color=#{UIKit.Hex(f.def.uiColor)}>{f.def.displayName}</color></size>";
            float hp = f.Health01;
            UIKit.SetBar(h.hp, hp, right);
            h.trail = hp > h.trail ? hp : Mathf.MoveTowards(h.trail, hp, Time.unscaledDeltaTime * 0.35f);
            UIKit.SetBar(h.hpTrail, h.trail, right);
            UIKit.SetBar(h.stamina, f.Stamina01, right);
            UIKit.SetBar(h.special, f.Special01, right);
            h.specialText.text = f.Special01 >= 1f ? (f.isPlayer ? "SPECIAL READY  [LEFT + RIGHT CLICK]" : "SPECIAL READY") : "";

            string info = "";
            if (f.IsRaging) info += "RAGE  ";
            foreach (var t in f.traits) if (t != EnemyTrait.None) info += t.ToString().ToUpper() + "  ";
            h.info.text = info;

            for (int i = 0; i < h.pips.Length; i++)
            {
                h.pips[i].gameObject.SetActive(i < need);
                h.pips[i].color = i < rounds ? UIKit.Accent : new Color(0, 0, 0, 0.6f);
            }
        }

        public void CenterMessage(string text, float duration)
        {
            _center.text = text;
            _center.color = Color.white;
            _centerUntil = Time.unscaledTime + duration;
        }

        public void Popup(Fighter who, string text)
        {
            Vector3 world = who.transform.position + Vector3.up * 2.3f * who.stats.scale;
            SpawnFloating(world, text, who.isPlayer ? new Color(0.5f, 0.8f, 1f) : new Color(1f, 0.85f, 0.3f), 44);
        }

        public void DamageNumber(Vector3 world, float amount, bool victimIsPlayer, bool blocked)
        {
            var c = blocked ? new Color(0.75f, 0.75f, 0.8f) : victimIsPlayer ? new Color(1f, 0.3f, 0.3f) : Color.white;
            SpawnFloating(world + Random.insideUnitSphere * 0.25f, Mathf.RoundToInt(amount).ToString(), c, blocked ? 30 : 40);
        }

        void SpawnFloating(Vector3 world, string text, Color c, int size)
        {
            Text t = null;
            for (int i = 0; i < _floating.Count; i++)
                if (!_floating[i].gameObject.activeSelf) { t = _floating[i]; _floatingLife[i] = 0f; _floatingWorld[i] = world; break; }
            if (t == null)
            {
                t = UIKit.MakeLabel(_fxRoot, "", size, TextAnchor.MiddleCenter, c, FontStyle.Bold);
                t.rectTransform.sizeDelta = new Vector2(500, 80);
                t.gameObject.AddComponent<Outline>().effectDistance = new Vector2(2, -2);
                _floating.Add(t); _floatingLife.Add(0f); _floatingWorld.Add(world);
            }
            t.gameObject.SetActive(true);
            t.text = text;
            t.fontSize = size;
            t.color = c;
        }

        void Update()
        {
            if (_center != null && _center.text.Length > 0)
            {
                float left = _centerUntil - Time.unscaledTime;
                if (left <= 0f) _center.text = "";
                else if (left < 0.3f) { var c = _center.color; c.a = left / 0.3f; _center.color = c; }
            }

            var cam = Camera.main;
            for (int i = 0; i < _floating.Count; i++)
            {
                var t = _floating[i];
                if (!t.gameObject.activeSelf) continue;
                _floatingLife[i] += Time.unscaledDeltaTime;
                float k = _floatingLife[i] / 0.9f;
                if (k >= 1f || cam == null) { t.gameObject.SetActive(false); continue; }
                Vector3 w = _floatingWorld[i] + Vector3.up * k * 0.8f;
                Vector3 sp = cam.WorldToScreenPoint(w);
                if (sp.z < 0f) { t.gameObject.SetActive(false); continue; }
                RectTransformUtility.ScreenPointToLocalPointInRectangle(_fxRoot, sp, null, out var local);
                t.rectTransform.anchorMin = t.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
                t.rectTransform.anchoredPosition = local;
                var c = t.color; c.a = 1f - k * k; t.color = c;
            }
        }
    }
}
