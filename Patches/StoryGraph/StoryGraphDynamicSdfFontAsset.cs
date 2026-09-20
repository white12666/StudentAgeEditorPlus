using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 从游戏当前 UGUI 中文字体创建剧情图共享的动态 TMP/SDF 字体。
    /// 动态多图集既允许作者输入任意中文，也让不同屏幕分辨率下的字形继续由
    /// SDF 边缘重建，而不是放大旧 UGUI 字体的栅格结果。
    /// </summary>
    internal static class StoryGraphDynamicSdfFontAsset
    {
        private const string CommonCharacters =
            "剧情图编辑查看刷新显示全图缩放连线关键预览本句搜索编号或文字回车定位" +
            "上个下个结果返回新增对话选项复制删除撤销重做隐藏属性保存放弃关闭" +
            "属性检查器基础人物画面逻辑小游戏高级正文角色记录标签配置提示" +
            "资源背景音乐屏幕效果查阅选择上一页下一页正序倒序当前尚未缺失错误" +
            "入口结束未使用分组注释标题内容条件成功失败路径演出帮助警告" +
            "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz" +
            " .,;:!?+-×÷=/%()[]{}<>｜：，。；！？（）【】《》“”‘’、…·→←↑↓✓○●\n";

        private static readonly Dictionary<int, TMP_FontAsset> Assets =
            new Dictionary<int, TMP_FontAsset>();
        private static readonly HashSet<string> LoggedMissingCharacters =
            new HashSet<string>(StringComparer.Ordinal);
        private static Font _osFallbackFont;
        private static bool _creationFailureLogged;
        private static bool _fallbackLogged;

        internal static TMP_FontAsset GetOrCreate(Font sourceFont, string initialCharacters)
        {
            if (sourceFont == null) sourceFont = GetOsFallbackFont(null);
            if (sourceFont == null) return TryGetDefaultFontAsset();

            int key = sourceFont.GetInstanceID();
            TMP_FontAsset asset;
            if (!Assets.TryGetValue(key, out asset) || asset == null)
            {
                asset = Create(sourceFont);
                if (asset == null) return TryGetDefaultFontAsset();
                Assets[key] = asset;
            }

            PrepareCharacters(asset, CommonCharacters);
            PrepareCharacters(asset, initialCharacters);
            return asset;
        }

        internal static void PrepareCharacters(TMP_FontAsset asset, string value)
        {
            if (asset == null || string.IsNullOrEmpty(value)
                || asset.atlasPopulationMode != AtlasPopulationMode.Dynamic) return;
            try
            {
                asset.TryAddCharacters(value, true);
                if (asset.HasCharacters(value)) return;
                List<char> missingCharacters;
                asset.HasCharacters(value, out missingCharacters);
                string missing = missingCharacters == null
                    ? value : new string(missingCharacters.ToArray());
                if (string.IsNullOrEmpty(missing)) return;
                string key = asset.GetInstanceID() + ":" + missing;
                if (LoggedMissingCharacters.Add(key))
                    Plugin.Log?.LogWarning(
                        "[StoryGraph/TMP] 当前字体缺少字符：" + missing);
            }
            catch (Exception e)
            {
                string key = asset.GetInstanceID() + ":" + e.GetType().FullName;
                if (LoggedMissingCharacters.Add(key))
                    Plugin.Log?.LogWarning(
                        "[StoryGraph/TMP] 动态补字失败，现有字形继续可用：" + e.Message);
            }
        }

        private static TMP_FontAsset Create(Font sourceFont)
        {
            try
            {
                TMP_FontAsset asset = CreateAsset(sourceFont);
                if (!SupportsCoreGlyphs(asset))
                {
                    if (asset != null) UnityEngine.Object.Destroy(asset);
                    Font fallback = GetOsFallbackFont(sourceFont);
                    if (fallback != null && !ReferenceEquals(fallback, sourceFont))
                    {
                        if (!_fallbackLogged)
                        {
                            _fallbackLogged = true;
                            Plugin.Log?.LogInfo(
                                "[StoryGraph/TMP] 游戏 UGUI 字体“" + sourceFont.name
                                + "”无法提取完整中文轮廓；改用系统字体“"
                                + fallback.name + "”生成共享 SDF。");
                        }
                        asset = CreateAsset(fallback);
                    }
                }
                return SupportsCoreGlyphs(asset) ? asset : null;
            }
            catch (Exception e)
            {
                if (!_creationFailureLogged)
                {
                    _creationFailureLogged = true;
                    Plugin.Log?.LogError(
                        "[StoryGraph/TMP] 无法创建动态 SDF 字体，将尝试 TMP 默认字体：" + e);
                }
                return null;
            }
        }

        private static TMP_FontAsset CreateAsset(Font sourceFont)
        {
            if (sourceFont == null) return null;
            TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(sourceFont,
                90, 9, GlyphRenderMode.SDFAA_HINTED, 2048, 2048,
                AtlasPopulationMode.Dynamic, true);
            if (asset == null) asset = TMP_FontAsset.CreateFontAsset(sourceFont);
            if (asset == null) return null;
            asset.name = "StudentAgeEditorPlus_StoryGraph_SDF_" + sourceFont.name;
            asset.hideFlags = HideFlags.DontSave;
            asset.atlasPopulationMode = AtlasPopulationMode.Dynamic;
            asset.isMultiAtlasTexturesEnabled = true;
            if (asset.fallbackFontAssetTable == null)
                asset.fallbackFontAssetTable = new List<TMP_FontAsset>();
            return asset;
        }

        private static bool SupportsCoreGlyphs(TMP_FontAsset asset)
        {
            if (asset == null || asset.atlasPopulationMode != AtlasPopulationMode.Dynamic)
                return false;
            try
            {
                string missing;
                return asset.TryAddCharacters("剧情图Aa01", out missing, true)
                       && string.IsNullOrEmpty(missing);
            }
            catch
            {
                return false;
            }
        }

        private static Font GetOsFallbackFont(Font preferred)
        {
            if (_osFallbackFont != null) return _osFallbackFont;
            try
            {
                string[] installed = Font.GetOSInstalledFontNames() ?? new string[0];
                string[] candidates =
                {
                    preferred == null ? string.Empty : preferred.name,
                    "Microsoft YaHei UI", "Microsoft YaHei", "DengXian",
                    "SimHei", "SimSun", "Noto Sans CJK SC", "Source Han Sans CN"
                };
                string selected = null;
                foreach (string candidate in candidates)
                {
                    if (string.IsNullOrWhiteSpace(candidate)) continue;
                    foreach (string installedName in installed)
                    {
                        if (!string.Equals(installedName, candidate,
                                StringComparison.OrdinalIgnoreCase)) continue;
                        selected = installedName;
                        break;
                    }
                    if (!string.IsNullOrEmpty(selected)) break;
                }
                if (string.IsNullOrEmpty(selected)) return null;
                _osFallbackFont = Font.CreateDynamicFontFromOSFont(selected, 90);
                if (_osFallbackFont != null)
                    _osFallbackFont.hideFlags = HideFlags.DontSave;
                return _osFallbackFont;
            }
            catch
            {
                return null;
            }
        }

        private static TMP_FontAsset TryGetDefaultFontAsset()
        {
            try
            {
                return TMP_Settings.defaultFontAsset;
            }
            catch
            {
                return null;
            }
        }
    }
}
