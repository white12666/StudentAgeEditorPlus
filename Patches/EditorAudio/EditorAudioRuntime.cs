using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using Config;
using HarmonyLib;
using Sdk;
using UnityEngine;
using View.Evt;

namespace StudentAgeEditorPlus.Patches
{
    internal static class EditorAudioRuntime
    {
        internal static readonly EditorAudioPolicy Policy = new EditorAudioPolicy();
        internal static event Action Changed;
        private static ConfigEntry<bool> quietSetting;
        private static AudioSource source;
        private static bool priorMute;
        private static bool externalMute;
        private static bool previewRequest;
        private static bool pendingLoad;
        private static string pendingPreviewUrl;
        private static readonly FieldInfo MusicUrl =
            AccessTools.Field(typeof(AudioMgrEx), "<musicUrl>k__BackingField");

        internal static void Initialize(ConfigFile config)
        {
            quietSetting = config.Bind("Editor", "MuteBackgroundMusic", false,
                "关闭编辑器环境 BGM；剧情预览仍可播放音乐，不改变游戏全局音量。");
            Policy.Quiet = quietSetting.Value;
        }

        internal static void Enter(object view)
        {
            bool wasActive = Policy.Active;
            Policy.Enter(view);
            Refresh();
            if (!wasActive && source != null && !source.isPlaying) RestoreAmbient();
        }

        internal static void Leave(object view)
        {
            bool wasActive = Policy.Active;
            bool interruptedLoad = pendingLoad;
            Policy.Leave(view);
            Refresh();
            if (wasActive && !Policy.Active && interruptedLoad)
            {
                pendingLoad = false;
                RestoreAmbient();
            }
        }

        internal static void BeginPreview(PreviewTalkView view)
        {
            Policy.BeginPreview(view);
            pendingPreviewUrl = null;
            Refresh();
        }

        internal static void EndPreview(PreviewTalkView view)
        {
            if (!Policy.EndPreview(view)) return;
            pendingPreviewUrl = null;
            Refresh();
        }

        internal static void Toggle()
        {
            Policy.Quiet = !Policy.Quiet;
            try { if (quietSetting != null) quietSetting.Value = Policy.Quiet; }
            catch (Exception e) { Plugin.Log?.LogWarning("[EditorAudio.Preference] " + e.Message); }
            Refresh();
        }

        private static void RestoreAmbient()
        {
            MusicUrl?.SetValue(null, null);
            AudioMgrEx.PlayBgm(true);
        }

        internal static void ApplyMute()
        {
            if (!Policy.Active)
            {
                if (source != null) source.mute = priorMute || externalMute;
                source = null;
                return;
            }
            if (source == null && AudioMgr.Ins != null)
            {
                try
                {
                    source = AudioMgr.Ins.GetChannel(AudioMgrEx.CHANNEL_BGM)?.source;
                    if (source != null)
                    {
                        priorMute = source.mute;
                        externalMute = false;
                    }
                }
                catch (NullReferenceException) { return; } // AudioMgr.Start 尚未完成。
            }
            if (source != null)
                source.mute = priorMute || externalMute || Policy.MuteMusic;
        }

        internal static void Refresh()
        {
            ApplyMute();
            Changed?.Invoke();
        }

        internal static void ObserveGlobalMute(bool mute)
        {
            if (source == null) return;
            // 原版全局静音/解除静音是新的外部意图，不能被旧快照覆盖。
            priorMute = false;
            externalMute = mute;
            ApplyMute();
        }

        internal static void Stop(int channel)
        {
            if (!Policy.Active || (channel != -1 && channel != AudioMgrEx.CHANNEL_BGM)) return;
            Policy.Invalidate();
            pendingLoad = false;
            pendingPreviewUrl = null;
            Refresh();
        }

        internal static void PlayPreviewMusic(int id, Dictionary<int, AudioCfg> map)
        {
            if (map == null || !map.TryGetValue(id, out AudioCfg cfg)
                || cfg == null || string.IsNullOrEmpty(cfg.url)) return;
            if (pendingPreviewUrl == cfg.url) return;
            // 首次预览可以与环境 BGM 使用同一资源，不能被原版 URL 去重吞掉。
            if (!Policy.PreviewMusic) MusicUrl?.SetValue(null, null);
            bool previous = previewRequest;
            previewRequest = true;
            try { AudioMgrEx.PlayEvtBgm(id, true, null, map); }
            finally { previewRequest = previous; }
        }

        internal static bool InterceptMusic(
            int channel, string url, float volume, bool loop, Action callback, float fade)
        {
            if (channel != AudioMgrEx.CHANNEL_BGM) return true;
            bool editorRequest = Policy.Active;
            bool fromPreview = previewRequest;
            if (string.IsNullOrEmpty(url)) return false;
            long token = Policy.Generation;
            if (editorRequest && !Policy.Request(fromPreview, out token)) return false;
            if (editorRequest) pendingLoad = true;
            AudioMgr owner = AudioMgr.Ins;
            if (fromPreview) pendingPreviewUrl = AudioMgrEx.musicUrl;
            ResMgr.LoadAudioAsync(url, clip =>
            {
                // 在编辑器退出或新曲目请求之后抵达的回调不能重新发声。
                if (owner == null || owner != AudioMgr.Ins || Policy.Generation != token
                    || (editorRequest && !Policy.Active))
                {
                    if (clip != null) ResMgr.Recycle(clip);
                    return;
                }
                pendingLoad = false;
                pendingPreviewUrl = null;
                if (clip == null)
                {
                    if (editorRequest) Policy.Complete(token, fromPreview, false);
                    MusicUrl?.SetValue(null, null);
                    Refresh();
                    Plugin.Log?.LogWarning("[EditorAudio] 找不到预览/环境音乐：" + url);
                    return;
                }
                try
                {
                    // 旧曲保持静音直到新 clip 真正接管 source，避免加载间隙串音。
                    owner.GetChannel(channel).Play(clip, volume, loop, callback, fade);
                    if (editorRequest) Policy.Complete(token, fromPreview, true);
                    Refresh();
                    EventMgr.Send(7);
                }
                catch (Exception e)
                {
                    Policy.Complete(token, fromPreview, false);
                    Refresh();
                    Plugin.Log?.LogError("[EditorAudio.Play] " + e);
                }
            });
            return false;
        }
    }

    [HarmonyPatch]
    internal static class EditorAmbientResumePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (MethodInfo method in typeof(AudioMgrEx).GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                if (method.Name == "PlayBgm") yield return method;
        }
        private static bool Prefix() { return !EditorAudioRuntime.Policy.InPreview; }
    }

    [HarmonyPatch(typeof(AudioMgr), nameof(AudioMgr.PlayMusic))]
    internal static class EditorMusicLoadPatch
    {
        private static bool Prefix(int _channel, string _url, float _volumeScale,
            bool _isLoop, Action _callback, float _fadeTime)
        {
            return EditorAudioRuntime.InterceptMusic(
                _channel, _url, _volumeScale, _isLoop, _callback, _fadeTime);
        }
    }

    [HarmonyPatch(typeof(AudioMgr), nameof(AudioMgr.Stop))]
    internal static class EditorMusicStopPatch
    {
        private static void Prefix(int _channel) { EditorAudioRuntime.Stop(_channel); }
    }

    [HarmonyPatch(typeof(AudioMgr), nameof(AudioMgr.MuteSound))]
    internal static class EditorGlobalMutePatch
    {
        private static void Postfix(bool _mute) { EditorAudioRuntime.ObserveGlobalMute(_mute); }
    }

    [HarmonyPatch(typeof(PreviewTalkView), "PlayAudio")]
    internal static class EditorPreviewMusicPatch
    {
        private static bool Prefix(PreviewTalkView __instance)
        {
            if (!EditorAudioRuntime.Policy.InPreview) return true;
            var t = Traverse.Create(__instance);
            TalkCfg talk = t.Field("cfg").GetValue<TalkCfg>();
            var map = t.Field("audioCfgMap").GetValue<Dictionary<int, AudioCfg>>();
            if (talk == null || map == null || !map.TryGetValue(talk.audio, out AudioCfg cfg)
                || cfg == null || cfg.type != 1) return true;
            EditorAudioRuntime.PlayPreviewMusic(talk.audio, map);
            __instance.endBgmChange = false;
            t.Field("playEvtGroupBgm").SetValue(false);
            return false; // 原版音效分支与已有语音 Postfix 均保留。
        }
    }
}
