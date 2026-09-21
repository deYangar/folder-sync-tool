using System;
using System.IO;
using FolderSync.Core.Platform;

namespace FolderSync.Core
{
    /// <summary>目录变更监控：FileSystemWatcher 兜底（非 NTFS/未提权时 USNWatcher 降级用）。</summary>
    public class DirWatcher : IDisposable, IRealtimeWatcher
    {
        private FileSystemWatcher? _fsw;
        private readonly Action _onChange;
        private readonly Action _onOverflow;
        private readonly string[] _excludes;
        private string _root = "";

        public DirWatcher(string path, string excludePatterns, Action onChange, Action onOverflow)
        {
            _onChange = onChange;
            _onOverflow = onOverflow;
            _excludes = Scanner.SplitPatterns(excludePatterns);
            Start(path);
        }

        private void Start(string path)
        {
            _root = path.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            _fsw = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
            };
            // 排除模式不能塞进 FSW.Filters：那是「只监听匹配项」的 include 语义，
            // 会把正常文件的变更全部滤掉（兜底模式下实时同步整体失效）。排除在回调里自己做。
            _fsw.Created += (_, e) => OnFsChange(e.FullPath);
            _fsw.Changed += (_, e) => OnFsChange(e.FullPath);
            _fsw.Deleted += (_, e) => OnFsChange(e.FullPath);
            _fsw.Renamed += (_, e) => OnFsChange(e.FullPath);
            _fsw.Error += (_, _) =>
            {
                // 缓冲区溢出/内部错误：丢过事件，需要全量扫描校准。
                // 每次溢出都报（曾用 _overflowed 只报首次——Error 后 watcher 继续工作，
                // 后续溢出同样丢过事件；引擎侧 onOverflow/onChange 同接 OnWatchedChange，
                // 重复上报由引擎去抖合并，无副作用）
                _onOverflow();
            };
            _fsw.EnableRaisingEvents = true;
        }

        /// <summary>排除过滤只用于减少无效触发；漏判/误判都无害（同步本身是全量对比）。</summary>
        private void OnFsChange(string fullPath)
        {
            var rel = fullPath.Length > _root.Length
                ? fullPath[_root.Length..].Replace('\\', '/')
                : fullPath;
            var name = Path.GetFileName(fullPath);
            if (Scanner.IsExcluded(rel, name, _excludes)) return;
            _onChange();
        }

        public void Dispose()
        {
            if (_fsw != null)
            {
                _fsw.EnableRaisingEvents = false;
                _fsw.Dispose();
                _fsw = null;
            }
        }
    }
}
