using System.Globalization;

namespace DubStudio.Core.Timecode;

/// <summary>帧率与时间码换算。内部统一毫秒存储；时间码支持 SMPTE 丢帧（29.97/59.94）。
/// 可变帧率（VFR）素材以毫秒为权威时间轴，帧号仅按标称帧率近似换算。</summary>
public sealed record FrameRate(double Num, double Den)
{
    public double FramesPerSecond => Num / Den;

    public bool IsDropFrame =>
        Math.Abs(FramesPerSecond - 30000.0 / 1001.0) < 0.01 ||
        Math.Abs(FramesPerSecond - 60000.0 / 1001.0) < 0.01;

    /// <summary>时间码基帧：29.97 与 59.94 的 SMPTE 丢帧都以 30 为基（59.94 每帧号对应两帧）。</summary>
    public int RoundedFramesPerSecond => IsDropFrame ? 30 : (int)Math.Round(FramesPerSecond);

    /// <summary>每帧号对应的物理帧数：59.94 为 2，其余为 1。</summary>
    public int FramesPerLabel => IsDropFrame && FramesPerSecond > 45 ? 2 : 1;

    /// <summary>物理帧率（VFR/标称换算用）。</summary>
    public int PhysicalFramesPerSecond => IsDropFrame ? (FramesPerSecond > 45 ? 60 : 30) : (int)Math.Round(FramesPerSecond);

    public static FrameRate Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new FrameRate(24, 1);
        text = text.Trim();
        if (text.Contains('/'))
        {
            var parts = text.Split('/', 2);
            return new FrameRate(double.Parse(parts[0], CultureInfo.InvariantCulture),
                                 double.Parse(parts[1], CultureInfo.InvariantCulture));
        }
        var v = double.Parse(text, CultureInfo.InvariantCulture);
        // 常见小数写法映射为精确分数
        return v switch
        {
            23.976 => new FrameRate(24000, 1001),
            29.97 => new FrameRate(30000, 1001),
            59.94 => new FrameRate(60000, 1001),
            _ => new FrameRate(v, 1)
        };
    }

    public long MillisecondsToFrame(double ms) => (long)Math.Round(ms / 1000.0 * FramesPerSecond);

    public double FrameToMilliseconds(long frame) => frame * 1000.0 / FramesPerSecond; // 物理帧

    public string FrameToTimecode(long frame)
    {
        int h, m, s, f;
        if (IsDropFrame)
        {
            var r = RoundedFramesPerSecond;
            var d = r / 15; // 30 基 -> 每非整十分丢 2 个帧号
            var label = frame / FramesPerLabel;
            (h, m, s, f) = FrameToDrop(label, r, d);
            return $"{h:00}:{m:00}:{s:00};{f:00}";
        }
        else
        {
            var r = RoundedFramesPerSecond;
            var total = frame;
            f = (int)(total % r); total /= r;
            s = (int)(total % 60); total /= 60;
            m = (int)(total % 60); total /= 60;
            h = (int)total;
            return $"{h:00}:{m:00}:{s:00}:{f:00}";
        }
    }

    public long TimecodeToFrame(string tc)
    {
        if (string.IsNullOrWhiteSpace(tc)) throw new FormatException("时间码为空");
        var parts = tc.Split(':', ';');
        if (parts.Length != 4) throw new FormatException($"时间码格式错误: {tc}");
        var h = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var m = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var s = int.Parse(parts[2], CultureInfo.InvariantCulture);
        var f = int.Parse(parts[3], CultureInfo.InvariantCulture);
        var r = RoundedFramesPerSecond;
        long n = 60L * h + m;
        bool drop = tc.Contains(';') && IsDropFrame;
        if (drop)
        {
            var d = r / 15;
            // 严格逆解：label = 60Rn - D(n - n/10) + 秒内偏移；
            // :00 秒允许的帧号为 [0,d) 仅在整十分钟；非整十分钟为 [d,r)
            if (f >= r)
                throw new FormatException($"帧号超出 {r} 基: {tc}");
            // 正解 H(n)=60Rn-累计丢帧 (+d 个无标签物理帧)。
            // 分钟内：:00 秒无标签物理帧 0..d-1（非整十分），标签 ;f 对应物理 f；
            //        :s≥1 秒相对 60Rn 的偏移为 s*r+f（d 个无标签帧位于 :00 内）。
            var minuteStart = 60L * r * n - d * (n - n / 10);
            var withinMinute = s == 0
                ? f
                : (long)s * r + f;
            if (n % 10 != 0 && s == 0 && f < d)
                throw new FormatException($"该分钟已丢弃 0..{d - 1} 帧号: {tc}");
            var labelFrame = minuteStart + withinMinute;
            return labelFrame * FramesPerLabel; // 59.94: 帧号 -> 物理帧（每标签 2 物理帧）
        }
        return ((n * 60 + s) * r) + f;
    }

    public double TimecodeToMilliseconds(string tc) => FrameToMilliseconds(TimecodeToFrame(tc));

    /// <summary>label 帧号（30 基）-> 丢帧时间码分量（已用全部 SMPTE 锚点验证）。</summary>
    private static (int h, int m, int s, int f) FrameToDrop(long frame, int r, int d)
    {
        long H(long n) => 60L * r * n - d * (n - n / 10) + (n % 10 == 0 ? 0 : d);
        long n = frame / (60L * r);
        while (H(n + 1) <= frame) n++;
        while (n > 0 && H(n) > frame) n--;
        var rem = frame - H(n);
        int s2, f2;
        if (n % 10 == 0)
        {
            s2 = (int)(rem / r);
            f2 = (int)(rem % r);
        }
        else
        {
            var firstSecond = r - d;
            if (rem < firstSecond) { s2 = 0; f2 = (int)rem + d; }
            else
            {
                var rest = rem - firstSecond;
                s2 = 1 + (int)(rest / r);
                f2 = (int)(rest % r);
            }
        }
        return ((int)(n / 60), (int)(n % 60), s2, f2);
    }
}
