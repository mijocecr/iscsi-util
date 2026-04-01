using CommunityToolkit.Mvvm.ComponentModel;

namespace ISCSI_Util.Models;

/// <summary>
/// Represents an iSCSI target with all its properties and states.
/// Uses MVVM Observable properties for two-way binding in the UI.
/// </summary>
public partial class IscsiDestino : ObservableObject
{
    /// <summary>IP address of the iSCSI target.</summary>
    [ObservableProperty]
    private string ip;

    /// <summary>IQN (iSCSI Qualified Name) identifier of the target.</summary>
    [ObservableProperty]
    private string iqn;

    /// <summary>Device path (e.g., /dev/disk/by-path/...).</summary>
    [ObservableProperty]
    private string devicePath;

    /// <summary>Mount point where the target is mounted (e.g., /home/iscsi/targetname).</summary>
    [ObservableProperty]
    private string mountPoint;

    /// <summary>Indicates if the target is currently connected.</summary>
    [ObservableProperty]
    private bool conectado;

    /// <summary>Indicates if the target is selected in the UI.</summary>
    [ObservableProperty]
    private bool seleccionado;

    /// <summary>Indicates if the target connection should persist across reboots.</summary>
    [ObservableProperty]
    private bool persistir;

    /// <summary>Indicates if CHAP authentication is enabled for this target.</summary>
    [ObservableProperty]
    private bool usaChap = false;

    /// <summary>CHAP username for authentication.</summary>
    [ObservableProperty]
    private string usuarioChap;

    /// <summary>CHAP password for authentication.</summary>
    [ObservableProperty]
    private string passwordChap;

    /// <summary>The actual partition path if a partition exists; otherwise the device path.</summary>
    public string PartitionPath { get; set; }
}
