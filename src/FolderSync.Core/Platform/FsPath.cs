using System;
using System.IO;

namespace FolderSync.Core.Platform
{
    /// <summary>长路径前缀 helper：Windows 加 \\?\ 设备路径前缀（超 MAX_PATH 与原生 API 直写统一通道），
    /// Unix 透传（PATH_MAX 由内核/ libc 处理，无前缀概念）。
    /// 三处历史重复实现（Executor/VersionStore/BaselineStore 各一份）统一收口于此。</summary>
    public static class FsPath
    {
        public static string LongPath(string path)
        {
            if (!OperatingSystem.IsWindows()) return path;
            // 设备路径前缀：本地 \\?\C:\…；UNC 为 \\?\UNC\server\share（少 ?\ 的 \\UNC\ 会被当成
            // 机器名 "UNC" 解析，实测 GetFileAttributesW 返回 -1，UNC 任务执行通道整体失效）
            if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return path;
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
            if (path.StartsWith(@"\\UNC\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[6..];   // 历史错写防御
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[2..];
            return @"\\?\" + path;
        }
    }
}
