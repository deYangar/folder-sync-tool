using System.IO;

namespace FolderSync.Core.Platform
{
    /// <summary>Unix errno → 近似 Win32 错误码映射（Executor 的瞬时错误/磁盘满判定按 Win32 码走，
    /// 平台实现统一在此换算，三平台熔断语义一致）。HResult 低 16 位即 errno。</summary>
    public static class Errno
    {
        public static int ToWin32(IOException io) => (io.HResult & 0xFFFF) switch
        {
            28 => 112,      // ENOSPC → ERROR_DISK_FULL（磁盘满熔断的关键映射）
            13 => 5,        // EACCES → ERROR_ACCESS_DENIED
            1 => 5,         // EPERM → ERROR_ACCESS_DENIED
            2 => 2,         // ENOENT → ERROR_FILE_NOT_FOUND
            20 => 2,        // ENOTDIR → 按不存在处理
            17 => 183,      // EEXIST → ERROR_ALREADY_EXISTS
            11 => 32,       // EAGAIN → ERROR_SHARING_VIOLATION（锁竞争归瞬时错误）
            26 => 32,       // ETXTBSY → 文件正被写入，同共享冲突
            39 => 116,      // ENOTEMPTY → ERROR_DIR_NOT_EMPTY
            40 => 232,      // ELOOP → TOO_MANY_LINKS
            36 => 206,      // ENAMETOOLONG
            18 => 17,       // EXDEV → 跨设备（当作已存在/不可原子，交上层重试）
            122 => 122,     // EDQUOT → 配额（与 Win32 1108 邻域，保留原值）
            _ => 1
        };
    }
}
