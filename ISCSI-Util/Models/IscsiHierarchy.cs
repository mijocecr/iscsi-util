using System.Collections.Generic;
using System.Linq;

namespace ISCSI_Util.Models;

/// <summary>
/// Nivel Target / Portal: nodo raíz devuelto por el descubrimiento iSCSI
/// (IQN + portal). Puede exponer una o varias unidades lógicas.
/// </summary>
public sealed class IscsiTargetNode
{
    public string Iqn { get; init; } = string.Empty;
    public string Portal { get; init; } = string.Empty;
    public List<IscsiLunNode> Luns { get; init; } = new();

    public int LunCount => Luns.Count;
    public bool AnyConnected => Luns.Any(l => l.Connected);
    public bool HasMultipleLuns => Luns.Count > 1;
}

/// <summary>
/// Nivel LUN: unidad lógica concreta dentro del target. Apunta al
/// dispositivo de bloques real del cliente (/dev/sdX o partición).
/// </summary>
public sealed class IscsiLunNode
{
    public int LunId { get; init; }
    public SessionInfo Session { get; init; } = new();

    public string Device => Session.Device;
    public string MountPoint => Session.MountPoint;
    public string Filesystem => Session.Filesystem;
    public int SizeGb => Session.SizeGb;
    public bool Connected => Session.Connected;
}
