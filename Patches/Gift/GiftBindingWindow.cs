using System;
using System.Collections.Generic;
using System.Linq;
using Config;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.UI;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    internal sealed partial class GiftBindingWindow : MonoBehaviour
    {
        private ModNormalEditView _host;
        private GiftEvtCfg _source;
        private GiftBindingDraft _draft;
        private GiftStoryCatalog _catalog;
        private string _modRoot;
        private string _sourceFingerprint;
        private RectTransform _panel;
        private RectTransform _rows;
        private ScrollRect _scroll;
        private Text _status;
        private float _width;
        private float _height;
        private GiftBindingRow _pendingRemoval;

        internal static void Open(ModNormalEditView host)
        {
            GiftFormInput.CommitFocused(host);
            if (!(EditViewAccess.CurSelect(host) is GiftEvtCfg source))
            {
                ToastHelper.Toast("请先新增或选中一条送礼事件。");
                return;
            }
            if (FindObjectsOfType<GiftBindingWindow>().Any(window => ReferenceEquals(window._host, host))) return;
            GameObject root = null;
            try
            {
                string modRoot = HarmonyLib.Traverse.Create(host).Field("modRoot").GetValue<string>();
                GiftStoryCatalog catalog = GiftCatalogProvenance.Load(modRoot);
                root = new GameObject("EditorPlus_GiftBindingWindow", typeof(RectTransform),
                    typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                // 独立屏幕 Canvas，不能继承原生相机 Canvas 的深度或缩放。
                Canvas canvas = root.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.overrideSorting = true;
                canvas.sortingOrder = 32000;
                CanvasScaler scaler = root.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1280, 720);
                scaler.matchWidthOrHeight = 0.5f;
                GiftBindingWindow window = root.AddComponent<GiftBindingWindow>();
                window._host = host;
                window._source = source;
                window._modRoot = modRoot;
                window._catalog = catalog;
                window._sourceFingerprint = Fingerprint(source);
                window._draft = GiftBindingDraft.Create(source.npc, source.talkId, source.type);
                GiftBindingUi.Style = GiftBindingStyle.Capture(host);
                Canvas.ForceUpdateCanvases();
                window.Build();
            }
            catch (Exception e)
            {
                if (root != null) Destroy(root);
                Plugin.Log.LogError("[GiftBinding] 无法打开剧情绑定：" + e);
                ToastHelper.Toast("无法读取当前作品的剧情配置，请检查 JSON。未修改送礼数据。");
            }
        }

        private static string Fingerprint(GiftEvtCfg source) =>
            JsonConvert.SerializeObject(new object[] { source.npc, source.talkId, source.type });

        private void Build()
        {
            var canvasRect = (RectTransform)transform;
            _width = Mathf.Min(1120, canvasRect.rect.width - 32);
            _height = Mathf.Min(660, canvasRect.rect.height - 24);
            GameObject shade = GiftBindingUi.Panel(transform, "Blocker", new Color(0.10f, 0.07f, 0.03f, 0.45f));
            GiftBindingUi.Fill(shade.transform);
            _panel = (RectTransform)GiftBindingUi.Panel(transform, "GiftBindingPanel",
                GiftBindingUi.Style?.PanelColor ?? GiftBindingUi.Paper, nativeFrame: true).transform;
            _panel.anchorMin = _panel.anchorMax = _panel.pivot = new Vector2(0.5f, 0.5f);
            _panel.sizeDelta = new Vector2(_width, _height);
            _panel.anchoredPosition = Vector2.zero;
            GiftBindingUi.Label(_panel, "Title", "绑定送礼剧情", 25, 22, 14, _width - 44, 38);
            GiftBindingUi.Label(_panel, "Help",
                "为每位 NPC 选择要播放的首句，无需手抄 ID。默认男女主共用，也可分别设置。\n" +
                "这里只绑定对话，不会执行所选事件本身的条件、效果或小游戏。",
                17, 22, 55, _width - 44, 50, GiftBindingUi.Muted);
            GiftBindingUi.Button(_panel, "AddNpc", "＋ 添加 NPC", 22, 113, 148, 38, OpenNpcPicker);
            GiftBindingUi.Label(_panel, "DraftHint", "草稿 · 应用后回到原表单，再点击「保存」写入 JSON",
                16, 184, 113, _width - 206, 38, GiftBindingUi.Muted);
            _scroll = GiftBindingUi.Scroll(_panel, "NpcBindings", 22, 163,
                _width - 44, _height - 263, out _rows);
            _status = GiftBindingUi.Label(_panel, "Status", "", 16, 22, _height - 94,
                _width - 44, 42, GiftBindingUi.Muted);
            GiftBindingUi.Button(_panel, "Cancel", "取消", _width - 254, _height - 47, 100, 34, Close);
            GiftBindingUi.Button(_panel, "Apply", "应用绑定", _width - 142, _height - 47, 120, 34, Apply,
                GiftButtonKind.Primary);
            RefreshRows();
            SetStatus(_draft.ShapeError ?? (_draft.Rows.Count == 0
                ? "当前没有绑定 NPC。可添加 NPC，或应用空绑定以停用此送礼规则。"
                : "已有绑定会原样载入；取消不会修改原数据。"), _draft.ShapeError != null);
        }

        private void RefreshRows()
        {
            float position = _scroll.verticalNormalizedPosition;
            GiftBindingUi.Clear(_rows);
            float width = _width - 44;
            float y = 0;
            for (int i = 0; i < _draft.Rows.Count; i++)
            {
                int index = i;
                GiftBindingRow row = _draft.Rows[i];
                float height = row.Separate ? 218 : 158;
                GameObject card = GiftBindingUi.Panel(_rows, "Npc_" + row.NpcId,
                    new Color(0.99f, 0.98f, 0.94f));
                GiftBindingUi.Place(card.transform, 0, y, width, height);
                GiftBindingUi.Label(card.transform, "NpcName",
                    (i + 1) + ". " + _catalog.NpcName(row.NpcId) + "  ·  " + row.NpcId,
                    20, 14, 8, width - 320, 34);
                GiftBindingUi.Button(card.transform, "MoveUp", "上移", width - 264, 10, 66, 30,
                    () => { _draft.Move(index, -1); RefreshRows(); }).interactable = i > 0;
                GiftBindingUi.Button(card.transform, "MoveDown", "下移", width - 190, 10, 66, 30,
                    () => { _draft.Move(index, 1); RefreshRows(); }).interactable = i + 1 < _draft.Rows.Count;
                GiftBindingUi.Button(card.transform, "Remove",
                    ReferenceEquals(_pendingRemoval, row) ? "确认移除" : "移除",
                    width - 116, 10, 102, 30, () =>
                    {
                        if (ReferenceEquals(_pendingRemoval, row))
                        {
                            _draft.Rows.Remove(row);
                            _pendingRemoval = null;
                        }
                        else _pendingRemoval = row;
                        RefreshRows();
                        if (_draft.Rows.Count == 0)
                            SetStatus("已移除全部 NPC。应用并保存后，此记录不触发送礼对白；取消则保留原绑定。");
                    }, GiftButtonKind.Danger);
                GiftBindingUi.Button(card.transform, "GenderMode",
                    row.Separate ? "男女主分别设置" : "男女主共用", 14, 50, 192, 34, () =>
                    {
                        if (row.TalkIds.Count > 2)
                        {
                            SetStatus("此 NPC 超过两个入口，请取消并在高级 ID 字段修正，避免丢失旧数据。", true);
                            return;
                        }
                        bool wasSeparate = row.Separate;
                        row.SetSeparate(!wasSeparate);
                        RefreshRows();
                        SetStatus(wasSeparate ? "已改为共用男主当前入口；应用前切回分别设置可恢复女主入口。" :
                            "分别选择男主、女主的首句；可在选择窗口将某一性别设为不触发。");
                    });
                GiftBindingUi.Button(card.transform, "ItemMode",
                    row.ItemMode == 0 ? "赠出后：物品消失" : row.ItemMode == 1 ? "赠出后：物品保留" : "无效类型：" + row.ItemMode,
                    220, 50, 224, 34, () =>
                    {
                        row.ItemMode = row.ItemMode == 0 ? 1 : 0;
                        RefreshRows();
                    });
                AddTalkRow(card.transform, row, 0, 94, width);
                if (row.Separate) AddTalkRow(card.transform, row, 1, 154, width);
                y += height + 10;
            }
            if (_draft.Rows.Count == 0)
            {
                GiftBindingUi.Label(_rows, "Empty", "当前没有绑定 NPC；应用并保存后不会触发。\n点击上方「＋ 添加 NPC」可重新绑定。",
                    20, 22, 20, width - 44, 64, GiftBindingUi.Muted);
                y = 108;
            }
            _rows.sizeDelta = new Vector2(width, y);
            Canvas.ForceUpdateCanvases();
            _scroll.verticalNormalizedPosition = position;
        }

        private void AddTalkRow(Transform parent, GiftBindingRow row, int slot, float y, float width)
        {
            int id = row.GetTalk(slot);
            string label = row.Separate ? (slot == 0 ? "男主剧情" : "女主剧情") : "共用剧情";
            GiftBindingUi.Label(parent, "SlotLabel_" + slot, label, 17, 14, y, 90, 46);
            string summary;
            Color color = GiftBindingUi.Ink;
            if (id == 0)
            {
                summary = row.Separate ? "此性别不触发 · 请选择剧情，或保留为不触发" : "尚未绑定 · 点击右侧选择剧情";
                color = GiftBindingUi.Muted;
            }
            else if (_catalog.Talks.TryGetValue(id, out GiftStoryChoice choice))
                summary = "[" + id + "] " + GiftStoryCatalog.Compact(choice.Title, 44) +
                    "\n" + GiftStoryCatalog.Compact(choice.Content, 58);
            else
            {
                summary = "[" + id + "] 首句不存在，请重新选择";
                color = GiftBindingUi.ErrorRed;
            }
            GiftBindingUi.Label(parent, "TalkSummary_" + slot, summary, 16, 108, y,
                width - 286, 52, color);
            GiftBindingUi.Button(parent, "ChooseTalk_" + slot,
                id > 0 ? "更换剧情…" : "选择剧情…", width - 166, y + 5, 152, 38,
                () => OpenStoryPicker(row, slot));
        }

        private void Apply()
        {
            try
            {
                if (!ReferenceEquals(EditViewAccess.CurSelect(_host), _source) ||
                    Fingerprint(_source) != _sourceFingerprint)
                {
                    SetStatus("原表单已发生变化，未覆盖数据。请取消并重新打开绑定窗口。", true);
                    return;
                }
                GiftStoryCatalog fresh = GiftCatalogProvenance.Load(_modRoot);
                string error = _draft.Validate(id => fresh.Talks.ContainsKey(id));
                if (error != null) { SetStatus(error, true); return; }
                _draft.Export(out var npcs, out var talks, out var types);
                _source.npc = npcs;
                _source.talkId = talks;
                _source.type = types;
                _host.itemgroup_property.Refresh();
                _host.itemgroup_item.Refresh();
                _host.txtex_desc.text = "已应用送礼剧情绑定。请点击右下角「保存」写入 GiftEvtCfg.json。";
                ToastHelper.Toast(npcs.Count == 0 ? "已应用空绑定，请点击「保存」停用此送礼规则。" : "已应用绑定，请点击「保存」。");
                Close();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[GiftBinding] 应用失败：" + e);
                SetStatus("读取当前作品失败，未应用绑定。请检查 JSON 后重试。", true);
            }
        }

        private void SetStatus(string message, bool error = false)
        {
            _status.text = message;
            _status.color = error ? GiftBindingUi.ErrorRed : GiftBindingUi.Muted;
        }

        private void Close()
        {
            gameObject.SetActive(false);
            Destroy(gameObject);
        }

        private void OnDisable()
        {
            // 宿主页被关闭/回收时，不保留可写入旧作品的悬空窗口。
            Destroy(gameObject);
        }

        private void Update()
        {
            if (_host == null || _host.gameObject == null || !_host.gameObject.activeInHierarchy)
                Close();
        }
    }
}
