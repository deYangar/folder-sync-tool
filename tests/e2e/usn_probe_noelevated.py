# H-5 探针 v2：实测非提权进程能否读 USN Journal（拍板路线第一步，2026-09-12）
# 完全照抄 UsnWatcher 的调用模式（HGlobal 输入缓冲 + 44B READ_USN_JOURNAL_DATA_V1 + FILE_ID_DESCRIPTOR 24B）
# 测四件事：a) 卷/目录两种句柄打开  b) QUERY_USN_JOURNAL  c) READ_USN_JOURNAL  d) OpenFileById+GetFinalPathNameByHandle
# 用法：python usn_probe_noelevated.py [目录]（默认临时目录；非提权与提权各跑一次对比）
import os
import shutil
import subprocess
import sys
import tempfile

CS = r"""
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class UsnProbe2 {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inBuf, uint inSize,
        byte[] outBuf, uint outSize, out uint ret, IntPtr ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumePathNameW(string path, StringBuilder sb, uint len);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenFileById(IntPtr volume, IntPtr fileIdDescriptor,
        uint access, uint share, IntPtr secAttrs, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(IntPtr h, StringBuilder sb, uint len, uint flags);

    const uint GENERIC_READ = 0x80000000;
    const uint FILE_SHARE_RW_ALL = 1 | 2 | 4;
    const uint OPEN_EXISTING = 3;
    const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;
    const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;

    // READ_USN_JOURNAL_DATA_V1 → 44 字节（照抄 UsnWatcher.AllocStructToBytes 的手工序列化）
    static IntPtr PackReadInput(ulong startUsn, uint reasonMask, ulong journalId) {
        var p = Marshal.AllocHGlobal(44);
        Marshal.WriteInt64(p, 0, (long)startUsn);
        Marshal.WriteInt32(p, 8, unchecked((int)reasonMask));
        Marshal.WriteInt32(p, 12, 0);            // ReturnOnlyOnClose
        Marshal.WriteInt64(p, 16, 0);            // Timeout
        Marshal.WriteInt64(p, 24, 0);            // BytesToWaitFor
        Marshal.WriteInt64(p, 32, (long)journalId);
        Marshal.WriteInt16(p, 40, 2);            // MinMajorVersion
        Marshal.WriteInt16(p, 42, 2);            // MaxMajorVersion
        return p;
    }

    static string TryRead(IntPtr h, string tag, ulong journalId, ulong nextUsn) {
        var inBuf = PackReadInput(nextUsn, 0x00003301, journalId);   // StartUsn=NextUsn（下一条写入位置，UsnWatcher 同款）
        try {
            var outBuf = new byte[64 * 1024];
            if (DeviceIoControl(h, FSCTL_READ_USN_JOURNAL, inBuf, 44, outBuf, (uint)outBuf.Length, out var ret, IntPtr.Zero) && ret >= 8) {
                // 找第一条记录的 parentFRN 供 OpenFileById 测试
                ulong parentFrn = 0; int off = 8; int recLenPos = -1;
                while (off + 60 <= (int)ret) {
                    uint recLen = BitConverter.ToUInt32(outBuf, off);
                    if (recLen == 0 || off + recLen > ret) break;
                    parentFrn = BitConverter.ToUInt64(outBuf, off + 16);
                    recLenPos = off;
                    break;
                }
                var s = $"[{tag}] READ_USN_JOURNAL 成功 返回 {ret} 字节";
                if (parentFrn != 0 && recLenPos >= 0) {
                    // OpenFileById：FILE_ID_DESCRIPTOR 24B（Type=2，照抄 UsnWatcher）
                    var fd = Marshal.AllocHGlobal(24);
                    try {
                        Marshal.WriteInt32(fd, 0, 24);                 // dwSize
                        Marshal.WriteInt32(fd, 4, 2);                  // Type = ExtendedFileIdType
                        Marshal.WriteInt64(fd, 8, (long)parentFrn);    // FileId
                        Marshal.WriteInt64(fd, 16, 0);                 // Reserved
                        var h2 = OpenFileById(h, fd, GENERIC_READ, FILE_SHARE_RW_ALL, IntPtr.Zero, FILE_FLAG_BACKUP_SEMANTICS);
                        if (h2 != IntPtr.Zero && h2 != new IntPtr(-1)) {
                            try {
                                var psb = new StringBuilder(1024);
                                var len = GetFinalPathNameByHandleW(h2, psb, 1024, 0);
                                s += $"\n[{tag}] OpenFileById 成功 FRN=0x{parentFrn:X} 路径={psb.ToString()}";
                            } finally { CloseHandle(h2); }
                        } else
                            s += $"\n[{tag}] OpenFileById 失败 win32={Marshal.GetLastWin32Error()}";
                    } finally { Marshal.FreeHGlobal(fd); }
                }
                return s;
            }
            return $"[{tag}] READ_USN_JOURNAL 失败 win32={Marshal.GetLastWin32Error()}";
        } finally { Marshal.FreeHGlobal(inBuf); }
    }

    public static string Run(string dir) {
        var sb = new StringBuilder(512);
        var vol = GetVolumePathNameW(dir, sb, 512) ? sb.ToString() : "";
        var lines = new StringBuilder();
        lines.AppendLine($"[env] dir={dir} volume={vol}");
        var princ = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent());
        lines.AppendLine($"[env] 提权={princ.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)}");

        // 句柄一：目录句柄（BACKUP_SEMANTICS）—— UsnWatcher 的替代候选
        var hd = CreateFileW(dir, GENERIC_READ, FILE_SHARE_RW_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        lines.AppendLine(hd == new IntPtr(-1)
            ? $"[dir] 打开失败 win32={Marshal.GetLastWin32Error()}" : "[dir] 打开成功");
        // 句柄二：卷句柄 " \\.\C: " —— UsnWatcher 现行方式（需要管理员）
        IntPtr hv = new IntPtr(-1);
        if (vol.Length >= 2 && vol[1] == ':') {
            hv = CreateFileW(@"\\.\" + vol[0] + ":", GENERIC_READ, 1 | 2, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            lines.AppendLine(hv == new IntPtr(-1)
                ? $"[vol] 打开失败 win32={Marshal.GetLastWin32Error()}" : "[vol] 打开成功");
        }

        foreach (var (h, tag) in new[] { (hd, "dir"), (hv, "vol") }) {
            if (h == new IntPtr(-1)) continue;
            try {
                var q = new byte[64];
                if (DeviceIoControl(h, FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0, q, 64, out var qr, IntPtr.Zero) && qr >= 56) {
                    ulong jid = BitConverter.ToUInt64(q, 0), next = BitConverter.ToUInt64(q, 16);
                    lines.AppendLine($"[{tag}] QUERY 成功 JournalID={jid} NextUsn={next}");
                    lines.AppendLine(TryRead(h, tag, jid, next));
                } else {
                    lines.AppendLine($"[{tag}] QUERY 失败 win32={Marshal.GetLastWin32Error()}");
                }
            } finally { CloseHandle(h); }
        }
        return lines.ToString();
    }
}
"""


def main():
    target = sys.argv[1] if len(sys.argv) > 1 else None
    tmp = None
    if target is None:
        tmp = tempfile.mkdtemp(prefix="usnprobe_")
        with open(os.path.join(tmp, "touch.txt"), "w") as f:
            f.write("x")
        target = tmp
    ps = "[Console]::OutputEncoding = [Text.Encoding]::UTF8; Add-Type -TypeDefinition @'\n" + CS + "\n'@\n[UsnProbe2]::Run('" + target + "')"
    r = subprocess.run(["pwsh", "-NoProfile", "-Command", ps], capture_output=True, text=True, encoding="utf-8", errors="replace")
    print(r.stdout)
    if r.stderr:
        print("STDERR:", r.stderr[:2000])
    if tmp:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    main()
