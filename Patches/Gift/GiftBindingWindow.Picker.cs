using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace StudentAgeEditorPlus.Patches
{
    internal sealed partial class GiftBindingWindow
    {
        private GameObject _picker;
        private RectTransform _pickerRows;
        private Text _pickerPageText;
        private Text _pickerPreview;
        private Button _pickerPrevious;
        private Button _pickerNext;
        private Button _pickerConfirm;
        private InputField _pickerSearch;
        private int _pickerPage;
        private int _pickerPageSize;
        private bool _pickingNpc;
        private bool _localOnly = true;
        private bool _entriesOnly = true;
        private GiftBindingRow _pickerTarget;
        private int _pickerSlot;
        private int? _pickerSelection;
        private List<GiftStoryChoice> _pickerCandidates = new List<GiftStoryChoice>();
        private List<GiftStoryChoice> _matches = new List<GiftStoryChoice>();

        private void OpenNpcPicker()
        {
            _pickingNpc = true;
            _pickerTarget = null;
            OpenPicker();
        }

        private void OpenStoryPicker(GiftBindingRow target, int slot)
        {
            _pickingNpc = false;
            _pickerTarget = target;
            _pickerSlot = slot;
            OpenPicker();
        }

        private void OpenPicker()
        {
            try { _catalog = GiftCatalogProvenance.Load(_modRoot); }
            catch (Exception e)
            {
                Plugin.Log.LogError("[GiftBinding] 刷新剧情库失败：" + e);
                SetStatus("无法读取当前作品，未打开选择器。请检查剧情 JSON。", true);
                return;
            }
            ClosePicker();
            _pickerSelection = null;
            _pickerPage = 0;
            IEnumerable<GiftStoryChoice> candidates = _pickingNpc
                ? _catalog.Npcs.Where(pair => !_draft.Rows.Any(row => row.NpcId == pair.Key))
                    .Select(pair => new GiftStoryChoice { Id = pair.Key, Title = pair.Value, Content = "NPC " + pair.Key })
                : _catalog.Talks.Values;
            _pickerCandidates = candidates.OrderByDescending(choice => choice.IsLocal)
                .ThenByDescending(choice => choice.IsEntry).ThenBy(choice => choice.Id).ToList();
            _picker = GiftBindingUi.Panel(_panel, "GiftPicker",
                GiftBindingUi.Style?.PanelColor ?? GiftBindingUi.Paper, nativeFrame: true);
            GiftBindingUi.Fill(_picker.transform);
            Transform root = _picker.transform;
            string title = _pickingNpc ? "选择送礼 NPC" :
                "选择剧情 · " + _catalog.NpcName(_pickerTarget.NpcId) + " · " +
                (_pickerTarget.Separate ? (_pickerSlot == 0 ? "男主" : "女主") : "男女主共用");
            GiftBindingUi.Label(root, "PickerTitle", title, 23, 22, 14, _width - 180, 38);
            GiftBindingUi.Button(root, "Back", "返回", _width - 112, 16, 90, 34, ClosePicker);
            _pickerSearch = GiftBindingUi.Search(root, _pickingNpc ? "搜索 NPC 姓名或 ID…" : "搜索剧情标题、首句台词或 ID…",
                22, 65, _width - 44, query =>
                {
                    _pickerPage = 0;
                    ClearPickerSelection();
                    RefreshPicker();
                });
            if (!_pickingNpc)
            {
                Button source = null;
                source = GiftBindingUi.Button(root, "SourceFilter", SourceTitle(), 22, 115, 220, 34, () =>
                {
                    _localOnly = !_localOnly;
                    source.GetComponentInChildren<Text>().text = SourceTitle();
                    ResetPicker();
                });
                Button entries = null;
                entries = GiftBindingUi.Button(root, "EntryFilter", EntryTitle(), 252, 115, 222, 34, () =>
                {
                    _entriesOnly = !_entriesOnly;
                    entries.GetComponentInChildren<Text>().text = EntryTitle();
                    ResetPicker();
                });
                GiftBindingUi.Label(root, "SourceHelp", "其它已加载配置可能需要玩家安装相应 Mod。",
                    15, 488, 113, _width - 510, 40, GiftBindingUi.Muted);
            }
            else GiftBindingUi.Label(root, "NpcHelp", "已添加的 NPC 不会重复显示。选择后点击「添加 NPC」。",
                16, 22, 110, _width - 44, 38, GiftBindingUi.Muted);

            _pickerPageSize = Math.Max(1, (int)((_height - 342) / 57));
            var rowsGo = new GameObject("PickerRows", typeof(RectTransform));
            rowsGo.transform.SetParent(root, false);
            _pickerRows = (RectTransform)rowsGo.transform;
            GiftBindingUi.Place(_pickerRows, 22, 162, _width - 44, _pickerPageSize * 57);
            float pageY = 166 + _pickerPageSize * 57;
            _pickerPrevious = GiftBindingUi.Button(root, "Previous", "上一页", 22, pageY, 92, 32, () =>
            {
                _pickerPage--;
                ClearPickerSelection();
                RenderPickerPage();
            });
            _pickerPageText = GiftBindingUi.Label(root, "Page", "", 16, 128, pageY, _width - 256, 32);
            _pickerPageText.alignment = TextAnchor.MiddleCenter;
            _pickerNext = GiftBindingUi.Button(root, "Next", "下一页", _width - 114, pageY, 92, 32, () =>
            {
                _pickerPage++;
                ClearPickerSelection();
                RenderPickerPage();
            });
            _pickerPreview = GiftBindingUi.Label(root, "SelectionPreview", "选择一项后，在这里核对首句并确认。",
                16, 22, _height - 128, _width - 44, 73, GiftBindingUi.Muted);
            _pickerConfirm = GiftBindingUi.Button(root, "ConfirmSelection",
                _pickingNpc ? "添加 NPC" : "绑定此首句", _width - 180, _height - 47, 158, 34,
                ConfirmPicker, GiftButtonKind.Primary);
            _pickerConfirm.interactable = false;
            if (!_pickingNpc && _pickerTarget.Separate)
                GiftBindingUi.Button(root, "DisableGender", "此性别不触发", 22, _height - 47, 168, 34, () =>
                {
                    _pickerSelection = 0;
                    _pickerPreview.text = "将此性别设为不触发。另一个性别的入口不变。\n点击「绑定此首句」确认。";
                    _pickerConfirm.interactable = true;
                });
            RefreshPicker();
        }

        private string SourceTitle() => _localOnly ? "来源：当前作品" : "来源：全部已加载＋作品";
        private string EntryTitle() => _entriesOnly ? "范围：事件入口" : "范围：所有对话（高级）";

        private void ResetPicker()
        {
            _pickerPage = 0;
            ClearPickerSelection();
            RefreshPicker();
        }

        private void ClearPickerSelection()
        {
            _pickerSelection = null;
            if (_pickerConfirm != null) _pickerConfirm.interactable = false;
            if (_pickerPreview != null) _pickerPreview.text = "选择一项后，在这里核对首句并确认。";
        }

        private void RefreshPicker()
        {
            string query = _pickerSearch?.text;
            _matches = _pickerCandidates.Where(choice =>
                (_pickingNpc || ((!_localOnly || choice.IsLocal) && (!_entriesOnly || choice.IsEntry))) &&
                choice.Matches(query)).ToList();
            RenderPickerPage();
        }

        private void RenderPickerPage()
        {
            int pages = Math.Max(1, (_matches.Count + _pickerPageSize - 1) / _pickerPageSize);
            _pickerPage = Math.Max(0, Math.Min(pages - 1, _pickerPage));
            GiftBindingUi.Clear(_pickerRows);
            for (int i = 0; i < _pickerPageSize && _pickerPage * _pickerPageSize + i < _matches.Count; i++)
            {
                GiftStoryChoice choice = _matches[_pickerPage * _pickerPageSize + i];
                string label = "[" + choice.Id + "] " + GiftStoryCatalog.Compact(choice.Title, 62);
                if (!_pickingNpc) label += "  · " + (choice.IsLocal ? "当前作品" : "已加载配置") +
                    "\n" + GiftStoryCatalog.Compact(choice.Content, 82);
                Button button = GiftBindingUi.Button(_pickerRows, "Choice_" + choice.Id, label,
                    0, i * 57, _width - 44, 51, () => SelectPickerChoice(choice));
                Text text = button.GetComponentInChildren<Text>();
                text.alignment = TextAnchor.MiddleLeft;
                text.fontSize = 16;
                if (_pickerSelection == choice.Id) button.image.color = GiftBindingUi.SelectedGold;
            }
            if (_matches.Count == 0)
                GiftBindingUi.Label(_pickerRows, "NoResults",
                    _pickingNpc ? "没有匹配的 NPC，请更换关键词。" :
                        "没有匹配的剧情。请切换来源或「所有对话（高级）」。\n新写的剧情需要先在事件编辑器保存，再到这里绑定。",
                    18, 12, 12, _width - 68, 85, GiftBindingUi.Muted);
            _pickerPageText.text = (_pickerPage + 1) + " / " + pages + " 页 · " + _matches.Count + " 项";
            _pickerPrevious.interactable = _pickerPage > 0;
            _pickerNext.interactable = _pickerPage + 1 < pages;
        }

        private void SelectPickerChoice(GiftStoryChoice choice)
        {
            _pickerSelection = choice.Id;
            _pickerPreview.text = _pickingNpc ? "将添加：" + choice.Title + "（" + choice.Id + "）" :
                "首句 " + choice.Id + " · " + GiftStoryCatalog.Compact(choice.Title, 64) +
                "\n" + GiftStoryCatalog.Compact(choice.Content, 180);
            _pickerConfirm.interactable = true;
            RenderPickerPage();
        }

        private void ConfirmPicker()
        {
            if (!_pickerSelection.HasValue) return;
            if (_pickingNpc) _draft.AddNpc(_pickerSelection.Value);
            else _pickerTarget.SetTalk(_pickerSlot, _pickerSelection.Value);
            ClosePicker();
            RefreshRows();
            if (_pickingNpc) _scroll.verticalNormalizedPosition = 0;
            SetStatus("已更新窗口草稿。点击「应用绑定」回填原表单，取消则放弃本窗口的全部修改。");
        }

        private void ClosePicker()
        {
            if (_picker != null)
            {
                _picker.SetActive(false);
                Destroy(_picker);
            }
            _picker = null;
        }
    }
}
