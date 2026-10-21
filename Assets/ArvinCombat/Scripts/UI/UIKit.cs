using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ArvinCombat
{
    /// <summary>Small helpers to build uGUI from code (no prefabs, no textures).</summary>
    public static class UIKit
    {
        public static readonly Color Bg = new Color(0.05f, 0.06f, 0.08f, 0.92f);
        public static readonly Color Panel = new Color(0.11f, 0.12f, 0.15f, 0.95f);
        public static readonly Color PanelLight = new Color(0.17f, 0.18f, 0.22f, 1f);
        public static readonly Color Accent = new Color(0.95f, 0.55f, 0.15f, 1f);
        public static readonly Color TextColor = new Color(0.93f, 0.93f, 0.95f, 1f);
        public static readonly Color Muted = new Color(0.62f, 0.64f, 0.7f, 1f);

        static Font _font;
        public static Font UIFont
        {
            get
            {
                if (_font == null) _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                return _font;
            }
        }

        public static Canvas CreateCanvas(string name, int order)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var c = go.GetComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = order;
            var s = go.GetComponent<CanvasScaler>();
            s.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            s.referenceResolution = new Vector2(1920, 1080);
            s.matchWidthOrHeight = 0.5f;
            return c;
        }

        public static void EnsureEventSystem()
        {
            if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() != null) return;
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }

        public static RectTransform Rect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        public static RectTransform Stretch(this RectTransform r, float l = 0, float t = 0, float rr = 0, float b = 0)
        {
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
            r.offsetMin = new Vector2(l, b); r.offsetMax = new Vector2(-rr, -t);
            return r;
        }

        /// <summary>Place by anchor point (0..1) with pixel size and offset.</summary>
        public static RectTransform Place(this RectTransform r, Vector2 anchor, Vector2 size, Vector2 offset)
        {
            r.anchorMin = r.anchorMax = anchor;
            r.pivot = anchor;
            r.sizeDelta = size;
            r.anchoredPosition = offset;
            return r;
        }

        public static Image MakeImage(Transform parent, string name, Color color)
        {
            var r = Rect(parent, name);
            var img = r.gameObject.AddComponent<Image>();
            img.color = color;
            return img;
        }

        public static Text MakeLabel(Transform parent, string text, int size, TextAnchor align = TextAnchor.MiddleLeft, Color? color = null, FontStyle style = FontStyle.Normal)
        {
            var r = Rect(parent, "Text");
            var t = r.gameObject.AddComponent<Text>();
            t.font = UIFont;
            t.text = text;
            t.fontSize = size;
            t.alignment = align;
            t.color = color ?? TextColor;
            t.fontStyle = style;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            return t;
        }

        public static Button MakeButton(Transform parent, string label, UnityAction onClick, Color? color = null, int fontSize = 30)
        {
            var img = MakeImage(parent, "Button_" + label, color ?? PanelLight);
            var b = img.gameObject.AddComponent<Button>();
            var colors = b.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.25f, 1.2f, 1.1f, 1f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            colors.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.6f);
            colors.colorMultiplier = 1.3f;
            b.colors = colors;
            if (onClick != null) b.onClick.AddListener(onClick);
            var t = MakeLabel(img.transform, label, fontSize, TextAnchor.MiddleCenter, null, FontStyle.Bold);
            t.rectTransform.Stretch(8, 4, 8, 4);
            return b;
        }

        public static void SetButtonText(Button b, string text)
        {
            var t = b.GetComponentInChildren<Text>();
            if (t != null) t.text = text;
        }

        public static InputField MakeInput(Transform parent, string placeholder, string value, int size = 30)
        {
            var bg = MakeImage(parent, "Input", new Color(0.02f, 0.02f, 0.03f, 1f));
            var field = bg.gameObject.AddComponent<InputField>();
            var txt = MakeLabel(bg.transform, "", size, TextAnchor.MiddleCenter);
            txt.rectTransform.Stretch(14, 4, 14, 4);
            txt.supportRichText = false;
            var ph = MakeLabel(bg.transform, placeholder, size, TextAnchor.MiddleCenter, Muted, FontStyle.Italic);
            ph.rectTransform.Stretch(14, 4, 14, 4);
            field.textComponent = txt;
            field.placeholder = ph;
            field.characterLimit = 16;
            field.text = value;
            return field;
        }

        /// <summary>A horizontal bar. Returns the fill RectTransform; set its anchorMax.x to change value.</summary>
        public static RectTransform MakeBar(Transform parent, string name, Color bg, Color fill, out RectTransform back, bool rightToLeft = false)
        {
            var b = MakeImage(parent, name, bg);
            back = b.rectTransform;
            var f = MakeImage(b.transform, "Fill", fill);
            var fr = f.rectTransform;
            fr.anchorMin = rightToLeft ? new Vector2(0f, 0f) : Vector2.zero;
            fr.anchorMax = Vector2.one;
            fr.pivot = new Vector2(rightToLeft ? 1f : 0f, 0.5f);
            fr.offsetMin = new Vector2(3, 3); fr.offsetMax = new Vector2(-3, -3);
            return fr;
        }

        public static void SetBar(RectTransform fill, float v, bool rightToLeft = false)
        {
            v = Mathf.Clamp01(v);
            if (rightToLeft) { fill.anchorMin = new Vector2(1f - v, 0f); fill.anchorMax = Vector2.one; }
            else { fill.anchorMin = Vector2.zero; fill.anchorMax = new Vector2(v, 1f); }
        }

        public static VerticalLayoutGroup VLayout(GameObject go, float spacing, int pad, TextAnchor align = TextAnchor.UpperCenter)
        {
            var l = go.AddComponent<VerticalLayoutGroup>();
            l.spacing = spacing;
            l.padding = new RectOffset(pad, pad, pad, pad);
            l.childAlignment = align;
            l.childControlWidth = true; l.childControlHeight = true;
            l.childForceExpandWidth = true; l.childForceExpandHeight = false;
            return l;
        }

        public static HorizontalLayoutGroup HLayout(GameObject go, float spacing, int pad, TextAnchor align = TextAnchor.MiddleCenter)
        {
            var l = go.AddComponent<HorizontalLayoutGroup>();
            l.spacing = spacing;
            l.padding = new RectOffset(pad, pad, pad, pad);
            l.childAlignment = align;
            l.childControlWidth = true; l.childControlHeight = true;
            l.childForceExpandWidth = true; l.childForceExpandHeight = true;
            return l;
        }

        public static LayoutElement Size(Component c, float prefH = -1, float prefW = -1, float flexW = -1, float flexH = -1)
        {
            var le = c.gameObject.GetComponent<LayoutElement>();
            if (le == null) le = c.gameObject.AddComponent<LayoutElement>();
            if (prefH >= 0) le.preferredHeight = prefH;
            if (prefW >= 0) le.preferredWidth = prefW;
            if (flexW >= 0) le.flexibleWidth = flexW;
            if (flexH >= 0) le.flexibleHeight = flexH;
            return le;
        }

        public static void Clear(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(t.GetChild(i).gameObject);
        }

        public static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);
    }
}
