using System;
using System.Collections.Generic;
using Config;
using GenUI.Mod;
using HarmonyLib;
using Sdk;
using UnityEngine;
using View.Evt;
using View.Common;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 修复：事件对话编辑器的人物预览最多只显示 3 个人物，实际 UI 最多能放 9 个。
    ///
    /// 成因：ModEvtEditUI 预制体为左/中/右三个方位各定义了 3 个槽位
    ///（root_left_0/1/2、root_mid_0/1/2、root_right_0/1/2，共 9 个），
    /// 但 ModEvtEditView.InitUI() 每个方位只创建了 1 个 Cell（_0），
    /// 6 个槽位被白白浪费。同时 RefreshRoles() 的分配逻辑写死了 [0] 索引，
    /// 同方位第 2 个人物起被静默丢弃。
    ///
    /// 预制体实测数据（UnityPy dump，修复逻辑依赖这些事实）：
    ///   - 槽位在屏幕上的排列顺序是按 x 坐标：_2 在最左、_0 居中、_1 在右
    ///     （例如左方位：left_2=-770, left_0=-560, left_1=-350），
    ///     并非想当然的 _0 → _1 → _2；
    ///   - _1/_2 槽位默认 inactive（原版从未启用），且其 btn_add 布局是半成品：
    ///     anchoredPosition=(0,0)、sizeDelta=(400,0)、"+"图标居中，
    ///     与 _0 槽（(0,-220)、(400,-440)、图标 y=156）不一致，
    ///     直接启用会导致空槽的 "+" 高低不齐（视觉错位）。
    ///   - 9 个槽位横向排得很密（相邻中心距最小仅 140px），而每个 btn_add 的
    ///     透明点击区（_click，拉伸填满按钮，宽 400px）远大于间距——9 槽全启用后
    ///     相邻点击区大面积互相叠压，例如 left_1 的 "+" 图标整个被 mid_2 的
    ///     点击区盖住：点这里的 "+"，响应的却是隔壁槽（人物加错方位）。
    ///
    /// 修复：
    ///   - 补丁 A（InitUI Postfix）：创建 _1、_2 槽位 Cell；把它们的 btn_add
    ///     布局与同方位 _0 槽对齐；把全部 9 个槽位的点击区收窄到 "+" 图标
    ///     附近（130×130，相邻互不重叠；人物立绘 raycastTarget=false 不拦截
    ///     点击，有人的槽位照样能点）；每个方位的槽位列表按屏幕 x 坐标排序，
    ///     使后续填充顺序 = 视觉从左到右。

    ///   - 补丁 B（RefreshRoles Prefix）：替换分配逻辑：
    ///       1) 优先把"刚通过 + 按钮添加/替换的人物"放进被点击的那个槽位；
    ///       2) 已在场人物保持上次的槽位不动（避免增删人物时其他人跳位）；
    ///       3) 其余人物按"居中 → 左 → 右"填入空槽：每方位第 1 人落在
    ///          原版唯一启用的居中槽（_0），旧 mod / 旧数据单人对话的
    ///          预览位置与原版编辑器完全一致（向后兼容）；
    ///       4) 所有槽位常显，作者随时可点任意空槽的 "+"（不再是原版的
    ///          "前一个槽有人，下一个槽才出现"的链式显示）。
    ///   - 补丁 C（OnCreateRole Prefix）：替换 + 按钮逻辑，允许同方位多人物；
    ///     记录点击的槽位供补丁 B 精确落位；替换从上文对话入场的人物时，
    ///     补退场动作实现真正的"换人"而非"多加一个人"。
    /// </summary>
    [HarmonyPatch(typeof(ModEvtEditView), "InitUI")]
    internal static class EvtRoleDisplayInitPatch
    {
        private static void Postfix(ModEvtEditView __instance)
        {
            try
            {
                var t = Traverse.Create(__instance);

                // 获取 roleCells 字段
                var roleCells = t.Field("roleCells")
                    .GetValue<Dictionary<TalkAxis, List<Cell_ModEvtRoleItemUI>>>();
                if (roleCells == null) return;

                // 每个方位需要补全的额外槽位（_1、_2）
                var extraSlots = new Dictionary<TalkAxis, string[]>
                {
                    { TalkAxis.Left,  new[] { "root_left_1",  "root_left_2"  } },
                    { TalkAxis.Mid,   new[] { "root_mid_1",   "root_mid_2"   } },
                    { TalkAxis.Right, new[] { "root_right_1", "root_right_2" } },
                };

                // 获取私有方法委托
                // 带参数的方法必须指定参数类型，否则 Traverse 找不到（日志已证实）
                var onRenderRole = t.Method("OnRenderRole", new[] { typeof(Cell_ModEvtRoleItemUI) });
                var onCreateRole = t.Method("OnCreateRole", new[] { typeof(Cell_ModEvtRoleItemUI) });

                bool added = false;

                foreach (var kvp in extraSlots)
                {
                    TalkAxis axis = kvp.Key;
                    if (!roleCells.TryGetValue(axis, out var list) || list == null) continue;
                    if (list.Count == 0) continue;

                    // 该方位 _0 槽的 btn_add，作为布局模板
                    var templateBtn = list[0].btn_add;

                    foreach (var fieldName in kvp.Value)
                    {
                        var rootRt = t.Field(fieldName).GetValue<RectTransform>();
                        if (rootRt == null) continue;

                        // 创建 Cell（构造函数内部会执行 InitUI 绑定 btn_add）
                        var cell = new Cell_ModEvtRoleItemUI(rootRt.gameObject);

                        // 预制体里 _1/_2 槽的 btn_add 布局是半成品（"+"图标位置、
                        // 点击区域都与 _0 槽不同），把它对齐到 _0 槽的样式。
                        AlignAddButton(cell.btn_add, templateBtn);

                        // 注册渲染回调（与原版 InitUI 中的 SetOnShow<...>(OnRenderRole) 一致）。
                        // OnRenderRole 是私有方法，通过 Traverse 间接调用。
                        cell.SetOnShow<Cell_ModEvtRoleItemUI>(c => onRenderRole.GetValue(c));

                        // 调用 OnCreateRole（创建 TalkRoleItem 子物体 + 绑定按钮事件；
                        // 会被下方补丁 C 拦截，走多人物版逻辑）
                        onCreateRole.GetValue(cell);

                        list.Add(cell);
                        added = true;
                    }

                    // 收窄全部槽位（含 _0）的点击区到 "+" 图标附近：
                    // 9 个槽位排得很密，原始点击区（宽 400）会互相叠压，
                    // 导致"点这个 + 却触发隔壁槽"的错位反应。
                    foreach (var cell in list)
                        ShrinkClickArea(cell.btn_add);

                    // 按屏幕 x 坐标排序：预制体的视觉顺序是 _2 < _0 < _1，
                    // 排序后列表顺序 = 视觉从左到右，后续 RefreshRoles 的
                    // "依次填入空槽"才不会出现人物乱序跳位。
                    list.Sort((a, b) =>
                        a.transform.anchoredPosition.x.CompareTo(b.transform.anchoredPosition.x));
                }

                if (added)
                    Plugin.Log.LogInfo("[EvtRoleDisplay] 已补全事件编辑器人物显示槽位（3→9），并对齐 + 按钮布局。");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[EvtRoleDisplayInit] {e}");
            }
        }

        /// <summary>
        /// 把 _1/_2 槽位 btn_add 的 RectTransform 布局（含 "+" 图标、点击区域子节点）
        /// 对齐到 _0 槽位的模板样式。
        /// </summary>
        private static void AlignAddButton(UIButton dst, UIButton src)
        {
            if (dst == null || src == null) return;
            var d = dst.transform;
            var s = src.transform;
            if (d == null || s == null) return;

            d.anchorMin = s.anchorMin;
            d.anchorMax = s.anchorMax;
            d.pivot = s.pivot;
            d.anchoredPosition = s.anchoredPosition;
            d.sizeDelta = s.sizeDelta;

            // 同步子节点（_add 加号图标、_click 点击区域）的布局
            for (int i = 0; i < s.childCount; i++)
            {
                var sc = s.GetChild(i) as RectTransform;
                if (sc == null) continue;
                var dcT = d.Find(sc.name);
                var dc = dcT as RectTransform;
                if (dc == null) continue;
                dc.anchorMin = sc.anchorMin;
                dc.anchorMax = sc.anchorMax;
                dc.pivot = sc.pivot;
                dc.anchoredPosition = sc.anchoredPosition;
                dc.sizeDelta = sc.sizeDelta;
            }
        }

        /// <summary>
        /// 把槽位 btn_add 的点击判定区收窄到 "+" 图标附近（130×130）。
        /// 相邻槽位中心距最小 140px，收窄后互不重叠；
        /// 人物立绘（icon_role/l2d_role）不参与点击判定，不受影响。
        /// </summary>
        private static void ShrinkClickArea(UIButton btn)
        {
            if (btn == null || btn.transform == null) return;
            var root = btn.transform;

            var add = root.Find("_add") as RectTransform;
            Vector2 iconPos = add != null ? add.anchoredPosition : Vector2.zero;

            // _click：从"拉伸填满按钮"改为围绕 + 图标的固定小区域
            var click = root.Find("_click") as RectTransform;
            if (click != null)
            {
                click.anchorMin = new Vector2(0.5f, 0.5f);
                click.anchorMax = new Vector2(0.5f, 0.5f);
                click.pivot = new Vector2(0.5f, 0.5f);
                click.anchoredPosition = iconPos;
                click.sizeDelta = new Vector2(130f, 130f);
            }

            // UIButton 兜底创建的 EmptyGraphic（全填充的透明点击图形）若存在，
            // 会把点击区又扩回整个按钮，这里一并关闭。
            var empty = root.Find("empty");
            if (empty != null)
            {
                var g = empty.GetComponent<UnityEngine.UI.Graphic>();
                if (g != null) g.raycastTarget = false;
            }
        }
    }

    [HarmonyPatch(typeof(ModEvtEditView), "RefreshRoles")]
    internal static class EvtRoleDisplayRefreshPatch
    {
        /// <summary>
        /// Prefix 替换原方法。分配优先级：
        ///   0) 刚点击 + 添加/替换的人物 → 落在被点击的那个槽位；
        ///   1) 已在场人物 → 保持上一次所在槽位（增删他人时不跳位）；
        ///   2) 其余人物 → 按"居中 → 左 → 右"填入空槽（第 1 人与原版编辑器
        ///      的显示位置一致，兼容旧 mod 的单人对话预览）。
        /// 最后所有槽位常显（空槽显示 + 按钮）。
        /// </summary>
        private static bool Prefix(ModEvtEditView __instance)
        {
            try
            {
                var t = Traverse.Create(__instance);

                var roleCells = t.Field("roleCells")
                    .GetValue<Dictionary<TalkAxis, List<Cell_ModEvtRoleItemUI>>>();
                if (roleCells == null) return false;

                var curSelect = t.Field("curSelect").GetValue<TalkCfg>();
                if (curSelect == null) return false;

                // 取出"刚通过 + 按钮操作的槽位"（一次性，用完即清）
                var pendingCell = EvtRoleDisplayCreatePatch.PendingCell;
                int pendingPersonId = EvtRoleDisplayCreatePatch.PendingPersonId;
                EvtRoleDisplayCreatePatch.ClearPending();

                // ── 收集当前在场人物（用 FindRoles 确定谁在场） ──
                var foundRoles = t.Method("FindRoles", new[] { typeof(int) })
                    .GetValue(curSelect.id) as Dictionary<int, TalkAxis>;
                // foundRoles: key=人物ID, value=方位。可能为 null。

                // ── 阶段 1：记录每个槽位当前的人物，然后清空 ──
                // 记录旧位置用于保持稳定性：同一个人物应尽量留在原来的槽位
                var oldSlots = new Dictionary<TalkAxis, Dictionary<int, int>>();
                // oldSlots[axis][personId] = slotIndex
                foreach (var kvp in roleCells)
                {
                    if (kvp.Value == null) continue;
                    var axisMap = new Dictionary<int, int>();
                    for (int i = 0; i < kvp.Value.Count; i++)
                    {
                        if (kvp.Value[i].data != null)
                            axisMap[(int)kvp.Value[i].data] = i;
                    }
                    oldSlots[kvp.Key] = axisMap;
                    foreach (var item in kvp.Value)
                    {
                        item.SetData(null, kvp.Key);
                        item.gameObject.SetActive(false);
                    }
                }

                // ── 阶段 2：分配人物到槽位 ──
                if (foundRoles != null)
                {
                    foreach (var kvp in roleCells)
                    {
                        TalkAxis axis = kvp.Key;
                        var list = kvp.Value;
                        if (list == null) continue;

                        // 收集该方位所有在场人物
                        var axisPersons = new List<int>();
                        foreach (var roleKvp in foundRoles)
                        {
                            if (roleKvp.Value == axis)
                                axisPersons.Add(roleKvp.Key);
                        }

                        var placed = new HashSet<int>();

                        // 第 0 轮：刚点击 + 添加/替换的人物，精确落在被点击的槽位
                        if (pendingCell != null && pendingPersonId > 0 &&
                            axisPersons.Contains(pendingPersonId))
                        {
                            int slotIdx = list.IndexOf(pendingCell);
                            if (slotIdx >= 0 && list[slotIdx].data == null)
                            {
                                list[slotIdx].SetData(pendingPersonId, axis);
                                placed.Add(pendingPersonId);
                            }
                        }

                        // 第 1 轮：把之前就在某个槽位的人物放回原位
                        if (oldSlots.TryGetValue(axis, out var oldMap))
                        {
                            foreach (var personId in axisPersons)
                            {
                                if (placed.Contains(personId)) continue;
                                if (oldMap.TryGetValue(personId, out int slotIdx) &&
                                    slotIdx < list.Count && list[slotIdx].data == null)
                                {
                                    list[slotIdx].SetData(personId, axis);
                                    placed.Add(personId);
                                }
                            }
                        }

                        // 第 2 轮：把剩余人物填入空槽。
                        // 填充顺序为"居中槽 → 左槽 → 右槽"（列表已按屏幕 x 排序，
                        // 3 槽时索引 1 即居中槽，也就是原版唯一启用的 _0 槽）。
                        // 这样旧 mod / 旧数据里"每方位单人"的对话在编辑器中
                        // 依旧显示在与原版编辑器相同的居中位置，不产生预览偏移；
                        // 第 2、3 人再依次排到左、右两侧。
                        var fillOrder = new List<int>(list.Count);
                        if (list.Count == 3)
                        {
                            fillOrder.Add(1); fillOrder.Add(0); fillOrder.Add(2);
                        }
                        else
                        {
                            for (int i = 0; i < list.Count; i++) fillOrder.Add(i);
                        }
                        int fillPtr = 0;
                        foreach (var personId in axisPersons)
                        {
                            if (placed.Contains(personId)) continue;
                            while (fillPtr < fillOrder.Count && list[fillOrder[fillPtr]].data != null)
                                fillPtr++;
                            if (fillPtr < fillOrder.Count)
                            {
                                list[fillOrder[fillPtr]].SetData(personId, axis);
                                fillPtr++;
                            }
                        }
                    }
                }

                // ── 阶段 3：全部显示 ──
                // 每个方位的 3 个槽位全部可见，让作者随时能点击空槽位的"+"按钮。
                foreach (var kvp in roleCells)
                {
                    if (kvp.Value == null) continue;
                    foreach (var cell in kvp.Value)
                    {
                        cell.gameObject.SetActive(true);
                    }
                }

                return false; // 跳过原方法
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[EvtRoleDisplayRefresh] {e}");
                return true; // 出错时回退到原方法
            }
        }
    }


    /// <summary>
    /// 补丁 C：OnCreateRole Prefix — 替换添加逻辑，允许同方位多人物。
    ///
    /// 原版对同一方位的 1002（入场）动作做替换而非新增，
    /// 导致每方位只能有 1 个入场人物。重写为：
    ///   - 空槽位 → 新增一条 1002 入场动作；
    ///   - 槽位已有人物 → 替换：
    ///       · 该人物的 1002 在本对话里 → 直接改 ID；
    ///       · 该人物是从上文对话入场的（本对话没有他的 1002）→ 给他补一条
    ///         2002 退场，再为新人物加 1002 入场（否则会变成"旧人还站着，
    ///         新人多加一个"）。
    /// 同时记录被点击的槽位（PendingCell/PendingPersonId），
    /// 供 RefreshRoles 把人物精确落在作者点击的那个槽位上。
    /// </summary>
    [HarmonyPatch(typeof(ModEvtEditView), "OnCreateRole")]
    internal static class EvtRoleDisplayCreatePatch
    {
        /// <summary>刚通过 + 按钮添加/替换人物的目标槽位（一次性，RefreshRoles 消费后清除）。</summary>
        internal static Cell_ModEvtRoleItemUI PendingCell;
        internal static int PendingPersonId;

        internal static void ClearPending()
        {
            PendingCell = null;
            PendingPersonId = 0;
        }

        private static bool Prefix(ModEvtEditView __instance, Cell_ModEvtRoleItemUI _cell)
        {
            try
            {
                var t = Traverse.Create(__instance);

                // ── 步骤 1-2：与原版完全一致 ──
                // 创建 TalkRoleItem 子物体
                var roleItem = TalkRoleItem.CreateCell(_cell.transform);
                roleItem.transform.localScale = new Vector3(0.7f, 0.7f, 0.7f);
                _cell.SetKeyObj("role", roleItem);

                // ── 步骤 3：重写 btn_add 点击事件 ──
                _cell.btn_add.AddClick(delegate
                {
                    TalkAxis axis = (TalkAxis)_cell.parentData;
                    Action<int> action = delegate(int _id)
                    {
                        var curSelect = t.Field("curSelect").GetValue<TalkCfg>();
                        if (curSelect == null) return;
                        if (_id == -1)
                        {
                            // ── 移除/退场逻辑（与原版一致）──
                            ClearPending();
                            if (_cell.data != null)
                            {
                                int oldId = (int)_cell.data;
                                RemoveRole(t, curSelect, oldId);
                                t.Field("input_action").GetValue<global::UnityEngine.UI.InputField>().text =
                                    ModCtrl.ListToStr(curSelect.roles);
                            }
                        }
                        else


                        {
                            // ── 添加/替换逻辑 ──
                            // 注意：写入新人物的 1002 入场前，必须先清掉他在本对话的
                            // 残留条目——最典型的是此前"清空"留下的 [id, 2002] 退场记录。
                            // FindRoles 对同一人物只认列表里的第一条动作（后面的忽略），
                            // 若 2002 残留在前、新加的 1002 在后，这个人会被判定为
                            // "已退场"，表现为"清空后重新添加没反应"。
                            if (_cell.data != null)
                            {
                                // 槽位已有人物 → 替换
                                int oldId = (int)_cell.data;
                                if (curSelect.roles != null)
                                {
                                    int idx = curSelect.roles.FindIndex(
                                        (List<float> _p) => _p[0] == (float)oldId && _p[1] == 1002f);
                                    if (idx > -1)
                                    {
                                        // 旧人物的入场动作就在本对话 → 直接改 ID。
                                        // 先记住该条目引用，清掉新人物的残留后再改
                                        // （清理可能移动列表索引，但不影响引用）。
                                        var entry = curSelect.roles[idx];
                                        curSelect.roles.RemoveAll(
                                            (List<float> _p) => _p[0] == (float)_id);
                                        entry[0] = _id;
                                    }
                                    else
                                    {
                                        // 旧人物是从上文对话入场的：先让他退场，再让新人物入场。
                                        // （若只加新人物的 1002，旧人物会依然在场，变成两个人）
                                        RemoveRole(t, curSelect, oldId);
                                        curSelect.roles.RemoveAll(
                                            (List<float> _p) => _p[0] == (float)_id);
                                        curSelect.roles.Add(new List<float>
                                        {
                                            _id, 1002f, 0f, (float)axis
                                        });
                                    }
                                }
                                else
                                {
                                    curSelect.roles = new List<List<float>>
                                    {
                                        new List<float> { _id, 1002f, 0f, (float)axis }
                                    };
                                }
                            }
                            else
                            {
                                // 空槽位 → 新增一条 1002 入场动作
                                if (curSelect.roles == null)
                                {
                                    curSelect.roles = new List<List<float>>();
                                }
                                curSelect.roles.RemoveAll(
                                    (List<float> _p) => _p[0] == (float)_id);
                                curSelect.roles.Add(new List<float>
                                {
                                    _id, 1002f, 0f, (float)axis
                                });
                            }

                            t.Field("input_action").GetValue<global::UnityEngine.UI.InputField>().text =
                                ModCtrl.ListToStr(curSelect.roles);

                            // 记录目标槽位：RefreshRoles 会把该人物放进作者点击的这个槽
                            PendingCell = _cell;
                            PendingPersonId = _id;
                        }
                        t.Method("RefreshRoles").GetValue();
                    };

                    var personCfgs = t.Field("personCfgs")
                        .GetValue<Dictionary<int, PersonCfg>>();
                    var foundRoles = t.Method("FindRoles", new[] { typeof(int) })
                        .GetValue(curSelectTalkId(t)) as Dictionary<int, TalkAxis>;
                    var ignoreList = foundRoles != null
                        ? new List<int>(foundRoles.Keys)
                        : new List<int>();
                    UIMgr.OpenView<ModSelectRoleView>(UILayerType.None, null,
                        new object[3] { personCfgs, action, ignoreList });
                });

                return false; // 跳过原方法
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[EvtRoleDisplayCreate] {e}");
                return true; // 出错时回退到原方法
            }
        }

        /// <summary>
        /// 让人物 personId 从当前对话退场（与原版移除逻辑一致）：
        /// 删掉本对话里他的全部动作条目，补一条 2002 退场动作；
        /// 若他是当前说话人（roleIds），同步移除并刷新对话框头像。
        /// </summary>
        private static void RemoveRole(Traverse t, TalkCfg curSelect, int personId)
        {
            if (curSelect.roles == null)
            {
                curSelect.roles = new List<List<float>>
                {
                    new List<float> { personId, 2002f }
                };
            }
            else
            {
                curSelect.roles.RemoveAll((List<float> _item) => _item[0] == (float)personId);
                curSelect.roles.Add(new List<float> { personId, 2002f });
            }
            if (curSelect.roleIds.Has(personId))
            {
                curSelect.roleIds.Remove(personId);
                t.Method("RefreshRoleTalk").GetValue();
            }
        }

        /// <summary>安全获取 curSelect.id，用于 FindRoles 调用。</summary>
        private static int curSelectTalkId(Traverse t)
        {
            var curSelect = t.Field("curSelect").GetValue<TalkCfg>();
            return curSelect != null ? curSelect.id : 0;
        }
    }
}
