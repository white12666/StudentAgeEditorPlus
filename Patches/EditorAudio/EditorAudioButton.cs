using DG.Tweening;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace StudentAgeEditorPlus.Patches
{
    internal sealed class EditorSpeakerGraphic : MaskableGraphic
    {
        internal float OpenAmount = 1f;

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            Color tint = color;
            Quad(mesh, new Vector2(-12, -4), new Vector2(-7, -4),
                new Vector2(-7, 4), new Vector2(-12, 4), tint);
            Quad(mesh, new Vector2(-7, -4), new Vector2(0, -10),
                new Vector2(0, 10), new Vector2(-7, 4), tint);
            Color wave = tint;
            wave.a *= OpenAmount;
            for (int arc = 0; arc < 2; arc++)
            {
                float radius = 7f + arc * 5f;
                for (int i = 0; i < 12; i++)
                {
                    float a = Mathf.Lerp(-0.9f, 0.9f, i / 12f);
                    float b = Mathf.Lerp(-0.9f, 0.9f, (i + 1) / 12f);
                    Line(mesh, new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius,
                        new Vector2(Mathf.Cos(b), Mathf.Sin(b)) * radius, 1.7f, wave);
                }
            }
            Color slash = tint;
            slash.a *= 1f - OpenAmount;
            Line(mesh, new Vector2(-13, -12), new Vector2(13, 12), 2.2f, slash);
        }

        private static void Line(VertexHelper mesh, Vector2 a, Vector2 b, float width, Color color)
        {
            Vector2 normal = new Vector2(-(b - a).y, (b - a).x).normalized * width * 0.5f;
            Quad(mesh, a - normal, b - normal, b + normal, a + normal, color);
        }

        private static void Quad(VertexHelper mesh, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color color)
        {
            int start = mesh.currentVertCount;
            mesh.AddVert(a, color, Vector2.zero);
            mesh.AddVert(b, color, Vector2.zero);
            mesh.AddVert(c, color, Vector2.zero);
            mesh.AddVert(d, color, Vector2.zero);
            mesh.AddTriangle(start, start + 1, start + 2);
            mesh.AddTriangle(start, start + 2, start + 3);
        }
    }

    internal sealed class EditorAudioButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        internal const float Width = 88f;
        internal const float Height = 42f;
        private EditorSpeakerGraphic icon;
        private Text label;
        private Button button;
        private RectTransform tooltipHost;
        private GameObject tooltip;
        private Tween transition;
        private Tween press;
        private bool previewIndicator;
        private bool compact;
        private bool hovered;
        private bool ready;
        private bool lastOpen;
        private bool lastPreview;
        private Canvas canvas;
        private float units;
        private static Font nativeFont;

        internal static EditorAudioButton Create(Transform parent, RectTransform host, Font font = null,
            bool indicator = false, bool compact = false)
        {
            var go = new GameObject("EditorPlus_BgmButton", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.sizeDelta = new Vector2(Width, Height);
            var image = go.GetComponent<Image>();
            image.color = new Color(0.98f, 0.94f, 0.84f, 1f);
            var control = go.AddComponent<EditorAudioButton>();
            control.canvas = parent.GetComponentInParent<Canvas>()?.rootCanvas;
            control.tooltipHost = host;
            control.previewIndicator = indicator;
            control.compact = compact;
            if (font != null) nativeFont = font;
            font = font != null ? font : nativeFont;
            if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            control.button = go.GetComponent<Button>();
            control.button.targetGraphic = image;
            control.button.onClick.AddListener(control.Click);
            control.button.interactable = !indicator;
            var iconGo = new GameObject("Speaker", typeof(RectTransform));
            iconGo.transform.SetParent(go.transform, false);
            var iconRect = (RectTransform)iconGo.transform;
            iconRect.anchorMin = iconRect.anchorMax = new Vector2(0f, 0.5f);
            iconRect.anchoredPosition = new Vector2(22f, 0f);
            iconRect.sizeDelta = new Vector2(30f, 30f);
            control.icon = iconGo.AddComponent<EditorSpeakerGraphic>();
            control.icon.raycastTarget = false;
            var textGo = new GameObject("Status", typeof(RectTransform), typeof(Text));
            textGo.transform.SetParent(go.transform, false);
            control.label = textGo.GetComponent<Text>();
            control.label.font = font;
            control.label.fontSize = 14;
            control.label.alignment = TextAnchor.MiddleCenter;
            control.label.raycastTarget = false;
            var textRect = (RectTransform)textGo.transform;
            textRect.anchorMin = new Vector2(0f, 0f);
            textRect.anchorMax = new Vector2(1f, 1f);
            textRect.offsetMin = new Vector2(40f, 0f);
            textRect.offsetMax = new Vector2(-3f, 0f);
            var layout = go.AddComponent<LayoutElement>();
            layout.preferredWidth = Width;
            layout.preferredHeight = Height;
            layout.minHeight = Height;
            control.ready = true;
            control.Resize();
            control.Render(false);
            return control;
        }

        private void LateUpdate()
        {
            if (!ready) return;
            float expected = canvas != null ? Mathf.Max(1f, 1f / Mathf.Max(0.1f, canvas.scaleFactor)) : 1f;
            if (Mathf.Abs(expected - units) > 0.01f) Resize();
        }

        private void Resize()
        {
            // 原生 Canvas 在 1080p 也可能是 0.75x；保证点击区至少 88x42 屏幕像素。
            units = canvas != null ? Mathf.Max(1f, 1f / Mathf.Max(0.1f, canvas.scaleFactor)) : 1f;
            float width = compact ? 72f : Width;
            ((RectTransform)transform).sizeDelta = new Vector2(width, Height) * units;
            var layout = GetComponent<LayoutElement>();
            layout.preferredWidth = layout.minWidth = width * units;
            layout.preferredHeight = layout.minHeight = Height * units;
            icon.rectTransform.anchoredPosition = new Vector2((compact ? 18f : 22f) * units, 0f);
            icon.transform.localScale = Vector3.one * units * (compact ? 0.85f : 1f);
            label.fontSize = Mathf.CeilToInt((compact ? 12f : 14f) * units);
            label.rectTransform.offsetMin = new Vector2((compact ? 34f : 40f) * units, 0f);
            label.rectTransform.offsetMax = new Vector2(-3f * units, 0f);
            if (hovered) ShowTooltip();
        }

        private void OnEnable()
        {
            EditorAudioRuntime.Changed += Refresh;
            if (ready) Render(false);
        }

        private void OnDisable()
        {
            EditorAudioRuntime.Changed -= Refresh;
            transition?.Kill();
            press?.Kill();
            transform.localScale = Vector3.one;
            HideTooltip();
            hovered = false;
        }

        private void Click()
        {
            if (previewIndicator) return;
            EditorAudioRuntime.Toggle();
            press?.Kill();
            transform.localScale = Vector3.one * 0.94f;
            press = transform.DOScale(1f, 0.16f).SetUpdate(true);
        }

        private void Refresh() { if (ready) Render(true); }

        private void Render(bool animate)
        {
            var state = EditorAudioRuntime.Policy;
            bool open = state.PreviewMusic || !state.Quiet;
            label.text = state.Label;
            Color tint = state.PreviewMusic ? new Color(0.14f, 0.44f, 0.39f)
                : open ? new Color(0.42f, 0.29f, 0.16f) : new Color(0.46f, 0.46f, 0.46f);
            label.color = tint;
            icon.color = tint;
            if (!animate || open != lastOpen || state.PreviewMusic != lastPreview)
            {
                transition?.Kill();
                float target = open ? 1f : 0f;
                if (animate)
                    transition = DOTween.To(() => icon.OpenAmount, value =>
                    {
                        icon.OpenAmount = value;
                        icon.SetVerticesDirty();
                    }, target, 0.2f).SetUpdate(true);
                else
                {
                    icon.OpenAmount = target;
                    icon.SetVerticesDirty();
                }
            }
            lastOpen = open;
            lastPreview = state.PreviewMusic;
            if (hovered) ShowTooltip();
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            hovered = true;
            ShowTooltip();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            hovered = false;
            HideTooltip();
        }

        private void ShowTooltip()
        {
            if (tooltipHost == null || label == null) return;
            if (tooltip == null)
            {
                tooltip = new GameObject("EditorPlus_BgmHint", typeof(RectTransform), typeof(Image));
                tooltip.transform.SetParent(tooltipHost, false);
                tooltip.GetComponent<Image>().color = new Color(0.18f, 0.15f, 0.12f, 0.96f);
                tooltip.GetComponent<Image>().raycastTarget = false;
                var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
                textGo.transform.SetParent(tooltip.transform, false);
                var text = textGo.GetComponent<Text>();
                text.font = label.font;
                text.fontSize = label.fontSize;
                text.color = Color.white;
                text.alignment = TextAnchor.MiddleCenter;
                text.raycastTarget = false;
                var tr = (RectTransform)textGo.transform;
                tr.anchorMin = Vector2.zero;
                tr.anchorMax = Vector2.one;
                tr.offsetMin = new Vector2(8, 4) * units;
                tr.offsetMax = new Vector2(-8, -4) * units;
            }
            tooltip.GetComponentInChildren<Text>().text = EditorAudioRuntime.Policy.Hint;
            var rt = (RectTransform)tooltip.transform;
            rt.anchorMin = rt.anchorMax = tooltipHost.pivot;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(300f, 52f) * units;
            Vector2 point = tooltipHost.InverseTransformPoint(transform.position);
            Rect bounds = tooltipHost.rect;
            float y = point.y + 54f * units;
            if (y + 26f * units > bounds.yMax) y = point.y - 54f * units;
            rt.anchoredPosition = new Vector2(Mathf.Clamp(point.x, bounds.xMin + 154f * units, bounds.xMax - 154f * units),
                Mathf.Clamp(y, bounds.yMin + 30f * units, bounds.yMax - 30f * units));
            tooltip.transform.SetAsLastSibling();
        }

        private void HideTooltip()
        {
            if (tooltip != null) Destroy(tooltip);
            tooltip = null;
        }
    }
}
