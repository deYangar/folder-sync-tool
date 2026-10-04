using System;
using System.Globalization;

namespace FolderSync.Core
{
    /// <summary>
    /// 指定时刻调度规格（v1.6 B2）：jobs.schedule_spec 列的解析与下次触发时刻计算。
    /// 格式（本地时区）：
    ///   daily HH:mm            —— 每天（如 "daily 03:00"）
    ///   weekly D[,D...] HH:mm  —— 每周指定星期（如 "weekly 1,3,5 03:00"；1=周一…7=周日，与 ISO 对齐）
    /// 解析失败返回 null（任务回退手动语义，绝不因脏数据炸调度器）。
    /// </summary>
    public static class ScheduleSpec
    {
        /// <summary>解析规格；非法返回 null。</summary>
        public static Parsed? Parse(string? spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return null;
            var parts = spec.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            try
            {
                if (parts.Length == 2 && parts[0].Equals("daily", StringComparison.OrdinalIgnoreCase))
                    return new Parsed(ParseHm(parts[1]), null);
                if (parts.Length == 3 && parts[0].Equals("weekly", StringComparison.OrdinalIgnoreCase))
                {
                    var days = parts[1].Split(',');
                    var mask = 0;
                    foreach (var d in days)
                    {
                        if (!int.TryParse(d.Trim(), out var day) || day is < 1 or > 7) return null;
                        mask |= 1 << day;
                    }
                    return mask == 0 ? null : new Parsed(ParseHm(parts[2]), mask);
                }
            }
            catch { }
            return null;
        }

        private static (int h, int m) ParseHm(string s)
        {
            var seg = s.Split(':');
            if (seg.Length != 2 || !int.TryParse(seg[0], out var h) || !int.TryParse(seg[1], out var m)
                || h is < 0 or > 23 || m is < 0 or > 59)
                throw new FormatException("HH:mm");
            return (h, m);
        }

        /// <summary>计算 after 之后的下一个触发时刻（本地时区；daily=每天，weekly=指定星期几）。
        /// 调度器每个 tick 用「上次触发时刻」作 after 现算，无需落库。</summary>
        public static DateTime? NextDue(string? spec, DateTime after)
        {
            var p = Parse(spec);
            return p == null ? null : NextDue(p.Value, after);
        }

        private static DateTime? NextDue(Parsed p, DateTime after)
        {
            after = after.AddSeconds(1);   // 「之后」严格大于：恰好落在 HH:mm:00 的 after 不再触发当次
            var (h, m) = p.Time;
            if (p.DaysMask == null)
            {
                var today = after.Date.AddHours(h).AddMinutes(m);
                return today > after ? today : today.AddDays(1);
            }
            // weekly：从 after 起逐日找第一个满足星期掩码的 HH:mm
            for (int i = 0; i < 8; i++)
            {
                var day = after.Date.AddDays(i).AddHours(h).AddMinutes(m);
                if (day > after && ((p.DaysMask.Value >> IsoDay(day)) & 1) == 1) return day;
            }
            return null;   // 掩码空（Parse 已挡），防御
        }

        /// <summary>DayOfWeek（周日=0）→ ISO 星期（周一=1…周日=7）。</summary>
        private static int IsoDay(DateTime d) => d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;

        /// <summary>显示文案：任务卡「下次 03:00（周三）」用。</summary>
        public static string DescribeNext(string? spec, DateTime now)
        {
            var due = NextDue(spec, now);
            if (due == null) return "";
            static string DayZh(int iso) => iso switch
            {
                1 => "周一", 2 => "周二", 3 => "周三", 4 => "周四", 5 => "周五", 6 => "周六", 7 => "周日", _ => ""
            };
            var dayPart = due.Value.Date == now.Date
                ? "今天"
                : due.Value.Date == now.Date.AddDays(1) ? "明天"
                : DayZh(IsoDay(due.Value));
            return $"{due.Value:HH:mm}（{dayPart}）";
        }

        public readonly record struct Parsed((int h, int m) Time, int? DaysMask);
    }
}
