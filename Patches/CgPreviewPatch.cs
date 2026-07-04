using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Config;
using HarmonyLib;
using Sdk;
using UnityEngine;
using UnityEngine.UI;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    /// 修复编辑器"不显示图片预览"：在 CGCfg / BgCfg / ItemCfg 等编辑界面右侧面板(group_right)
    /// 底部加一个图片预览。图片以原始分辨率为基准，等比缩放到刚好完整显示，底部对齐。
    ///
    /// 注：cfgType 在 OnOpen 才赋值（晚于 InitUI），所以预览在 Select 的 Postfix 里惰性创建。
    ///
    /// 热更新：上传新图 / 点保存后，预览立即换新（见 ImagePreviewUploadRefreshPatch /
    /// ImagePreviewSaveRefreshPatch）。因为游戏对已加载的图片按路径做了内存缓存
    /// （UISprite 同 URL 短路 + ResMgr.resDict），覆盖同名文件后普通加载永远拿到旧图，
    /// 所以刷新时走 SetExternTextureUrl(..., _isReload: true) 强制从磁盘重读。
    [HarmonyPatch(typeof(ModNormalEditView), "Select")]
    internal static class ImagePreviewPatch
    {
        private static readonly ConditionalWeakTable<ModNormalEditView, Holder> _previews =
            new ConditionalWeakTable<ModNormalEditView, Holder>();

        private const float PreviewRootHeight = 440f;
        private const float PreviewTitleHeight = 30f;
        private const int PreviewTitleFontSize = 20;
        private const float PreviewBottomOffset = PreviewTitleFontSize * 0.5f;
        private const float PreviewFrameHeight = PreviewRootHeight - PreviewTitleHeight - PreviewBottomOffset;

        private class Holder { public GameObject root; public UISprite sprite; public PreviewFit fit; }

        // ── 支持的配置类型 → 图片 URL 提取 ──────────────────────────

        /// 各配置类型的预览标题与图片 URL 提取逻辑。
        private static readonly Dictionary<Type, (string title, Func<object, string> getUrl)> _cfgHandlers =
            new Dictionary<Type, (string, Func<object, string>)>
            {
                [typeof(CGCfg)] = ("CG 预览", o =>
                {
                    var cg = (CGCfg)o;
                    return (cg.urls != null && cg.urls.Count > 0) ? cg.urls[0] : null;
                }),
                [typeof(BgCfg)] = ("背景预览", o =>
                {
                    var bg = (BgCfg)o;
                    return !string.IsNullOrEmpty(bg.url) ? bg.url : null;
                }),
                [typeof(ItemCfg)] = ("物品预览", o =>
                {
                    var item = (ItemCfg)o;
                    return !string.IsNullOrEmpty(item.icon) ? item.icon : null;
                }),
                [typeof(BookCfg)] = ("书籍预览", o =>
                {
                    var book = (BookCfg)o;
                    return !string.IsNullOrEmpty(book.icon) ? book.icon : null;
                }),
                [typeof(RenshengguanMemoryCfg)] = ("记忆预览", o =>
                {
                    var mem = (RenshengguanMemoryCfg)o;
                    return (mem.url != null && mem.url.Count > 0) ? mem.url[0] : null;
                }),
                [typeof(ActionCfg)] = ("行动预览", o =>
                {
                    var act = (ActionCfg)o;
                    return !string.IsNullOrEmpty(act.icon) ? act.icon : null;
                }),
                [typeof(PersonStateCfg)] = ("状态预览", o =>
                {
                    var state = (PersonStateCfg)o;
                    return !string.IsNullOrEmpty(state.icon) ? state.icon : null;
                }),
                [typeof(KZoneContentCfg)] = ("动态预览", o =>
                {
                    var content = (KZoneContentCfg)o;
                    return (content.imgs != null && content.imgs.Count > 0) ? content.imgs[0] : null;
                }),
                [typeof(KZoneAvatarCfg)] = ("头像预览", o =>
                {
                    var avatar = (KZoneAvatarCfg)o;
                    return !string.IsNullOrEmpty(avatar.icon) ? avatar.icon : null;
                }),
            };

        private static void Postfix(ModNormalEditView __instance)
        {
            Refresh(__instance, _forceReload: false);
        }

        /// <summary>
        /// 刷新当前选中条目的图片预览。
        /// _forceReload=true 时跳过游戏的纹理缓存、直接从磁盘重读，
        /// 用于图片文件内容可能已变化的场景（上传新图、点保存）。
        /// </summary>
        internal static void Refresh(ModNormalEditView __instance, bool _forceReload)
        {
            try
            {
                var cfgType = EditViewAccess.CfgType(__instance);
                if (cfgType == null || !_cfgHandlers.TryGetValue(cfgType, out var handler))
                    return;

                var holder = GetOrCreate(__instance, handler.title);
                if (holder == null) return;

                object cur = EditViewAccess.CurSelect(__instance);
                string url = cur != null ? handler.getUrl(cur) : null;

                if (!string.IsNullOrEmpty(url))
                {
                    holder.root.SetActive(true);
                    ApplyUrl(holder, url, _forceReload);
                    // 适配由 PreviewFit 在图片异步加载完成后自动处理
                }
                else
                {
                    holder.sprite.SetTextureUrl(null);
                    holder.root.SetActive(false);
                }
            }
            catch (Exception e) { Plugin.Log.LogError($"[ImagePreview] {e}"); }
        }

        /// 给预览设置图片。普通模式直接走游戏的 SetTextureUrl；
        /// 强制重载模式把 Mods 相对路径解析成磁盘绝对路径后走 _isReload 通道，
        /// 绕过 UISprite 的同 URL 短路和 ResMgr 的 Sprite 缓存。
        private static void ApplyUrl(Holder holder, string url, bool forceReload)
        {
            if (forceReload)
            {
                string full = url;
                if (!Path.IsPathRooted(full) && full.StartsWith("Mods"))
                    full = Singleton<ModCtrl>.Ins.GetFullUrl(full);

                if (Path.IsPathRooted(full))
                {
                    if (File.Exists(full))
                    {
                        holder.sprite.showWhenComp = true;
                        holder.sprite.SetExternTextureUrl(full, _isReload: true);
                    }
                    else
                    {
                        // 磁盘上已找不到这张图（被删/改名）：清空预览，避免一直挂着旧图
                        holder.sprite.SetTextureUrl(null);
                        holder.root.SetActive(false);
                    }
                    return;
                }
                // 非 Mods / 非绝对路径 = 游戏内置资源，运行期不会变化，走普通加载即可
            }
            holder.sprite.SetTextureUrl(url);
        }

        private static Holder GetOrCreate(ModNormalEditView view, string title)
        {
            if (_previews.TryGetValue(view, out var h) && h.root != null)
            {
                // 标题可能因 cfg 类型不同而变化，更新一下
                var titleText = h.root.transform.Find("Title")?.GetComponent<Text>();
                if (titleText != null) titleText.text = title;
                return h;
            }

            var groupRight = Traverse.Create(view).Field("group_right").GetValue<RectTransform>();
            if (groupRight == null) return null;

            var font = FindUiFont();

            // 容器：右侧面板底部对齐
            var root = new GameObject("ImagePreview", typeof(RectTransform));
            var rt = root.GetComponent<RectTransform>();
            rt.SetParent(groupRight, false);
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.sizeDelta = new Vector2(-30f, PreviewRootHeight);
            rt.anchoredPosition = Vector2.zero;

            // 预览框：遮罩裁剪；底透明
            // 容器高 440，标题高 30；frame 底部上移半个标题字号，标题由 PreviewFit 贴住缩放后图片顶部
            var frameGo = new GameObject("Frame", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            var frt = frameGo.GetComponent<RectTransform>();
            frt.SetParent(rt, false);
            Anchor(frt, posY: PreviewBottomOffset, height: PreviewFrameHeight);
            var frameImg = frameGo.GetComponent<Image>();
            frameImg.color = new Color(0f, 0f, 0f, 0f);
            frameImg.raycastTarget = false;

            // 图片：pivot=(0,0) 对齐游戏 sprite pivot
            var imgGo = new GameObject("Img", typeof(RectTransform));
            var irt = imgGo.GetComponent<RectTransform>();
            irt.SetParent(frt, false);
            irt.anchorMin = new Vector2(0.5f, 0.5f);
            irt.anchorMax = new Vector2(0.5f, 0.5f);
            irt.pivot = new Vector2(0f, 0f);
            irt.anchoredPosition = Vector2.zero;
            irt.sizeDelta = new Vector2(100f, 100f);
            var sprite = new UISprite(imgGo);
            sprite.image.preserveAspect = false;
            sprite.image.raycastTarget = false;

            var fit = frameGo.AddComponent<PreviewFit>();
            fit.img = irt;
            fit.image = sprite.image;
            fit.frame = frt;
            fit.bottomOffset = PreviewBottomOffset;
            fit.titleHeight = PreviewTitleHeight;
            fit.rootHeight = PreviewRootHeight;

            // 标题在 Frame 之后创建，确保渲染在图片之上
            var titleObj = MakeText(rt, "Title", title, font, PreviewTitleFontSize, TextAnchor.MiddleCenter,
                new Color(0.45f, 0.45f, 0.45f, 1f));
            // 初始放到可用区域顶端；图片加载后 PreviewFit 会把标题底边贴到缩放后图片顶边
            Anchor(titleObj.rectTransform, posY: PreviewRootHeight - PreviewTitleHeight, height: PreviewTitleHeight);
            fit.title = titleObj.rectTransform;

            root.SetActive(false);
            var holder = new Holder { root = root, sprite = sprite, fit = fit };
            _previews.Remove(view);
            _previews.Add(view, holder);
            return holder;
        }

        private static void Anchor(RectTransform t, float posY, float height)
        {
            t.anchorMin = new Vector2(0f, 0f);
            t.anchorMax = new Vector2(1f, 0f);
            t.pivot = new Vector2(0.5f, 0f);
            t.sizeDelta = new Vector2(0f, height);
            t.anchoredPosition = new Vector2(0f, posY);
        }

        private static Text MakeText(Transform parent, string name, string content, Font font,
            int size, TextAnchor align, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.text = content;
            t.font = font;
            t.fontSize = size;
            t.alignment = align;
            t.color = color;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.raycastTarget = false;
            return t;
        }

        private static Font FindUiFont()
        {
            var t = UnityEngine.Object.FindObjectOfType<Text>();
            if (t != null && t.font != null) return t.font;
            return Resources.GetBuiltinResource<Font>("Arial.ttf");
        }
    }

    /// 热更新（其一）：点「保存」后立即刷新预览。
    /// 原逻辑保存只把配置写进 JSON，不重新渲染右侧面板，
    /// 导致换图后预览一直是旧图、必须退出编辑器重进才更新。
    [HarmonyPatch(typeof(ModNormalEditView), "OnClickSave")]
    internal static class ImagePreviewSaveRefreshPatch
    {
        private static void Postfix(ModNormalEditView __instance)
        {
            ImagePreviewPatch.Refresh(__instance, _forceReload: true);
        }
    }

    /// 热更新（其二）：点「上传」选完图片后立即刷新预览（不用等保存）。
    /// 上传回调只把新路径写进配置和输入框，不触发 Select，预览不会自己更新；
    /// 且同名覆盖时路径不变，游戏纹理缓存会命中旧图，因此这里也走强制重读。
    [HarmonyPatch(typeof(AtlasMgr), nameof(AtlasMgr.SelectImage))]
    internal static class ImagePreviewUploadRefreshPatch
    {
        private static void Prefix(ref Action<string> _callback)
        {
            var origin = _callback;
            _callback = _url =>
            {
                origin?.Invoke(_url);
                try
                {
                    if (string.IsNullOrEmpty(_url)) return;
                    // SelectImage 也被封面上传等其他界面使用；
                    // ModNormalEditView 继承 BaseView（不是 MonoBehaviour），
                    // 不能用 FindObjectOfType，改用 UIMgr 查询：界面开着才返回实例。
                    var view = UIMgr.GetOpeningView<ModNormalEditView>();
                    if (view != null)
                        ImagePreviewPatch.Refresh(view, _forceReload: true);
                }
                catch (Exception e) { Plugin.Log.LogError($"[ImagePreview] 上传后刷新预览失败: {e}"); }
            };
        }
    }


    /// 预览图自动适配：图片加载后等比缩放到刚好完整显示，底部对齐。
    internal class PreviewFit : MonoBehaviour
    {
        public RectTransform img;
        public Image image;
        public RectTransform frame;
        public RectTransform title;
        public float bottomOffset = 10f;
        public float titleHeight = 30f;
        public float rootHeight = 440f;

        private Sprite _lastSprite;
        private Vector2 _lastFrameSize;

        private void Update()
        {
            var cur = image != null ? image.sprite : null;
            if (cur == null) { _lastSprite = null; return; }
            if (frame == null || frame.rect.width <= 1f || frame.rect.height <= 1f) return;
            var frameSize = frame.rect.size;
            if (cur == _lastSprite && frameSize == _lastFrameSize) return;
            _lastSprite = cur;
            _lastFrameSize = frameSize;
            SetupForSprite(cur);
        }

        private void SetupForSprite(Sprite s)
        {
            float nw = Mathf.Max(1f, s.rect.width);
            float nh = Mathf.Max(1f, s.rect.height);
            img.sizeDelta = new Vector2(nw, nh);
            float fw = frame.rect.width, fh = frame.rect.height;
            float scale = Mathf.Min(fw / nw, fh / nh);
            img.localScale = new Vector3(scale, scale, 1f);
            // X 居中，Y 贴 frame 底；frame 自身已上移半个标题字号，避免抵住下方栏
            float dw = nw * scale;
            float dh = nh * scale;
            img.anchoredPosition = new Vector2(-dw / 2f, -fh / 2f);

            // 标题底边贴住“缩放后的图片顶边”，不要固定在容器顶端留下大空隙。
            if (title != null)
            {
                float titleY = bottomOffset + dh;
                float maxY = Mathf.Max(bottomOffset, rootHeight - titleHeight);
                titleY = Mathf.Clamp(titleY, bottomOffset, maxY);
                title.anchoredPosition = new Vector2(0f, titleY);
            }
        }
    }
}
