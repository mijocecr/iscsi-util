using System;

namespace ISCSI_Util.Models;

public class SessionInfo
{
    public string Iqn { get; set; } = "";
    public string Portal { get; set; } = "";
    public string Device { get; set; } = "";
    public int SizeGb { get; set; }
    /// <summary>Tamaño exacto en bytes del dispositivo resuelto. Se usa para
    /// detectar cambios de tamaño en caliente sin depender del redondeo a GB.</summary>
    public long SizeBytes { get; set; }
    public string Filesystem { get; set; } = "";
    public string MountPoint { get; set; } = "";
    public bool Connected { get; set; }
    /// <summary>El volumen está montado ahora mismo (detectado con findmnt en segundo plano).</summary>
    public bool Mounted { get; set; }
    public string Auth { get; set; } = "";
    public int LunId { get; set; }
    public string Vendor { get; set; } = "";
    public string Model { get; set; } = "";
    public DateTime ConnectedSince { get; set; }
}