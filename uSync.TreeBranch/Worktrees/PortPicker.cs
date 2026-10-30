using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace uSync.TreeBranch.Worktrees;

/// <summary>
///  Picks a stable https/http port pair for a worktree: the same name gets the same ports
///  every time, unless something else is already using them.
/// </summary>
internal static class PortPicker
{
    private const int Pairs = 500;

    public static (int Https, int Http) Pick(string name, int rangeStart)
    {
        var rangeEnd = rangeStart + Pairs * 2 - 1;
        if (rangeStart < 1024 || rangeEnd > IPEndPoint.MaxPort)
            throw new ArgumentOutOfRangeException(nameof(rangeStart), rangeStart, $"Git:Worktree:PortRangeStart must be between 1024 and {IPEndPoint.MaxPort - Pairs * 2 + 1}");

        // anything listening on any address (127.0.0.1, ::1, 0.0.0.0, ::) - Kestrel's localhost binds both loopbacks.
        var listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Select(x => x.Port)
            .ToHashSet();

        var start = (int)(StableHash(name) % Pairs);

        for (var attempt = 0; attempt < Pairs; attempt++)
        {
            var https = rangeStart + ((start + attempt) % Pairs) * 2;
            if (IsFree(https, listening) && IsFree(https + 1, listening))
                return (https, https + 1);
        }

        throw new InvalidOperationException($"No free port pair between {rangeStart} and {rangeEnd}");
    }

    /// <summary>
    ///  FNV-1a, because string.GetHashCode is different every time the process starts.
    /// </summary>
    private static uint StableHash(string value)
    {
        var hash = 2166136261;
        foreach (var b in Encoding.UTF8.GetBytes(value.ToLowerInvariant()))
        {
            hash ^= b;
            hash *= 16777619;
        }
        return hash;
    }

    private static bool IsFree(int port, HashSet<int> listening)
    {
        if (listening.Contains(port)) return false;

        // nothing listening, but the port can still be reserved (e.g. Windows' excluded port ranges).
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
