using System.Net;
using System.Net.Sockets;
using System.Text;

namespace uSync.GitEdition.Worktrees;

/// <summary>
///  Picks a stable https/http port pair for a worktree: the same name gets the same ports
///  every time, unless something else is already using them.
/// </summary>
internal static class PortPicker
{
    private const int Pairs = 500;

    public static (int Https, int Http) Pick(string name, int rangeStart)
    {
        var start = (int)(StableHash(name) % Pairs);

        for (var attempt = 0; attempt < Pairs; attempt++)
        {
            var https = rangeStart + ((start + attempt) % Pairs) * 2;
            if (IsFree(https) && IsFree(https + 1))
                return (https, https + 1);
        }

        throw new InvalidOperationException($"No free port pair between {rangeStart} and {rangeStart + Pairs * 2}");
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

    private static bool IsFree(int port)
    {
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
