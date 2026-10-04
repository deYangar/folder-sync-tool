using System;
using System.Collections.Generic;

namespace FolderSync.Core.Platform
{
    /// <summary>文件系统操作抽象：复制+进度、时间戳保留、回收站删除、网络盘识别。
    /// 实现侧负责把平台错误统一映射到 Win32 错误码语义（如磁盘满=112），
    /// 使 Executor 的瞬时错误/磁盘满判定三平台一致。</summary>
    public interface IFileOps
    {
        /// <summary>复制单个文件（进度回调 (total, transferred)，返回 false 请求中止）。
        /// 成功返回 true；失败返回 false 且 win32Error 带近似 Win32 错误码
        /// （Unix 侧 ENOSPC→112/EACCES→5/ENOENT→2 等），用户中止返回 1235。</summary>
        bool CopyFile(string src, string dst, Func<long, long, bool>? onProgress, out int win32Error);

        /// <summary>把源的时间戳尽力写到目标（Windows: ctime+mtime；macOS: birthtime+mtime；Linux: 仅 mtime）。
        /// 尽力而为：失败静默（mtime 另有复制实现原生兜底）。</summary>
        void CopyTimestamps(string src, string dst);

        /// <summary>批量回收站删除。实现内不抛（批失败由调用方按 Exists 逐个核对兜底）。</summary>
        void RecycleDeleteBatch(IReadOnlyList<string> paths);

        /// <summary>单个条目回收站删除。失败且条目仍在时抛 IOException（调用方按条目失败处理）。</summary>
        void RecycleDelete(string path, bool isDirectory);

        /// <summary>网络路径识别（Windows: UNC+映射盘符；Unix 一期恒 false，按普通目录同步）。</summary>
        bool IsNetworkPath(string path);
    }

    /// <summary>回收站删除不支持时抛出（BCL 兜底实现无回收站能力；宁可不删也不错删）。</summary>
    public sealed class RecycleBinUnsupportedException : NotSupportedException
    {
        public RecycleBinUnsupportedException(string message) : base(message) { }
    }
}
