using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Sandbox.Game.World;
using Sandbox.ModAPI;

namespace ClientPlugin.Game;

/// <summary>
/// The rows of lots a server tells the plugin about. A server that sells in lots sends a message
/// on the channel below when a player opens a Store: UTF-8 text, the first line "BetterStoreLots 1",
/// then a line "store-item-id lot-size" for each row of lots of that Store.
/// </summary>
internal static class LotInfo
{
    public const ushort Channel = 14860;
    private const string Header = "BetterStoreLots 1";

    private static readonly Dictionary<long, int> lots = new Dictionary<long, int>();
    private static MySession registeredFor;

    /// <summary>True once the server of this session has sent its lots: then no row is guessed.</summary>
    public static bool Known { get; private set; }

    public static IReadOnlyDictionary<long, int> ByRowId => lots;

    /// <summary>Raised on the game thread when the server sends its lots.</summary>
    public static event Action Changed;

    /// <summary>Listens to the server of the current session. Call it before asking a Store for its items.</summary>
    public static void Listen()
    {
        if (registeredFor == MySession.Static || MyAPIGateway.Multiplayer == null)
            return;
        registeredFor = MySession.Static;
        lots.Clear();
        Known = false;
        MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(Channel, OnMessage);
        MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(Channel, OnMessage);
    }

    private static void OnMessage(ushort channel, byte[] data, ulong sender, bool fromServer)
    {
        if (!fromServer || !TryParse(Encoding.UTF8.GetString(data), out var received))
            return;
        foreach (var pair in received)
            lots[pair.Key] = pair.Value;
        Known = true;
        Changed?.Invoke();
    }

    internal static bool TryParse(string text, out Dictionary<long, int> received)
    {
        received = new Dictionary<long, int>();
        var lines = text.Split('\n');
        if (lines[0].Trim() != Header)
            return false;
        for (int i = 1; i < lines.Length; i++)
        {
            var fields = lines[i].Trim().Split(' ');
            if (
                fields.Length == 2
                && long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)
                && int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int lot)
                && lot > 1
            )
                received[id] = lot;
        }
        return true;
    }
}
