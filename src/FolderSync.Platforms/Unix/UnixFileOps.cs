using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using FolderSync.Core;
using FolderSync.Core.Platform;

namespace FolderSync.Platforms.Unix
{
    /// <summary>Unix 文件操作：分块流复制+进度、futimens 保 mtime（macOS 另设 birthtime）、
    /// XDG Trash（Linux）/ ~/.Trash（macOS）自实现（无桌面依赖）、网络盘一期不识别。
    /// 复制错误统一 errno→Win32 映射（Errno.ToWin32），磁盘满(ENOSPC→112)保持引擎熔断语义。</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public sealed class UnixFileOps : IFileOps
    {
        public bool CopyFile(string src, string dst, Func<long, long, bool>? onProgress, out int win32Error)
        {
            try
            {
                using var srcFs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var dstFs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
                var total = srcFs.Length;
                var buf = new byte[4 * 1024 * 1024];
                long done = 0;
                int n;
                while ((n = srcFs.Read(buf, 0, buf.Length)) > 0)
                {
                    dstFs.Write(buf, 0, n);
                    done += n;
                    if (onProgress != null && !onProgress(total, done))
                    {
                        win32Error = 1235;   // ERROR_REQUEST_ABORTED（用户中止）
                        return false;
                    }
                }
                dstFs.Flush(flushToDisk: false);
                CopyUnixMode(src, dst);
                win32Error = 0;
                return true;
            }
            catch (UnauthorizedAccessException) { win32Error = 5; return false; }
            catch (FileNotFoundException) { win32Error = 2; return false; }
            catch (DirectoryNotFoundException) { win32Error = 3; return false; }
            catch (IOException io) { win32Error = Errno.ToWin32(io); return false; }
            catch (Exception) { win32Error = 1; return false; }
        }

        /// <summary>权限位跟随源（CopyFileEx 原样带 ACL 的近似；可执行位等语义在此保留）。尽力而为。</summary>
        private static void CopyUnixMode(string src, string dst)
        {
            try { File.SetUnixFileMode(dst, File.GetUnixFileMode(src)); }
            catch { /* 特殊文件系统无 mode 概念：静默 */ }
        }

        public void CopyTimestamps(string src, string dst)
        {
            try
            {
                // mtime：.NET 在 Unix 内部走 futimens；ctime（Linux crtime）不可移植已放弃（方案 §6）
                var mtime = File.GetLastWriteTimeUtc(src);
                File.SetLastWriteTimeUtc(dst, mtime);
                if (OperatingSystem.IsMacOS())
                    SetMacBirthtime(dst, File.GetCreationTimeUtc(src));
            }
            catch { /* 尽力而为：mtime 另有复制通道兜底 */ }
        }

        // ---------- macOS birthtime（APFS/HFS+ 创建时间）：setattrlist(ATTR_CMN_CRTIME) ----------

        [StructLayout(LayoutKind.Sequential)]
        private struct AttrList
        {
            public ushort BitmapCount;   // ATTR_BIT_MAP_COUNT = 5
            public ushort Reserved;
            public uint CommonAttr;      // ATTR_CMN_CRTIME = 0x00000100
            public uint VolAttr, DirAttr, FileAttr, ForkAttr;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Timespec
        {
            public long Sec, Nsec;
        }

        [DllImport("/usr/lib/libSystem.dylib", SetLastError = true)]
        private static extern int setattrlist(string path, ref AttrList attrList,
            ref Timespec attrBuf, nuint attrBufSize, uint options);

        private const uint ATTR_CMN_CRTIME = 0x00000100;

        /// <summary>macOS 创建时间显式设置（等价 Windows SetFileTime(ctime) 的角色）。</summary>
        private static void SetMacBirthtime(string path, DateTime utc)
        {
            try
            {
                var ts = utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var buf = new Timespec { Sec = (long)ts.TotalSeconds, Nsec = (long)(ts.Ticks % TimeSpan.TicksPerSecond) * 100 };
                var al = new AttrList { BitmapCount = 5, CommonAttr = ATTR_CMN_CRTIME };
                setattrlist(path, ref al, ref buf, (nuint)System.Runtime.InteropServices.Marshal.SizeOf<Timespec>(), 0);
            }
            catch { /* 尽力而为 */ }
        }

        // ---------- 回收站 ----------

        /// <summary>回收站根：Linux XDG 规范 ~/.local/share/Trash（$XDG_DATA_HOME 覆盖）；
        /// macOS ~/.Trash。topdir 挂载点回收站（.Trash-&lt;uid&gt;）一期不做（方案 §6 降级矩阵）。</summary>
        private static string TrashHome() =>
            OperatingSystem.IsMacOS()
                ? Path.Combine(Home(), ".Trash")
                : Path.Combine(XdgDataHome(), "Trash");

        private static string Home() =>
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        private static string XdgDataHome()
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(xdg)) return xdg;
            return Path.Combine(Home(), ".local", "share");
        }

        public void RecycleDeleteBatch(IReadOnlyList<string> paths)
        {
            // XDG Trash 逐条 mv 本来就快（无 SHFileOperation 的 shell 初始化开销），无需攒批优化
            foreach (var p in paths)
                try { RecycleDelete(p, isDirectory: false); } catch { /* 调用方 Exists 核对兜底 */ }
        }

        public void RecycleDelete(string path, bool isDirectory)
        {
            var trash = TrashHome();
            var filesDir = OperatingSystem.IsMacOS() ? trash : Path.Combine(trash, "files");
            Directory.CreateDirectory(filesDir);

            var name = UniqueTrashName(filesDir, Path.GetFileName(path));
            var dest = Path.Combine(filesDir, name);

            try
            {
                // 目录必须 Directory.Move：File.Move 只吃文件（.NET 对目录路径必抛 IOException，
                // 曾被 Linux 的 IOException 兜底分支意外救活，macOS 分支无兜底直接失败）
                if (isDirectory || Directory.Exists(path))
                    Directory.Move(path, dest);
                else
                    // 注意：.NET 8 的 Unix File.Move 跨设备并不自动 copy+delete（Windows 才有该回退），
                    // EXDEV 会以 IOException 冒出——靠下面的 catch 兜底才有跨卷回收站，别拆
                    File.Move(path, dest);
            }
            catch (IOException)
            {
                // 跨设备残留（EXDEV）：手动 copy+delete（含目录树）。
                // U-1 体积闸：外置卷删大目录=整树拷进家目录回收站，中途 ENOSPC 反复重拷会持续
                // 填满系统盘殃及全系统——超阈值不再冒充回收站，抛错让上层按条目失败处理（文件留原位）
                if (isDirectory || Directory.Exists(path))
                {
                    var size = TreeSize(path);
                    if (size > CrossVolumeTrashMaxBytes)
                        throw new IOException(
                            $"跨卷目录 {Executor.FormatSize(size)} 超过回收站跨卷上限（{Executor.FormatSize(CrossVolumeTrashMaxBytes)}），" +
                            $"已拒绝拷入家目录回收站（防填满系统盘）；文件保留在原位，请手动处理或改用永久删除");
                    CopyDirToTrash(path, dest);
                }
                else { File.Copy(path, dest); File.Delete(path); }
            }
            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException($"回收站删除失败（条目仍在原位）: {path}");

            // XDG .trashinfo 元数据（恢复工具按它找原路径）；macOS Finder 不需要
            if (!OperatingSystem.IsMacOS())
            {
                var infoDir = Path.Combine(trash, "info");
                Directory.CreateDirectory(infoDir);
                var absPath = Path.GetFullPath(path);
                // 按 XDG 实践保留 '/' 分隔（gvfs/KDE 不做反转义，整串 EscapeDataString 会把 '/' 编成 %2F）
                var escaped = string.Join("/", absPath.Split('/').Select(Uri.EscapeDataString));
                File.WriteAllText(Path.Combine(infoDir, name + ".trashinfo"),
                    $"[Trash Info]\nPath={escaped}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n");
            }
        }

        /// <summary>重名防御：foo.txt → foo.2.txt → foo.3.txt…（同秒批量删除同名条目）。
        /// 存在性探测必须 lstat 语义（参考#8）：File/Directory.Exists 跟随符号链接，
        /// 悬空链接探测不到 → 误判无重名 → rename 静默顶掉回收站里的悬空链接。</summary>
        private static string UniqueTrashName(string dir, string name)
        {
            if (!EntryExists(Path.Combine(dir, name))) return name;
            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);
            for (int i = 2; ; i++)
            {
                var candidate = $"{stem}.{i}{ext}";
                if (!EntryExists(Path.Combine(dir, candidate))) return candidate;
            }
        }

        /// <summary>lstat 语义存在性：GetAttributes 不跟随链接，悬空 symlink 也返回属性（ReparsePoint）。</summary>
        private static bool EntryExists(string p)
        {
            try { File.GetAttributes(p); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return true; }   // 在但读不了：按存在处理（保守防顶替）
        }

        /// <summary>跨卷回收站的体积上限：超过则拒绝拷入家目录（U-1，防填满系统盘）。</summary>
        private static readonly long CrossVolumeTrashMaxBytes = 2L * 1024 * 1024 * 1024;   // 2 GiB

        /// <summary>目录树体积（符号链接不跟进不计入——与 CopyDirToTrash 的 lstat 语义一致，防链接环）。</summary>
        private static long TreeSize(string dir)
        {
            long total = 0;
            var opt = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint   // 链接（含指向目录的链接）整条跳过
            };
            foreach (var fi in new DirectoryInfo(dir).EnumerateFiles("*", opt))
                total += fi.Length;
            return total;
        }

        /// <summary>S-6：跨卷目录进回收站按 lstat 语义复制——符号链接绝不跟进目标：
        /// 悬空链接 File.Copy 抛 FileNotFoundException 会炸整次删除、链接→目录被当真目录递归
        /// 把外部整棵树拷进 ~/.Trash、链接环更是无限递归 StackOverflow（不可捕获）直接杀进程。
        /// 链接原样重建（悬空也可重建——ResolveSymbolicLink 只解析路径不要求目标存在），
        /// 与 Windows 侧「按链接删除」语义对齐。</summary>
        private static void CopyDirToTrash(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var entry in Directory.EnumerateFileSystemEntries(src))
            {
                var attr = File.GetAttributes(entry);   // Unix 上 lstat：链接本身返回 ReparsePoint
                var target = Path.Combine(dst, Path.GetFileName(entry));
                if (attr.HasFlag(FileAttributes.ReparsePoint))
                {
                    // ResolveLinkTarget(returnFinalTarget:false) 不要求目标存在，悬空链接同样可解析
                    var linkTarget = File.ResolveLinkTarget(entry, returnFinalTarget: false);
                    if (linkTarget != null)
                        File.CreateSymbolicLink(target, linkTarget.FullName);
                    else if (attr.HasFlag(FileAttributes.Directory))
                        Directory.CreateDirectory(target);   // 解析失败的目录形链接：空目录占位，删除流程可继续
                    continue;
                }
                if (attr.HasFlag(FileAttributes.Directory))
                    CopyDirToTrash(entry, target);
                else
                    File.Copy(entry, target);
            }
            // src 本身是链接时不该走到这里（外层 RecycleDelete 的 Directory.Move 分支按链接 mv）；
            // 真目录递归删：.NET 对子目录中的符号链接只删链接本身不跟进（与 lstat 复制语义对称）
            Directory.Delete(src, recursive: true);
        }

        public bool IsNetworkPath(string path) => false;   // 一期不识别（方案 §6）：按普通目录同步，错误重试链路兜底
    }
}
