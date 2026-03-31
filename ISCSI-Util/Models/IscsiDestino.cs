
/*
using CommunityToolkit.Mvvm.ComponentModel;

namespace ISCSI_Util.Models;

// 🔥 Ahora la clase es partial y hereda de ObservableObject
public partial class IscsiDestino : ObservableObject
{
    [ObservableProperty]
    private string ip;

    [ObservableProperty]
    private string iqn;

    [ObservableProperty]
    private string devicePath;

    [ObservableProperty]
    private string mountPoint;

    [ObservableProperty]
    private bool conectado;

    [ObservableProperty]
    private bool seleccionado;

    [ObservableProperty]
    private bool persistir;

    // Campos para CHAP
    [ObservableProperty]
    private bool usaChap = false;

    [ObservableProperty]
    private string usuarioChap;

    [ObservableProperty]
    private string passwordChap;
    
    // NUEVO: ruta real que se monta (partición si existe; si no, el device)
    public string PartitionPath { get; set; }
}
*/

using CommunityToolkit.Mvvm.ComponentModel;

namespace ISCSI_Util.Models;

// 🔥 Clase moderna usando ObservableObject + ObservableProperty
public partial class IscsiDestino : ObservableObject
{
    // Datos base del destino
    [ObservableProperty]
    private string ip;

    [ObservableProperty]
    private string iqn;

    [ObservableProperty]
    private string devicePath;

    [ObservableProperty]
    private string mountPoint;

    [ObservableProperty]
    private bool conectado;

    [ObservableProperty]
    private bool seleccionado;

    [ObservableProperty]
    private bool persistir;

    // ⭐ CHAP
    // Esta propiedad es clave para activar/desactivar los campos Usuario/Password
    [ObservableProperty]
    private bool usaChap = false;

    [ObservableProperty]
    private string usuarioChap;

    [ObservableProperty]
    private string passwordChap;

    // ⭐ Ruta real de la partición (si existe)
    public string PartitionPath { get; set; }
    
    
    
    
}
