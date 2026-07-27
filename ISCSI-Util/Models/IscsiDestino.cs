using System;

namespace ISCSI_Util.Models;

public class IscsiDestino
{
    // ============================================================
    // IDENTIDAD DEL DESTINO
    // ============================================================
    public string Ip { get; set; } = "";
    public string Iqn { get; set; } = "";
    public bool EsAccesible { get; set; } = false;
    public string PortalReal { get; set; } = "";

    // ============================================================
    // ESTADO DE CONEXIÓN
    // ============================================================
    public bool Conectado { get; set; } = false;
    public bool Seleccionado { get; set; } = false;
    public DateTime? ConnectedSince { get; set; } = null;

    // ============================================================
    // RUTAS DEL SISTEMA
    // ============================================================
    public string? DevicePath { get; set; } = null;
    public string? PartitionPath { get; set; } = null;
    public string? MountPoint { get; set; } = null;

    // ============================================================
    // FILESYSTEM
    // ============================================================
    public bool TieneFilesystem { get; set; } = false;
    public string FsType { get; set; } = "";

    // ============================================================
    // INFORMACIÓN DEL DISCO
    // ============================================================
    public string Vendor { get; set; } = "";
    public string Model { get; set; } = "";
    public int SizeGb { get; set; } = 0;
    public int LunId { get; set; } = 0;

    // ============================================================
    // PERSISTENCIA
    // ============================================================
    public bool Persistir { get; set; } = false;
    public bool PersistenteReal { get; set; } = false;

    // ============================================================
    // CHAP / MUTUAL CHAP
    // ============================================================
    public bool RequiresChap { get; set; } = false;
    public bool RequiresMutualChap { get; set; } = false;

    public bool HasLocalChapConfigured { get; set; } = false;
    public bool HasLocalMutualConfigured { get; set; } = false;

    public string LocalUser { get; set; } = "";
    public string LocalPass { get; set; } = "";
    public string LocalUserIn { get; set; } = "";
    public string LocalPassIn { get; set; } = "";

    public bool UsaChap { get; set; } = false;
    public bool UsaMutualChap { get; set; } = false;

    public string UsuarioChap { get; set; } = "";
    public string PasswordChap { get; set; } = "";
    public string UsuarioMutualChap { get; set; } = "";
    public string PasswordMutualChap { get; set; } = "";

    public bool InfoCompleta { get; set; } = false;

    // ============================================================
    // PROPIEDADES DERIVADAS
    // ============================================================
    public bool SinChap => !UsaChap && !UsaMutualChap;
    public bool EsRaw => !TieneFilesystem;
    public bool IsMounted => !string.IsNullOrEmpty(MountPoint);
    public bool IsReady => Conectado && TieneFilesystem;

    public string Estado =>
        Conectado
            ? (IsMounted ? "Mounted" : "Connected")
            : "Disconnected";

    public string DisplayName =>
        $"{Iqn} ({Ip})";

    // ============================================================
    // ICONO
    // ============================================================
    public string Icono
    {
        get
        {
            if (UsaMutualChap)
                return EsRaw ? "chap-mutual-hdd" : "chap-mutual";

            if (UsaChap)
                return EsRaw ? "chap-hdd" : "chap";

            return EsRaw ? "no-chap-hdd" : "no-chap";
        }
    }
}
