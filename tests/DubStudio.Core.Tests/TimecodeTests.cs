using DubStudio.Core.Timecode;
using Xunit;

namespace DubStudio.Core.Tests;

public class TimecodeTests
{
    [Fact]
    public void DropFrame_2997_KnownLabels()
    {
        var fr = FrameRate.Parse("29.97");
        Assert.True(fr.IsDropFrame);
        // 标准丢帧时间码锚点
        Assert.Equal("00:00:59;28", fr.FrameToTimecode(1798));
        Assert.Equal("00:01:00;02", fr.FrameToTimecode(1800)); // 1800 原始帧恰为 ;02 标签
        Assert.Equal("00:10:00;00", fr.FrameToTimecode(17982));
        Assert.Equal("01:00:00;00", fr.FrameToTimecode(107892));
    }

    [Fact]
    public void DropFrame_RoundTrips()
    {
        var fr = new FrameRate(30000, 1001);
        foreach (var n in new long[] { 0, 1, 29, 1798, 1799, 1800, 1801, 17982, 107892, 123456 })
        {
            var label = fr.FrameToTimecode(n);
            Assert.Equal(n, fr.TimecodeToFrame(label));
        }
    }

    [Fact]
    public void DropFrame_5994_DropsFourFrames()
    {
        var fr = FrameRate.Parse("59.94");
        Assert.True(fr.IsDropFrame);
        Assert.Equal(30, fr.RoundedFramesPerSecond);
        Assert.Equal(2, fr.FramesPerLabel);
        var f = fr.TimecodeToFrame("00:01:00;02");
        Assert.Equal(3600, f);  // label 1800 × 每标签 2 物理帧
        Assert.Equal("00:01:00;02", fr.FrameToTimecode(f));
    }

    [Fact]
    public void NonDrop_24_IsStraightforward()
    {
        var fr = new FrameRate(24, 1);
        Assert.False(fr.IsDropFrame);
        Assert.Equal("00:00:01:00", fr.FrameToTimecode(24));
        Assert.Equal(25, fr.TimecodeToFrame("00:00:01:01"));
    }

    [Fact]
    public void Milliseconds_RoundTrips()
    {
        var fr = new FrameRate(30000, 1001);
        var frame = fr.MillisecondsToFrame(10_000);
        var back = fr.FrameToMilliseconds(frame);
        // 30000/1001 帧周期约 33.37ms，10s 处量化误差不超过一帧
        Assert.True(Math.Abs(back - 10_000) < 35, $"往返误差过大: {back}");
    }

    [Fact]
    public void FractionalParse_23976()
    {
        var fr = FrameRate.Parse("23.976");
        Assert.Equal(24000, fr.Num);
        Assert.Equal(1001, fr.Den);
        Assert.False(fr.IsDropFrame);
    }
}
