using System.Net;
using System.Net.Sockets;

using uSync.TreeBranch.Worktrees;

namespace uSync.TreeBranch.Tests;

public class PortPickerTests
{
    private const int RangeStart = 46000;

    [Fact]
    public void SameName_GetsTheSamePorts()
        => Assert.Equal(PortPicker.Pick("feature-a", RangeStart), PortPicker.Pick("FEATURE-A", RangeStart));

    [Fact]
    public void Ports_AreAPairInTheRange()
    {
        var (https, http) = PortPicker.Pick("feature-a", RangeStart);
        Assert.Equal(https + 1, http);
        Assert.Equal(0, (https - RangeStart) % 2);
        Assert.InRange(https, RangeStart, RangeStart + 999);
    }

    [Fact]
    public void PortInUse_PicksAnotherPair()
    {
        var (https, _) = PortPicker.Pick("busy-worktree", RangeStart);

        var listener = new TcpListener(IPAddress.Loopback, https);
        listener.Start();
        try
        {
            var (next, _) = PortPicker.Pick("busy-worktree", RangeStart);
            Assert.NotEqual(https, next);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData(80)]
    [InlineData(65000)]
    public void RangeOutsideTheUsablePorts_Throws(int rangeStart)
        => Assert.Throws<ArgumentOutOfRangeException>(() => PortPicker.Pick("x", rangeStart));
}
