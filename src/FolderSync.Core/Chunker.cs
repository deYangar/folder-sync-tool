using System;
using System.Collections.Generic;
using System.IO;

namespace FolderSync.Core
{
    /// <summary>
    /// CDC 内容定义切块（块级去重版本库的地基，restic 同原理轻量版）：
    /// BuzHash 滚动哈希（48 字节窗口、CRC32 表驱动），哈希低 19 位为 0 处切出边界（名义平均 512KiB），
    /// 64KiB / 2MiB 钳制。块边界只由内容本身决定：文件头部插入数据只波及局部 1-2 块，
    /// 后续块逐字节不变 → 跨版本去重的基础。
    /// 查表与全部参数为固定常量（零随机种子，跨机跨次确定）；改参数必须升 VersionStore.FormatVersion。
    /// </summary>
    public static class CdcChunker
    {
        public const int MinChunkSize = 64 * 1024;
        public const int MaxChunkSize = 2 * 1024 * 1024;
        public const int MaskBits = 19;
        public const uint BoundaryMask = (1u << MaskBits) - 1;
        public const int WindowSize = 48;
        private const int OutRot = WindowSize % 32;   // 出窗撤销的循环移位量（48→16）

        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            const uint poly = 0xEDB88320;   // 标准 CRC32 反射多项式
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint h = i;
                for (int k = 0; k < 8; k++)
                    h = (h & 1) != 0 ? poly ^ (h >> 1) : h >> 1;
                t[i] = h;
            }
            return t;
        }

        private static uint Rotl(uint v, int n) => (v << n) | (v >> (32 - n));

        /// <summary>
        /// 流式切块：按枚举顺序拼接全部输出即原始字节。每块 ≤ MaxChunkSize，内部 80KiB 缓冲读。
        /// </summary>
        public static IEnumerable<byte[]> Chunk(Stream input) => Chunk(input, null, null);

        /// <summary>缓冲复用重载：热路径（块级增量）传入外部缓冲免每次分配 2MiB+80KiB。
        /// 注意同一缓冲不可被两个并发迭代共用（每次 yield 的 chunk 是独立拷贝，缓冲仅滚动暂存）。</summary>
        public static IEnumerable<byte[]> Chunk(Stream input, byte[]? chunkBuf, byte[]? readBuf)
        {
            var buf = chunkBuf ?? new byte[MaxChunkSize];
            var rb = readBuf ?? new byte[80 * 1024];
            var win = new byte[WindowSize];
            int len = 0, wp = 0;
            long total = 0;
            uint h = 0;

            int n;
            while ((n = input.Read(rb, 0, rb.Length)) > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    byte c = rb[i];
                    if (total >= WindowSize)
                        h ^= Rotl(Table[win[wp]], OutRot);   // win[wp] = 恰好 48 步前写入的最老字节
                    win[wp] = c;
                    wp = (wp + 1) % WindowSize;
                    h = Rotl(h ^ Table[c], 1);
                    buf[len++] = c;
                    total++;
                    if (len >= MinChunkSize && ((h & BoundaryMask) == 0 || len >= MaxChunkSize))
                    {
                        var chunk = new byte[len];
                        Buffer.BlockCopy(buf, 0, chunk, 0, len);
                        yield return chunk;
                        len = 0;
                    }
                }
            }
            if (len > 0)
            {
                var chunk = new byte[len];
                Buffer.BlockCopy(buf, 0, chunk, 0, len);
                yield return chunk;
            }
        }
    }
}
