namespace FolderSync.Core.Platform
{
    /// <summary>Shell 集成抽象：打开文件/目录（explorer / xdg-open / open）。</summary>
    public interface IShellOps
    {
        /// <summary>用系统默认方式打开文件或目录（日志/报告/失败明细的「打开」入口）。</summary>
        void OpenPath(string path);
    }
}
