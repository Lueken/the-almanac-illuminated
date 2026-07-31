using ProtoBuf;

namespace AlmanacIlluminated;

/// <summary>Client asks the server for the player's bound spawn (bed, else world default).</summary>
[ProtoContract]
public class HomebaseRequest { }

/// <summary>
/// Server's reply: the player's resolved spawn position. The bound spawn lives only
/// on the server (IServerPlayer), so the Crops tab fetches it over this channel to
/// read the home climate. BedSpawn is false when the player has no bed set and the
/// world default was used.
/// </summary>
[ProtoContract]
public class HomebaseResponse
{
    [ProtoMember(1)] public int X;
    [ProtoMember(2)] public int Y;
    [ProtoMember(3)] public int Z;
    [ProtoMember(4)] public bool BedSpawn;
}

/// <summary>
/// Client tells the server it opened or closed the Almanac. The server stamps the
/// player entity's watched attributes, which broadcast to every client, so anyone
/// nearby sees the reader holding the open book (ReadingHands renders it). The
/// flag is server-cleared on join so a crash mid-read cannot strand it.
/// </summary>
[ProtoContract]
public class ReadingState
{
    [ProtoMember(1)] public bool Reading;
}
