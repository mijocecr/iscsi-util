using System.ComponentModel;
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

    
    public string FsType { get; set; }

    
    /// <summary>IQN (iSCSI Qualified Name) identifier of the target.</summary>
    [ObservableProperty]
    private string iqn;

    /// <summary>Device path (e.g., /dev/disk/by-path/... ).</summary>
    [ObservableProperty]
    private string devicePath;

    /// <summary>Mount point where the target is mounted.</summary>
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

    /// <summary>Indicates if CHAP authentication is enabled.</summary>
    [ObservableProperty]
    private bool usaChap = false;

    /// <summary>CHAP username.</summary>
    [ObservableProperty]
    private string usuarioChap;

    /// <summary>CHAP password.</summary>
    [ObservableProperty]
    private string passwordChap;

    /// <summary>Indicates if Mutual CHAP is enabled.</summary>
    [ObservableProperty]
    private bool usaMutualChap = false;

    /// <summary>Mutual CHAP username (target authenticates initiator).</summary>
    [ObservableProperty]
    private string usuarioMutualChap;

    /// <summary>Mutual CHAP password.</summary>
    [ObservableProperty]
    private string passwordMutualChap;

    /// <summary>Indicates if the partition has a filesystem.</summary>
    [ObservableProperty]
    private bool tieneFilesystem = false;

    /// <summary>The actual partition path if a partition exists; otherwise the device path.</summary>
    public string PartitionPath { get; set; }

    /// <summary>
    /// Computed property: true if target is connected AND has no filesystem (can be initialized).
    /// </summary>
    public bool PuedeInicializar => Conectado && !TieneFilesystem;

    /// <summary>
    /// True if any authentication mode is active (CHAP or Mutual CHAP).
    /// </summary>
    public bool AutenticacionActiva => UsaChap || UsaMutualChap;

    /// <summary>
    /// True if CHAP credentials are valid.
    /// </summary>
    public bool ChapValido =>
        UsaChap &&
        !string.IsNullOrWhiteSpace(UsuarioChap) &&
        !string.IsNullOrWhiteSpace(PasswordChap);

    /// <summary>
    /// True if Mutual CHAP credentials are valid.
    /// </summary>
    public bool MutualChapValido =>
        UsaMutualChap &&
        !string.IsNullOrWhiteSpace(UsuarioMutualChap) &&
        !string.IsNullOrWhiteSpace(PasswordMutualChap);

    /// <summary>
    /// Override OnPropertyChanged to notify when computed properties change.
    /// </summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName == nameof(Conectado) ||
            e.PropertyName == nameof(TieneFilesystem))
        {
            OnPropertyChanged(nameof(PuedeInicializar));
        }

        if (e.PropertyName == nameof(UsaChap) ||
            e.PropertyName == nameof(UsaMutualChap))
        {
            OnPropertyChanged(nameof(AutenticacionActiva));
        }
    }
}
