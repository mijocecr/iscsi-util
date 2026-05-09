using System;

namespace ISCSI_Util.Models;

public class IscsiDestino
{
    // ============================================================
    // IDENTIDAD DEL DESTINO
    // ============================================================
    public string Ip { get; set; } = "";          // Portal original
    public string Iqn { get; set; } = "";         // Identificador único

    // Portal real (puede cambiar tras discovery)
    public string PortalReal { get; set; } = "";

    // ============================================================
    // ESTADO DE CONEXIÓN
    // ============================================================
    public bool Conectado { get; set; } = false;
    public bool Seleccionado { get; set; } = false;

    // Cuándo se conectó (si se puede detectar)
    public DateTime? ConnectedSince { get; set; } = null;

    // ============================================================
    // RUTAS DEL SISTEMA
    // ============================================================
    public string? DevicePath { get; set; } = null;      // /dev/disk/by-path/...
    public string? PartitionPath { get; set; } = null;   // /dev/sdX1
    public string? MountPoint { get; set; } = null;      // /mnt/iscsi/...

    // ============================================================
    // FILESYSTEM
    // ============================================================
    public bool TieneFilesystem { get; set; } = false;
    public string FsType { get; set; } = "";             // ext4, xfs, raw...

    // ============================================================
    // INFORMACIÓN DEL DISCO
    // ============================================================
    public string Vendor { get; set; } = "";             // QNAP, Synology, iSCSI, etc.
    public string Model { get; set; } = "";              // Virtual Disk, iSCSI Disk...
    public int SizeGb { get; set; } = 0;                 // Tamaño aproximado
    public int LunId { get; set; } = 0;                  // LUN detectado

    // ============================================================
    // PERSISTENCIA
    // ============================================================
    public bool Persistir { get; set; } = false;         // Lo que el usuario quiere
    public bool PersistenteReal { get; set; } = false;   // Lo que detecta el sistema

    // Nombre seguro para systemd
    public string SafeName =>
        Iqn.Replace(":", "_")
           .Replace(".", "_")
           .Replace("-", "_")
           .Replace("/", "_");

    // ============================================================
    // CHAP
    // ============================================================
    public bool UsaChap { get; set; } = false;
    public string UsuarioChap { get; set; } = "";
    public string PasswordChap { get; set; } = "";

    // ============================================================
    // MUTUAL CHAP
    // ============================================================
    public bool UsaMutualChap { get; set; } = false;
    public string UsuarioMutualChap { get; set; } = "";
    public string PasswordMutualChap { get; set; } = "";

    // ============================================================
    // PROPIEDADES DERIVADAS (MUY ÚTILES PARA LA UI)
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
    // ICONO (OPCIONAL)
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
