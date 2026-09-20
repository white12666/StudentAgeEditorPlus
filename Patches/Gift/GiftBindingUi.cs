using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    internal enum GiftButtonKind { Secondary, Primary, Danger }

    // 从原生送礼表单捕获字体、圆角面板九宫格和按钮模板，让绑定窗口复用游戏自身外观。
    // 捕获失败时各项保持 null，调用方回退到平面色块，功能不受影响。
    internal sealed class GiftBindingStyle
    {
        internal Font Font;
        internal Sprite PanelSprite;
        internal Color PanelColor = Color.white;
        internal Sprite InputSprite;
        internal Color InputColor = Color.white;
        internal GameObject Primary;    // 原生「保存」金色按钮
        internal GameObject Secondary;  // 原生「查阅」浅黄按钮
        internal GameObject Danger;     // 原生「删除此项」红色按钮

        internal static GiftBindingStyle Capture(ModNormalEditView host)
        {
            var style = new GiftBindingStyle();
            try
            {
                Text saveLabel = host.btn_save?.gameObject.GetComponentInChildren<Text>(true);
                style.Font = saveLabel != null && saveLabel.font != null
                    ? saveLabel.font : SearchBarUtil.FindUiFont();
                style.Primary = host.btn_save?.gameObject;
                style.Danger = host.btn_delete?.gameObject;
                var cell = host.Cell_ModNormalPropertyItem;
                if (cell != null)
                {
                    style.Secondary = cell.btn_value?.gameObject;
                    Image inputImage = cell.input_value != null
                        ? cell.input_value.GetComponent<Image>() : null;
                    if (inputImage != null && inputImage.sprite != null)
                    {
                        style.InputSprite = inputImage.sprite;
                        style.InputColor = inputImage.color;
                    }
                }
                // 原生主面板：带九宫格边框、接近白色的最大 Image。
                Image best = null;
                float bestArea = 0f;
                foreach (Image img in host.gameObject.GetComponentsInChildren<Image>(true))
                {
                    if (img == null || img.sprite == null || img.sprite.border == Vector4.zero) continue;
                    if (img.color.grayscale < 0.75f) continue;
                    var rt = img.transform as RectTransform;
                    float area = rt == null ? 0f : rt.rect.width * rt.rect.height;
                    if (area > bestArea) { best = img; bestArea = area; }
                }
                if (best != null)
                {
                    style.PanelSprite = best.sprite;
                    style.PanelColor = best.color;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[GiftBinding] 原生样式捕获失败：" + e.Message); }
            return style;
        }
    }

    internal static class GiftBindingUi
    {
        internal static readonly Color Paper = new Color(0.98f, 0.97f, 0.93f);
        internal static readonly Color Ink = new Color(0.30f, 0.20f, 0.13f);
        internal static readonly Color Muted = new Color(0.47f, 0.38f, 0.29f);
        internal static readonly Color ErrorRed = new Color(0.70f, 0.20f, 0.13f);
        internal static readonly Color SelectedGold = new Color(0.98f, 0.90f, 0.66f);

        // 每次打开窗口时重新捕获；跨游戏会话也不残留引用。
        internal static GiftBindingStyle Style;

        internal static GameObject Panel(Transform parent, string name, Color color,
            bool nativeFrame = false)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            Image image = go.GetComponent<Image>();
            if (nativeFrame && Style?.PanelSprite != null)
            {
                // 原生九宫格中心半透明：根节点先铺不透明底色，边框作为子层叠上，避免背后 UI 透出。
                var backing = color;
                backing.a = 1f;
                image.color = backing;
                var frame = new GameObject("Frame", typeof(RectTransform), typeof(Image));
                frame.transform.SetParent(go.transform, false);
                Fill(frame.transform);
                Image frameImage = frame.GetComponent<Image>();
                frameImage.sprite = Style.PanelSprite;
                frameImage.type = Image.Type.Sliced;
                frameImage.color = Style.PanelColor;
                frameImage.raycastTarget = false;
            }
            else image.color = color;
            return go;
        }

        internal static void Place(Transform transform, float x, float y, float width, float height)
        {
            var rt = (RectTransform)transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(width, height);
        }

        internal static void Fill(Transform transform, float padding = 0)
        {
            var rt = (RectTransform)transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.one * padding;
            rt.offsetMax = Vector2.one * -padding;
        }

        internal static Text Label(Transform parent, string name, string value, int size,
            float x, float y, float width, float height, Color? color = null)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            Text text = go.GetComponent<Text>();
            text.font = Style?.Font != null ? Style.Font : SearchBarUtil.FindUiFont();
            text.fontSize = size;
            text.color = color ?? Ink;
            text.supportRichText = false;
            text.raycastTarget = false;
            text.alignment = TextAnchor.MiddleLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.text = value;
            Place(go.transform, x, y, width, height);
            return text;
        }

        internal static Button Button(Transform parent, string name, string title,
            float x, float y, float width, float height, Action click,
            GiftButtonKind kind = GiftButtonKind.Secondary)
        {
            GameObject template = kind == GiftButtonKind.Primary ? Style?.Primary
                : kind == GiftButtonKind.Danger ? Style?.Danger
                : Style?.Secondary;
            if (template != null)
            {
                // 克隆原生按钮：自带渐变皮肤、圆角和悬停反馈；清掉本地化组件防文案被顶回。
                GameObject go = UnityEngine.Object.Instantiate(template, parent, false);
                go.name = name;
                MiniGameUtil.StripBadComponents(go);
                var button = go.GetComponent<Button>() ?? go.AddComponent<Button>();
                button.onClick.RemoveAllListeners();
                button.navigation = new Navigation { mode = Navigation.Mode.None };
                if (click != null) button.onClick.AddListener(() => click());
                foreach (Text text in go.GetComponentsInChildren<Text>(true))
                {
                    text.text = title;
                    if (height < 46f)
                        text.fontSize = Mathf.Min(text.fontSize,
                            Mathf.Max(13, Mathf.RoundToInt(height * 0.44f)));
                }
                foreach (TMP_Text tmp in go.GetComponentsInChildren<TMP_Text>(true)) tmp.text = title;
                Place(go.transform, x, y, width, height);
                go.SetActive(true);
                return button;
            }
            // 兜底：模板缺失时用平面色块，功能仍可用。
            GameObject flat = Panel(parent, name,
                kind == GiftButtonKind.Primary ? new Color(0.39f, 0.47f, 0.32f)
                : kind == GiftButtonKind.Danger ? new Color(0.80f, 0.42f, 0.35f)
                : new Color(0.91f, 0.87f, 0.74f));
            Place(flat.transform, x, y, width, height);
            var fallback = flat.AddComponent<Button>();
            fallback.targetGraphic = flat.GetComponent<Image>();
            fallback.navigation = new Navigation { mode = Navigation.Mode.None };
            if (click != null) fallback.onClick.AddListener(() => click());
            Text label = Label(flat.transform, "Label", title, 17, 6, 0, width - 12, height,
                kind == GiftButtonKind.Secondary ? Ink : Color.white);
            label.alignment = TextAnchor.MiddleCenter;
            return fallback;
        }

        internal static InputField Search(Transform parent, string hint,
            float x, float y, float width, Action<string> changed)
        {
            var pair = SearchBarUtil.Create(parent, hint);
            pair.go.name = "GiftPickerSearch";
            Place(pair.go.transform, x, y, width, 38);
            Image bg = pair.go.GetComponent<Image>();
            if (Style?.InputSprite != null)
            {
                bg.sprite = Style.InputSprite;
                bg.type = Image.Type.Sliced;
                bg.color = Style.InputColor;
            }
            else bg.color = Color.white;
            Font font = Style?.Font;
            if (font != null)
            {
                pair.input.textComponent.font = font;
                if (pair.input.placeholder is Text placeholder) placeholder.font = font;
            }
            pair.input.textComponent.color = Ink;
            pair.input.textComponent.supportRichText = false;
            pair.input.caretColor = Ink;
            pair.input.onValueChanged.AddListener(value => changed(value));
            return pair.input;
        }

        internal static ScrollRect Scroll(Transform parent, string name,
            float x, float y, float width, float height, out RectTransform content)
        {
            GameObject go = Panel(parent, name, Color.white, nativeFrame: true);
            Place(go.transform, x, y, width, height);
            var scroll = go.AddComponent<ScrollRect>();
            var viewport = Panel(go.transform, "Viewport", Color.white);
            Fill(viewport.transform);
            viewport.AddComponent<Mask>().showMaskGraphic = false;
            var body = new GameObject("Content", typeof(RectTransform));
            body.transform.SetParent(viewport.transform, false);
            content = (RectTransform)body.transform;
            Place(content, 0, 0, width, 0);
            scroll.viewport = (RectTransform)viewport.transform;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 32;
            return scroll;
        }

        internal static void Clear(Transform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                GameObject child = parent.GetChild(i).gameObject;
                child.SetActive(false);
                UnityEngine.Object.Destroy(child);
            }
        }
    }
}
