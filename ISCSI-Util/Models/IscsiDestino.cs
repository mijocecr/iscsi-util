namespace ISCSI_Util.Models;

public class IscsiDestino
{
    // ============================================================
    // IDENTIDAD DEL DESTINO
    // ============================================================
    public string Ip { get; set; } = "";
    public string Iqn { get; set; } = "";

    // ============================================================
    // ESTADO DE CONEXIÓN
    // ============================================================
    public bool Conectado { get; set; } = false;
    public bool Seleccionado { get; set; } = false;

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
    // PERSISTENCIA
    // ============================================================
    public bool Persistir { get; set; } = false;

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
    // PROPIEDADES DERIVADAS (MUY ÚTILES)
    // ============================================================

    // Indica si NO hay ningún tipo de CHAP
    public bool SinChap => !UsaChap && !UsaMutualChap;

    // Indica si NO tiene filesystem (más claro para iconos)
    public bool EsHdd => !TieneFilesystem;

    // ============================================================
    // ICONO (OPCIONAL, SI QUIERES CENTRALIZARLO AQUÍ)
    // ============================================================
    public string Icono
    {
        get
        {
            if (UsaMutualChap)
                return EsHdd ? "chap-mutual-hdd" : "chap-mutual";

            if (UsaChap)
                return EsHdd ? "chap-hdd" : "chap";

            return EsHdd ? "no-chap-hdd" : "no-chap";
        }
    }
}
