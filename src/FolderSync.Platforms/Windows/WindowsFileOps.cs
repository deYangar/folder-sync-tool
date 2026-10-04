using System;
using System.Runtime.Versioning;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace FolderSync.Platforms.Windows
{
    /// <summary>Windows 文件操作：CopyFileEx 复制+进度、GetFileAttributesEx/SetFileTime 保 ctime/mtime、
    /// SHFileOperation 回收站删除（攒批）、WNetGetConnection 网络盘识别。自 Executor 平移（行为零变化）。</summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsFileOps : Core.Platform.IFileOps
    {
        private delegate int CopyProgressRoutine(long totalFileSize, long totalBytesTransferred,
            long streamSize, long streamBytesTransferred, uint dwStreamNumber, uint dwCallbackReason,
            IntPtr hSourceFile, IntPtr hDestinationFile, IntPtr lpData);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CopyFileEx(string lpExistingFileName, string lpNewFileName,
            CopyProgressRoutine? lpProgressRoutine, IntPtr lpData, ref bool pbCancel, uint dwCopyFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

        private const int NO_ERROR = 0, ERROR_MORE_DATA = 234;

        private const uint FO_DELETE = 3;
        private const ushort FOF_ALLOWUNDO = 0x40;
        private const ushort FOF_NOCONFIRMATION = 0x10;
        private const ushort FOF_SILENT = 0x4;
        private const ushort FOF_NOERRORUI = 0x400;

        // ---------- 时间戳复制（原生 API） ----------
        // ctime（创建时间）必须显式复制：CopyFileEx 落地的 tmp 创建时间=复制时刻，rename 不改 ctime，
        // 不补设的话源文件的创建时间在目标侧永远丢失。原生 GetFileAttributesEx/SetFileTime 直写
        // \\?\ 前缀路径（.NET 8 的 File.* 对 \\?\ 长路径同样可用，这里用原生是与执行器其余长路径惯例统一）；
        // mtime 由 CopyFileEx 原生复制兜底，此处一并设置

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME { public uint Low, High; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WIN32_FILE_ATTRIBUTE_DATA
        {
            public uint Attributes;
            public FILETIME Creation, LastAccess, LastWrite;
            public uint SizeHigh, SizeLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileAttributesEx(string fileName, int infoLevelId,
            out WIN32_FILE_ATTRIBUTE_DATA data);

        // 注意：返回值 SafeFileHandle 绝不能挂 [return: MarshalAs(Bool)]——net8.0-windows TFM 容忍
        // 该非法组合，纯 net8.0 TFM 的 marshaler 直接抛 "SafeHandles must not have a MarshalAs
        // attribute set"（CopyTimestamps 静默失败，DeltaTransfer 的 tmp mtime 全丢——2026-09-16 实测）
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string fileName,
            uint desiredAccess, uint shareMode, IntPtr securityAttrs, uint creationDisposition,
            uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileTime(Microsoft.Win32.SafeHandles.SafeFileHandle handle,
            ref FILETIME creation, IntPtr lastAccess, ref FILETIME lastWrite);

        private const uint FILE_WRITE_ATTRIBUTES = 0x100;

        private readonly object _shGate = new();   // SHFileOperation 官方不保证线程安全：D2 并行 worker 下串行

        public bool CopyFile(string src, string dst, Func<long, long, bool>? onProgress, out int win32Error)
        {
            bool cancelFlag = false;   // 必须显式初始化 false，垃圾值会让 CopyFileEx 直接中止
            bool ok = CopyFileEx(src, dst,
                onProgress == null ? null : (total, transferred, _, _, _, _, _, _, _) =>
                {
                    // W-1：回调体绝不许异常穿越 native 边界——反向 P/Invoke 帧上抛异常默认终止进程
                    //（IFileOps 契约未约定 onProgress 不得抛；Bcl 实现收敛成返回 false，这里对齐）
                    try { return onProgress(total, transferred) ? 0 : 1; }   // PROGRESS_CONTINUE=0, PROGRESS_CANCEL=1（WinBase.h，别记反）
                    catch { return 1; }
                },
                IntPtr.Zero, ref cancelFlag, 0);
            if (ok) { win32Error = 0; return true; }
            win32Error = cancelFlag ? 1235 : Marshal.GetLastWin32Error();
            return false;
        }

        public void CopyTimestamps(string src, string dst)
        {
            try
            {
                if (!GetFileAttributesEx(src, 0, out var d)) return;
                using var h = CreateFileW(dst, FILE_WRITE_ATTRIBUTES, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h.IsInvalid) return;
                var ct = d.Creation;
                var wt = d.LastWrite;
                SetFileTime(h, ref ct, IntPtr.Zero, ref wt);
            }
            catch { }
        }

        public void RecycleDeleteBatch(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0) return;
            try
            {
                var op = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    // 多路径：每个 \0 结尾 + 整体再补 \0
                    pFrom = string.Join("", paths.Select(p => p + "\0")) + "\0",
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
                };
                lock (_shGate) SHFileOperation(ref op);
            }
            catch { /* 批调用异常不致命，调用方逐个核对兜底 */ }
        }

        public void RecycleDelete(string path, bool isDirectory)
        {
            lock (_shGate)
            {
                var op = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    pFrom = path + "\0",   // 双 null 结尾
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
                };
                var rc = SHFileOperation(ref op);
                var stillThere = isDirectory ? Directory.Exists(path) : File.Exists(path);
                if (stillThere && rc != 0 && !op.fAnyOperationsAborted)
                    throw new IOException($"删除失败 (SHFileOperation {rc}): {path}");
            }
        }

        public bool IsNetworkPath(string path)
        {
            // W-3：先剥 \\?\ 设备前缀——FsPath.LongPath 的产物形态之一正是 \\?\UNC\server\share，
            // 不剥的话连前缀里的 \\ 都判不到（旧代码对 \\?\ 显式 return false），绕过
            // 「网络路径无回收站」告警链后在 UNC 侧静默永久删除
            if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) path = "\\" + path[7..];
            else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
            // 网络路径判定：\\ 前缀直接判；映射盘符（Z:\）经 WNetGetConnection 解析真身——
            // 映射盘没有回收站，FOF_ALLOWUNDO 会静默永久删除，必须按网络路径同等处理
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
            {
                try
                {
                    var sb = new StringBuilder(512);
                    int len = sb.Capacity;
                    var rc = WNetGetConnection(path[..2], sb, ref len);
                    if (rc == ERROR_MORE_DATA)
                    {
                        sb.Capacity = len;
                        rc = WNetGetConnection(path[..2], sb, ref len);
                    }
                    return rc == NO_ERROR;   // 0=该盘符当前映射到网络共享；本地盘/已断开映射不误判
                }
                catch { return false; }
            }
            return false;
        }
    }
}
