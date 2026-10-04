using System.IO;

namespace FolderSync.Core.Platform
{
    /// <summary>Unix errno → 近似 Win32 错误码映射（Executor 的瞬时错误/磁盘满判定按 Win32 码走，
    /// 平台实现统一在此换算，三平台熔断语义一致）。HResult 低 16 位即 errno。
    /// C-4 单表纪律：BclFileOps 等一切「errno→Win32」换算必须委托本表——两张表并存时
    /// 宿主装没装 Platforms 程序集，同一错误的瞬时/致命判定就不同。</summary>
    public static class Errno
    {
        public static int ToWin32(IOException io) => (io.HResult & 0xFFFF) switch
        {
            28 => 112,      // ENOSPC → ERROR_DISK_FULL（磁盘满熔断的关键映射）
            122 => 395,     // EDQUOT → ERROR_DISK_QUOTA_EXCEEDED（C-3：配额满必须落熔断判定——
                            // 原样保留 122 映不到 Executor 认的 112/395，Linux 配额满整轮磨失败）
            13 => 5,        // EACCES → ERROR_ACCESS_DENIED
            1 => 5,         // EPERM → ERROR_ACCESS_DENIED
            2 => 2,         // ENOENT → ERROR_FILE_NOT_FOUND
            20 => 2,        // ENOTDIR → 按不存在处理（路径中某段不是目录）
            17 => 183,      // EEXIST → ERROR_ALREADY_EXISTS
            11 => 32,       // EAGAIN → ERROR_SHARING_VIOLATION（锁竞争归瞬时错误）
            26 => 32,       // ETXTBSY → 文件正被写入，同共享冲突
            39 => 145,      // ENOTEMPTY → ERROR_DIR_NOT_EMPTY（C-3：145 才是，原 116 是 ERROR_NO_SIGNAL_SENT）
            40 => 1142,     // ELOOP → ERROR_TOO_MANY_LINKS（C-3：1142 才是，原 232 是 ERROR_NO_DATA）
            36 => 206,      // ENAMETOOLONG
            18 => 17,       // EXDEV → ERROR_NOT_SAME_DEVICE（跨设备，交上层重试）
            21 => 5,        // EISDIR → 按拒绝访问处理
            22 => 123,      // EINVAL → ERROR_INVALID_NAME
            30 => 29,       // EROFS → ERROR_WRITE_PROTECT
            _ => 1
        };
    }
}
