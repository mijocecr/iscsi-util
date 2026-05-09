using System;

namespace ISCSI_Util.Models;

public class SessionInfo
{
    public string Iqn { get; set; } = "";
    public string Portal { get; set; } = "";
    public string Device { get; set; } = "";
    public int SizeGb { get; set; }
    public string Filesystem { get; set; } = "";
    public string MountPoint { get; set; } = "";
    public bool Connected { get; set; }
    public string Auth { get; set; } = "";
    public int LunId { get; set; }
    public string Vendor { get; set; } = "";
    public string Model { get; set; } = "";
    public DateTime ConnectedSince { get; set; }
}