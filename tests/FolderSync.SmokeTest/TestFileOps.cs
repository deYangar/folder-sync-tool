using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using FolderSync.Core.Platform;

namespace FolderSync.SmokeTest
{
    /// <summary>
    /// FileOps 层测试注入——跨平台统一的「文件占用」模拟。
    /// Windows 曾用 FileShare.None/msvcrt 独占句柄制造 win32 32，Unix 无强制共享锁、手法全部失效；
    /// 统一改在 IFileOps 层注入：对指定路径按脚本失败/阻塞，其余调用原样转发，三平台一份代码。
    /// 测试语义 = 引擎对 FileOps 错误的反应链（队列/退避/明细/取消），不再依赖 OS 锁行为。
    /// 经反射替换 Platform._impl（Init 幂等拒绝二次装配，测试进程内反射换装、Dispose 恢复）。
    /// </summary>
    internal static class TestFileOps
    {
        /// <summary>当前 Platform.FileOps 是否为注入的脚本实现（诊断用）。</summary>
        public static bool IsInjected =>
            Platform.FileOps.GetType().Name.Contains("Scripted", StringComparison.Ordinal);

        internal sealed class Script
        {
            public string Path = "";
            public int RemainingFails;          // CopyFile 前N次返回 Win32Error
            public int Win32Error = 32;
            public readonly ManualResetEventSlim BlockGate = new(false);   // 初始未置位：Block 场景 CopyFile 挂住直到 Release
            public bool BlockCopy;              // CopyFile 挂起直到 Release()
            public bool FailRecycle;            // RecycleDelete 恒失败（模拟占用删不掉）
            // Executor 侧 CopyFile 传 LongPath 前缀（\\?\C:\...），测试传裸路径——归一后比较
            public bool Matches(string p) =>
                string.Equals(TrimLongPath(p), TrimLongPath(Path), StringComparison.OrdinalIgnoreCase);
            private static string TrimLongPath(string p) =>
                p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p[4..] : p;
        }

        /// <summary>注入句柄：Dispose 恢复真实平台实现。</summary>
        internal sealed class Handle : IDisposable
        {
            private FieldInfo? _field;
            private IPlatformImpl? _orig;

            public Script State = null!;

            public void SetBacking(FieldInfo field, IPlatformImpl orig) { _field = field; _orig = orig; }

            /// <summary>解除阻塞/停止失败（CopyFile 恢复真实转发）。</summary>
            public void Release()
            {
                State.BlockGate.Set();
                State.RemainingFails = 0;
            }

            public void Dispose()
            {
                Release();
                _field?.SetValue(null, _orig);
            }
        }

        private static Handle Install(Script s)
        {
            var field = typeof(Platform).GetField("_impl", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("Platform._impl 反射失败（字段改名？）");
            var orig = (IPlatformImpl?)field.GetValue(null)
                ?? throw new InvalidOperationException("Platform._impl 未初始化（测试入口应先 Platform.Init/默认 Bcl）");
            var h = new Handle { State = s };
            h.SetBacking(field, orig);
            field.SetValue(null, new DecoratedImpl(orig, new ScriptedFileOps(orig.FileOps, s)));
            return h;
        }

        /// <summary>对 path 的 CopyFile 前 failTimes 次返回 win32Error（默认 32=共享冲突，属瞬时错误进重试队列）。</summary>
        public static Handle FailCopy(string path, int failTimes, int win32Error = 32)
            => Install(new Script { Path = path, RemainingFails = failTimes, Win32Error = win32Error });

        /// <summary>对 path 的 CopyFile 阻塞直到 handle.Release()（模拟持续占用，制造稳定忙窗口）。</summary>
        public static Handle BlockCopy(string path)
            => Install(new Script { Path = path, BlockCopy = true });

        /// <summary>对 path 的回收站删除恒失败（模拟占用删不掉；条目核对兜底路径按失败计）。</summary>
        public static Handle FailRecycle(string path)
            => Install(new Script { Path = path, FailRecycle = true });

        private sealed class ScriptedFileOps : IFileOps
        {
            private readonly IFileOps _inner;
            private readonly Script _s;

            public ScriptedFileOps(IFileOps inner, Script s) { _inner = inner; _s = s; }

            public bool CopyFile(string src, string dst, Func<long, long, bool>? onProgress, out int win32Error)
            {
                if (_s.Matches(src) || _s.Matches(dst))
                {
                    if (_s.BlockCopy)
                    {
                        _s.BlockGate.Wait();   // Release 放行：本次真实复制，之后不再阻塞
                        _s.BlockCopy = false;
                        return _inner.CopyFile(src, dst, onProgress, out win32Error);
                    }
                    if (_s.RemainingFails > 0)
                    {
                        _s.RemainingFails--;
                        win32Error = _s.Win32Error;
                        return false;
                    }
                }
                return _inner.CopyFile(src, dst, onProgress, out win32Error);
            }

            public void CopyTimestamps(string src, string dst) => _inner.CopyTimestamps(src, dst);

            public void RecycleDeleteBatch(IReadOnlyList<string> paths)
            {
                // 批语义「实现内不抛，调用方 Exists 核对」：命中脚本的路径跳过不删，其余转发；
                // 调用方核对时被跳过条目仍在原位 → 按失败计
                var pass = new List<string>(paths.Count);
                foreach (var p in paths)
                    if (!_s.FailRecycle || !_s.Matches(p)) pass.Add(p);
                if (pass.Count > 0) _inner.RecycleDeleteBatch(pass);
            }

            public void RecycleDelete(string path, bool isDirectory)
            {
                if (_s.FailRecycle && _s.Matches(path))
                    throw new IOException($"TestFileOps: 模拟占用（win32 {_s.Win32Error}）");
                _inner.RecycleDelete(path, isDirectory);
            }

            public bool IsNetworkPath(string path) => _inner.IsNetworkPath(path);
        }

        private sealed class DecoratedImpl : IPlatformImpl
        {
            private readonly IPlatformImpl _orig;
            private readonly IFileOps _fileOps;
            public DecoratedImpl(IPlatformImpl orig, IFileOps fileOps) { _orig = orig; _fileOps = fileOps; }
            public IFileOps FileOps => _fileOps;
            public IRealtimeWatcherFactory Watchers => _orig.Watchers;
            public IAutostart? Autostart => _orig.Autostart;
            public IShellOps Shell => _orig.Shell;
            public IIconProvider? Icons => _orig.Icons;
        }
    }
}
