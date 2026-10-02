using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Bootstrap;
using HarmonyLib;
using Sdk;
using StudentAgeHarness;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace StudentAgeEditorPlus.HarnessScenarios
{
    /// <summary>
    /// 两个场景共用的工具：编号断言、反射访问 EditorPlus 的 internal 类型、屏幕矩形、输入框按键事件和临时作品。
    /// EditorPlus 的类型都是 internal，这里和旧的游戏内测试插件一样用反射，不需要改动插件本身。
    /// </summary>
    internal sealed class EditorPlusDriver
    {
        internal const string PluginGuid = "com.studentage.editorplus";
        internal const string AssemblyName = "StudentAgeEditorPlus";
        internal const string HarmonyId = "com.studentage.editorplus.harnessscenarios";

        private readonly HarnessContext ctx;
        private readonly List<string> passed = new List<string>();

        internal EditorPlusDriver(HarnessContext ctx)
        {
            this.ctx = ctx;
        }

        // ---- 编号断言 ----

        internal int Checks => passed.Count;

        internal IList<string> Passed => passed;

        /// <summary>
        /// 旧测试里的一条检查。条件求值抛异常也算失败，并在消息里写明是第几条、实际是什么。
        /// </summary>
        internal void Check(string description, Func<bool> condition, Func<string> actual = null)
        {
            int number = passed.Count + 1;
            bool ok;
            string error = null;
            try { ok = condition(); }
            catch (Exception ex)
            {
                ok = false;
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                error = inner.GetType().Name + "：" + inner.Message;
            }
            if (!ok)
            {
                string detail = error ?? SafeActual(actual);
                ctx.Fail("检查 #" + number + " 未通过：" + description + (string.IsNullOrEmpty(detail) ? "" : "（" + detail + "）"));
            }
            passed.Add("#" + number + " " + description);
        }

        private static string SafeActual(Func<string> actual)
        {
            if (actual == null) return null;
            try { return "实际：" + actual(); }
            catch (Exception ex) { return "读取实际值失败：" + ex.Message; }
        }

        // ---- EditorPlus 程序集 ----

        internal static Assembly EditorPlus =>
            AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AssemblyName)
            ?? throw new InvalidOperationException("EditorPlus 程序集没有加载。");

        internal static Type PatchType(string name) =>
            EditorPlus.GetType("StudentAgeEditorPlus.Patches." + name, false)
            ?? throw new InvalidOperationException("EditorPlus 里找不到类型 StudentAgeEditorPlus.Patches." + name + "，插件结构可能变了。");

        internal static string AssemblyVersion => EditorPlus.GetName().Version.ToString();

        internal static string PluginVersion =>
            Chainloader.PluginInfos.TryGetValue(PluginGuid, out var info) ? info.Metadata.Version.ToString() : null;

        // ---- 反射 ----

        internal static object Call(object target, string method, params object[] args)
        {
            MethodInfo info = AccessTools.Method(target.GetType(), method)
                ?? throw new InvalidOperationException(target.GetType().FullName + " 没有方法 " + method + "。");
            return Invoke(info, target, args);
        }

        internal static object CallStatic(Type type, string method, params object[] args)
        {
            MethodInfo info = AccessTools.Method(type, method)
                ?? throw new InvalidOperationException(type.FullName + " 没有方法 " + method + "。");
            return Invoke(info, null, args);
        }

        internal static object Invoke(MethodInfo info, object target, object[] args)
        {
            try { return info.Invoke(target, args); }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw new InvalidOperationException(info.DeclaringType?.Name + "." + info.Name + " 抛出 " +
                    ex.InnerException.GetType().Name + "：" + ex.InnerException.Message, ex.InnerException);
            }
        }

        internal static T Field<T>(object target, string name) => Field<T>(target.GetType(), target, name);

        internal static T Field<T>(Type type, object target, string name)
        {
            FieldInfo info = AccessTools.Field(type, name)
                ?? throw new InvalidOperationException(type.FullName + " 没有字段 " + name + "。");
            return (T)info.GetValue(target);
        }

        internal static T Property<T>(object target, string name)
        {
            PropertyInfo info = AccessTools.Property(target.GetType(), name)
                ?? throw new InvalidOperationException(target.GetType().FullName + " 没有属性 " + name + "。");
            return (T)info.GetValue(target, null);
        }

        // ---- 几何 ----

        internal static Rect ScreenRect(Transform target)
        {
            var corners = new Vector3[4];
            ((RectTransform)target).GetWorldCorners(corners);
            Canvas canvas = target.GetComponentInParent<Canvas>();
            Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            Vector2 a = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            Vector2 b = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
            return Rect.MinMaxRect(a.x, a.y, b.x, b.y);
        }

        internal static object Describe(Rect rect) => new
        {
            x = Round(rect.x),
            y = Round(rect.y),
            width = Round(rect.width),
            height = Round(rect.height)
        };

        internal static string Format(Rect rect) =>
            string.Format(CultureInfo.InvariantCulture, "(x={0:F2}, y={1:F2}, w={2:F2}, h={3:F2})", rect.x, rect.y, rect.width, rect.height);

        private static double Round(float value) => Math.Round(value, 2);

        // ---- 输入 ----

        /// <summary>
        /// 直接把 IMGUI 键盘事件交给输入框。TMP_InputField/InputField 读的是 IMGUI Event，
        /// InputSystem 的虚拟按键到不了这条路径，所以沿用旧测试的 ProcessEvent。
        /// </summary>
        internal static void Key(TMP_InputField field, KeyCode key, EventModifiers modifiers = EventModifiers.None, char character = '\0') =>
            field.ProcessEvent(new Event { type = EventType.KeyDown, keyCode = key, modifiers = modifiers, character = character });

        internal static void Key(InputField field, KeyCode key, EventModifiers modifiers = EventModifiers.None, char character = '\0') =>
            field.ProcessEvent(new Event { type = EventType.KeyDown, keyCode = key, modifiers = modifiers, character = character });

        /// <summary>取消当前选中的控件，让聚焦的输入框提交内容（等同于点到空白处）。</summary>
        internal static void Commit()
        {
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        }

        /// <summary>等目标可交互、没有被挡住，再发送一次真实的指针点击（射线命中必须是目标自己或其子节点）。</summary>
        internal IEnumerator Click(Component target, float timeoutSec = 5f)
        {
            if (target == null) throw new InvalidOperationException("点击目标不存在。");
            return ctx.Ui.ClickWhenReachable(target, timeoutSec);
        }

        internal static IEnumerator Pause(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        /// <summary>轮询到条件成立或超时；超时不抛异常，由随后的检查给出失败原因。</summary>
        internal static IEnumerator WaitFor(Func<bool> condition, float timeoutSec)
        {
            float end = Time.realtimeSinceStartup + timeoutSec;
            while (Time.realtimeSinceStartup < end)
            {
                bool done;
                try { done = condition(); }
                catch { done = false; }
                if (done) yield break;
                yield return null;
            }
        }

        // ---- 夹具 ----

        /// <summary>在隔离的 profile/Mods 下建临时作品（运行结束自动删除），并建好当前语言的 Cfgs 目录。</summary>
        internal string CreateProject(string name, out string cfgDirectory)
        {
            string project = ctx.Fixtures.CreateLocalMod(name);
            cfgDirectory = Path.Combine(Path.Combine(project, "Cfgs"), LocalizationMgr.Lang);
            Directory.CreateDirectory(cfgDirectory);
            return project;
        }

        /// <summary>把证据文本写到本次运行的 fixtures 目录，返回相对运行目录的路径。</summary>
        internal string WriteEvidence(string folder, string fileName, IEnumerable<string> lines)
        {
            string directory = ctx.Fixtures.CreateDirectory(folder);
            string path = Path.Combine(directory, fileName);
            File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
            return Path.Combine(Path.Combine("fixtures", folder), fileName).Replace('\\', '/');
        }
    }
}
