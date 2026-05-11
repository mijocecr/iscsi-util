using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ISCSI_Util.Models;
using ISCSI_Util.Helpers;
using ISCSI_Util.Services;

namespace ISCSI_Util.Helpers;

public static class IscsiHelper
{
    // ============================================================
    //  INFRAESTRUCTURA DE TRAZAS
    // ============================================================

    private static long _traceCounter = 0;
    private static long NextTraceId() => ++_traceCounter;

    private static void TraceIn(long id, string method, string details = "")
    {
        LogService.Debug($"[ISCSI] #{id} → {method} {details}");
    }

    private static void TraceOut(long id, string method, string result = "OK")
    {
        LogService.Debug($"[ISCSI] #{id} ← {method} [{result}]");
    }

    // ============================================================
    //  SANITIZAR NOMBRE PARA ARCHIVOS Y SYSTEMD
    // ============================================================

    public static string SanitizarNombre(string iqn)
    {
        char[] invalid = Path.GetInvalidFileNameChars()
            .Concat(new[] { ':', '/', '\\', ' ' })
            .ToArray();

        return new string(iqn.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static string SystemdSafe(string s)
    {
        return s.Replace(":", "_")
                .Replace(".", "_")
                .Replace("-", "_")
                .Replace("/", "_");
    }

    // ============================================================
    //  DETECTAR FILESYSTEM
    // ============================================================

    private static string DetectarFsType(string blkidOut)
    {
        if (blkidOut.Contains("TYPE=\"ext2\"")) return "ext2";
        if (blkidOut.Contains("TYPE=\"ext3\"")) return "ext3";
        if (blkidOut.Contains("TYPE=\"ext4\"")) return "ext4";
        if (blkidOut.Contains("TYPE=\"xfs\"")) return "xfs";
        if (blkidOut.Contains("TYPE=\"btrfs\"")) return "btrfs";
        if (blkidOut.Contains("TYPE=\"f2fs\"")) return "f2fs";
        if (blkidOut.Contains("TYPE=\"ntfs\"")) return "ntfs";
        if (blkidOut.Contains("TYPE=\"vfat\"")) return "vfat";
        if (blkidOut.Contains("TYPE=\"exfat\"")) return "exfat";
        if (blkidOut.Contains("TYPE=\"iso9660\"")) return "iso9660";
        return "ext4";
    }

    // ============================================================
    //  DETECTAR CHAP / MUTUAL CHAP
    // ============================================================

  public static void DetectarChap(IscsiDestino d)
{
    try
    {
        // Crear nodo temporal si no existe
        var check = ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip}:3260"
        );

        bool nodoExiste = !check.Stderr.Contains("No records found", StringComparison.OrdinalIgnoreCase);

        if (!nodoExiste)
        {
            ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {d.Iqn} -p {d.Ip}:3260 --op=new"
            );
        }

        // Leer configuración real
        var show = ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip}:3260 -o show"
        );

        string config = show.Stdout ?? "";

        string authMethod = ExtraerValor(config, "node.session.auth.authmethod");
        string user      = ExtraerValor(config, "node.session.auth.username");
        string pass      = ExtraerValor(config, "node.session.auth.password");
        string userIn    = ExtraerValor(config, "node.session.auth.username_in");
        string passIn    = ExtraerValor(config, "node.session.auth.password_in");

        bool chapEnabled = authMethod.Equals("CHAP", StringComparison.OrdinalIgnoreCase);

        bool userEmpty   = string.IsNullOrWhiteSpace(user)   || user   == "<empty>";
        bool passEmpty   = string.IsNullOrWhiteSpace(pass)   || pass   == "<empty>";
        bool userInEmpty = string.IsNullOrWhiteSpace(userIn) || userIn == "<empty>";
        bool passInEmpty = string.IsNullOrWhiteSpace(passIn) || passIn == "<empty>";

        d.UsaChap        = chapEnabled && !userEmpty && !passEmpty;
        d.UsaMutualChap  = chapEnabled && !userInEmpty && !passInEmpty;

        d.UsuarioChap        = userEmpty   ? "" : user;
        d.PasswordChap       = passEmpty   ? "" : pass;
        d.UsuarioMutualChap  = userInEmpty ? "" : userIn;
        d.PasswordMutualChap = passInEmpty ? "" : passIn;

        // Si el nodo fue creado temporalmente, borrarlo
        if (!nodoExiste)
        {
            ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {d.Iqn} -p {d.Ip}:3260 --op=delete"
            );
        }
    }
    catch
    {
        d.UsaChap = false;
        d.UsaMutualChap = false;
    }
}

    
    
    private static string ExtraerValor(string config, string key)
    {
        if (string.IsNullOrWhiteSpace(config) || string.IsNullOrWhiteSpace(key))
            return "";

        foreach (var line in config.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Split('=', 2);
                if (parts.Length == 2)
                    return parts[1].Trim();
            }
        }

        return "";
    }

    

    // ============================================================
    //  DISCOVER — Descubrir destinos iSCSI
    // ============================================================

 public static async Task<List<IscsiDestino>> Descubrir(string ip)
{
    long id = NextTraceId();
    TraceIn(id, "Descubrir", $"IP='{ip}'");

    var destinos = new List<IscsiDestino>();

    using (LoadingService.Show($"Discovering targets at {ip}..."))
    {
        try
        {
            // --------------------------------------------------------------
            // 1) Discovery (única operación lenta)
            // --------------------------------------------------------------
            var discovery = await Task.Run(() =>
                ShellHelper.EjecutarComoRoot($"iscsiadm -m discovery -t sendtargets -p {ip}")
            );

            if (string.IsNullOrWhiteSpace(discovery.Stdout))
                return destinos;

            // Sesiones activas
            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout;

            // --------------------------------------------------------------
            // 2) Parseo rápido + FILTRO por portal solicitado
            // --------------------------------------------------------------
            foreach (var line in discovery.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.Contains("iqn.")) continue;

                var partes = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                var portalRaw = partes[0];
                var portal = portalRaw.Split(',')[0];

                if (!portal.Contains(":"))
                    portal = $"{portal}:3260";

                // 🔥 FILTRO: solo aceptar targets del portal solicitado
                if (!portal.StartsWith(ip))
                    continue;

                string iqn = partes.LastOrDefault(s => s.StartsWith("iqn."));
                if (string.IsNullOrWhiteSpace(iqn))
                    continue;

                bool conectado = sesiones.Contains(iqn, StringComparison.OrdinalIgnoreCase);

                var d = new IscsiDestino
                {
                    Ip = portal,
                    PortalReal = portal,
                    Iqn = iqn,
                    Conectado = conectado,
                    Seleccionado = false,
                    TieneFilesystem = false
                };

                destinos.Add(d);
            }

            // --------------------------------------------------------------
            // 3) Detectar CHAP en paralelo (rápido, no bloquea UI)
            // --------------------------------------------------------------
            await Task.Run(() =>
            {
                foreach (var d in destinos)
                {
                    var chap = IscsiChapDetector.Detect(d);

                    d.RequiresChap = chap.RequiresChap;
                    d.RequiresMutualChap = chap.RequiresMutualChap;
                    d.HasLocalChapConfigured = chap.HasLocalChapConfigured;
                    d.HasLocalMutualConfigured = chap.HasLocalMutualConfigured;

                    d.LocalUser = chap.LocalUser;
                    d.LocalPass = chap.LocalPass;
                    d.LocalUserIn = chap.LocalUserIn;
                    d.LocalPassIn = chap.LocalPassIn;

                    d.UsaChap = d.RequiresChap || d.HasLocalChapConfigured;
                    d.UsaMutualChap = d.RequiresMutualChap || d.HasLocalMutualConfigured;
                }
            });

            TraceOut(id, "Descubrir");
            return destinos;
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR Descubrir: {ex.Message}");
            return destinos;
        }
    }
}



    // ============================================================
    //  COMPLETAR INFORMACIÓN — DevicePath, PartitionPath, FS
    // ============================================================

    public static async Task CompletarInformacionDestino(IscsiDestino d, long parentId)
{
    long id = NextTraceId();
    TraceIn(id, "CompletarInformacion", d.Iqn);

    try
    {
        // --------------------------------------------------------------
        // 0) Si no está conectado → limpiar y salir
        // --------------------------------------------------------------
        if (!d.Conectado)
        {
            d.TieneFilesystem = false;
            d.FsType = "";
            d.MountPoint = "";
            return;
        }

        // --------------------------------------------------------------
        // 1) Detectar symlink en /dev/disk/by-path
        // --------------------------------------------------------------
        var byPath = ShellHelper.EjecutarComoRoot("ls -1 /dev/disk/by-path/").Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var match = byPath.FirstOrDefault(l =>
            l.Contains(d.Ip, StringComparison.OrdinalIgnoreCase) &&
            l.Contains("lun", StringComparison.OrdinalIgnoreCase)
        );

        if (match != null)
            d.DevicePath = "/dev/disk/by-path/" + match.Trim();
        else
            return;

        // --------------------------------------------------------------
        // 2) Detectar partición
        // --------------------------------------------------------------
        var lsblk = ShellHelper.EjecutarComoRoot($"lsblk -rno NAME {d.DevicePath}");
        var lines = lsblk.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        d.PartitionPath = lines.Length > 1
            ? "/dev/" + lines[1].Trim()
            : d.DevicePath;

        // --------------------------------------------------------------
        // 3) Detectar mountpoint
        // --------------------------------------------------------------
        var mounts = ShellHelper.EjecutarComoRoot("mount").Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var mline = mounts.FirstOrDefault(l => l.Contains(d.PartitionPath));
        if (mline != null)
        {
            var parts = mline.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
                d.MountPoint = parts[2];
        }

        // --------------------------------------------------------------
        // 4) Detectar filesystem
        // --------------------------------------------------------------
        var blkid = ShellHelper.EjecutarComoRoot($"blkid -p {d.PartitionPath}");

        d.TieneFilesystem =
            !string.IsNullOrWhiteSpace(blkid.Stdout) &&
            blkid.Stdout.Contains("TYPE=");

        if (d.TieneFilesystem)
            d.FsType = DetectarFsType(blkid.Stdout);

     

        // Flags usados por la UI
        d.UsaChap = d.RequiresChap || d.HasLocalChapConfigured;
        d.UsaMutualChap = d.RequiresMutualChap || d.HasLocalMutualConfigured;

        TraceOut(id, "CompletarInformacion");
    }
    catch (Exception ex)
    {
        LogService.Error($"[ISCSI] #{id} ERROR CompletarInformacion: {ex.Message}");
    }
}


// ======================================================================
//  CONECTAR — Login iSCSI + detección + montaje
// ======================================================================

public static async Task Conectar(IscsiDestino d)
{
    long id = NextTraceId();
    TraceIn(id, "Conectar", d.Iqn);

    using (LoadingService.Show($"Connecting to {d.Iqn}..."))
    {
        try
        {
            // --------------------------------------------------------------
            // 1) Crear mountpoint si no existe
            // --------------------------------------------------------------
            string basePath = ConfigManager.MountBasePath;
            d.MountPoint = Path.Combine(basePath, SanitizarNombre(d.Iqn));

            if (!Directory.Exists(d.MountPoint))
            {
                Directory.CreateDirectory(d.MountPoint);
                ShellHelper.EjecutarComoRoot(
                    $"chmod {ConfigManager.DefaultPermissions} \"{d.MountPoint}\""
                );
            }

            // --------------------------------------------------------------
            // 2) Comprobar si ya está conectado
            // --------------------------------------------------------------
            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout;
            bool yaConectado = sesiones.Contains(d.Iqn, StringComparison.OrdinalIgnoreCase);

            // --------------------------------------------------------------
            // 3) LOGIN iSCSI (solo si no está conectado)
            // --------------------------------------------------------------
            if (!yaConectado)
            {
                // ----------------------------------------------------------
                // 3A) Comprobar si el nodo ya existe (evita op=new)
                // ----------------------------------------------------------
                var checkNode = ShellHelper.EjecutarComoRoot(
                    $"iscsiadm -m node -T {d.Iqn} -p {d.Ip}"
                );

                bool nodoExiste = !checkNode.Stderr.Contains("No records found");

                // ----------------------------------------------------------
                // 3B) Crear nodo solo si no existe
                // ----------------------------------------------------------
                if (!nodoExiste)
                {
                    ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=new"
                    );
                }

                // ----------------------------------------------------------
                // 3C) Aplicar CHAP solo si el usuario lo configuró
                // ----------------------------------------------------------
                if (d.UsaChap || d.UsaMutualChap)
                {
                    ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=update --name node.session.auth.authmethod --value=CHAP"
                    );

                    if (d.UsaChap)
                    {
                        string user = string.IsNullOrWhiteSpace(d.UsuarioChap) ? d.LocalUser : d.UsuarioChap;
                        string pass = string.IsNullOrWhiteSpace(d.PasswordChap) ? d.LocalPass : d.PasswordChap;

                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=update --name node.session.auth.username --value=\"{user}\""
                        );

                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=update --name node.session.auth.password --value=\"{pass}\""
                        );
                    }

                    if (d.UsaMutualChap)
                    {
                        string userIn = string.IsNullOrWhiteSpace(d.UsuarioMutualChap) ? d.LocalUserIn : d.UsuarioMutualChap;
                        string passIn = string.IsNullOrWhiteSpace(d.PasswordMutualChap) ? d.LocalPassIn : d.PasswordMutualChap;

                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=update --name node.session.auth.username_in --value=\"{userIn}\""
                        );

                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=update --name node.session.auth.password_in --value=\"{passIn}\""
                        );
                    }
                }

                // ----------------------------------------------------------
                // 3D) LOGIN con timeout
                // ----------------------------------------------------------
                var loginTask = Task.Run(() =>
                    ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --login"
                    )
                );

                var completed = await Task.WhenAny(loginTask, Task.Delay(5000));
                if (completed != loginTask)
                    throw new Exception("TIMEOUT en login iSCSI");

                await Task.Delay(300);
            }

            // --------------------------------------------------------------
            // 4) Detectar symlink
            // --------------------------------------------------------------
            d.DevicePath = null;

            for (int i = 0; i < 10; i++)
            {
                var byPath = ShellHelper.EjecutarComoRoot("ls -1 /dev/disk/by-path/").Stdout
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries);

                var match = byPath.FirstOrDefault(l =>
                    l.Contains(d.Ip, StringComparison.OrdinalIgnoreCase) &&
                    l.Contains("lun", StringComparison.OrdinalIgnoreCase)
                );

                if (match != null)
                {
                    d.DevicePath = "/dev/disk/by-path/" + match.Trim();
                    break;
                }

                await Task.Delay(200);
            }

            if (string.IsNullOrWhiteSpace(d.DevicePath))
                throw new Exception("No se encontró symlink del dispositivo iSCSI.");

            // --------------------------------------------------------------
            // 5) Detectar partición
            // --------------------------------------------------------------
            var lsblk = ShellHelper.EjecutarComoRoot($"lsblk -rno NAME {d.DevicePath}");
            var lines = lsblk.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            d.PartitionPath = lines.Length > 1
                ? "/dev/" + lines[1].Trim()
                : d.DevicePath;

            // --------------------------------------------------------------
            // 6) Detectar filesystem
            // --------------------------------------------------------------
            var blkid = ShellHelper.EjecutarComoRoot($"blkid {d.PartitionPath}");

            if (string.IsNullOrWhiteSpace(blkid.Stdout))
            {
                d.TieneFilesystem = false;
                d.FsType = "";
                d.Conectado = true;
                TraceOut(id, "Conectar", "NO_FS");
                return;
            }

            d.TieneFilesystem = true;
            d.FsType = DetectarFsType(blkid.Stdout);

            // --------------------------------------------------------------
            // 7) Montar
            // --------------------------------------------------------------
            var mpCheck = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"");

            if (mpCheck.ExitCode != 0)
            {
                string mountFs = d.FsType == "ntfs" ? "ntfs-3g" : d.FsType;

                ShellHelper.EjecutarComoRoot(
                    $"mount -t {mountFs} {d.PartitionPath} \"{d.MountPoint}\""
                );
            }

            d.Conectado = true;
            NotificadorLinux.Enviar($"Target {d.Iqn} mounted in {d.MountPoint}");

            TraceOut(id, "Conectar");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR Conectar: {ex.Message}");
            NotificadorLinux.Enviar($"[ERROR] Failed to connect target {d.Iqn}", 6000, "critical");
        }
    }
}


// ======================================================================
//  OBTENER PORTAL REAL — universal, robusto, multi-servidor
// ======================================================================


    public static string? ObtenerPortalReal(IscsiDestino d)
    {
        try
        {
            var result = ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {d.Iqn}"
            );

            if (string.IsNullOrWhiteSpace(result.Stdout))
                return null;

            var line = result.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(l => l.Contains(d.Iqn));

            if (line == null)
                return null;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return null;

            var portal = parts[0].Trim();

            // ❌ Ignorar basura tipo "node.name"
            if (portal.StartsWith("node.", StringComparison.OrdinalIgnoreCase))
                return null;

            // ❌ Ignorar cosas que no parezcan IP:PUERTO
            if (!portal.Contains('.') || !portal.Contains(':'))
                return null;

            return portal;
        }
        catch
        {
            return null;
        }
    }



// ======================================================================
//  PERSISTENCIA — EXACTAMENTE COMO EL HELPER ORIGINAL + PORTAL REAL
// ======================================================================
public static async Task AplicarPersistencia(IscsiDestino d)
{
    long id = NextTraceId();
    TraceIn(id, "AplicarPersistencia", d.Iqn);

    using (LoadingService.Show($"Applying persistence for {d.Iqn}..."))
    {
        try
        {
            if (!Directory.Exists(d.MountPoint))
            {
                Directory.CreateDirectory(d.MountPoint);
                ShellHelper.EjecutarComoRoot(
                    $"chmod {ConfigManager.DefaultPermissions} \"{d.MountPoint}\""
                );
            }

            // ============================================================
            //  NUEVO: detectar portal real registrado por iscsiadm
            // ============================================================
            string? portalReal = ObtenerPortalReal(d);
            if (!string.IsNullOrWhiteSpace(portalReal))
            {
                d.Ip = portalReal;
                LogService.Debug($"[ISCSI] Portal real detectado: {portalReal}");
            }
            else
            {
                LogService.Debug($"[ISCSI] No se pudo detectar portal real, usando d.Ip actual: {d.Ip}");
            }

            if (d.Persistir)
            {
                await GuardarEnFstab_Original(d, id);
                await CrearServicioPersistencia_Original(d, id);

                ShellHelper.EjecutarComoRoot("systemctl daemon-reload");
                ShellHelper.EjecutarComoRoot($"systemctl enable iscsi-{SystemdSafe(d.Iqn)}.service");
            }
            else
            {
                await EliminarPersistencia_Original(d, id);
                ShellHelper.EjecutarComoRoot("systemctl daemon-reload");
            }

            TraceOut(id, "AplicarPersistencia");
            NotificadorLinux.Enviar($"Persistence updated for {d.Iqn}", 4000, "normal");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR AplicarPersistencia: {ex.Message}");
            NotificadorLinux.Enviar($"[ERROR] Failed to apply persistence for {d.Iqn}", 6000, "critical");
        }
    }
}








// ======================================================================
//  FSTAB — EXACTAMENTE COMO EL ORIGINAL (UUID + _netdev)
// ======================================================================

private static async Task GuardarEnFstab_Original(IscsiDestino d, long id)
{
    using (LoadingService.Show($"Updating fstab for {d.Iqn}..."))
    {
        try
        {
            var blkid = ShellHelper.EjecutarComoRoot($"blkid {d.PartitionPath}");
            string uuid = blkid.Stdout.Split(' ')
                .FirstOrDefault(s => s.StartsWith("UUID="))?
                .Replace("UUID=", "")
                .Trim('"');

            if (string.IsNullOrWhiteSpace(uuid))
            {
                LogService.Error($"[ISCSI] #{id} No se pudo obtener UUID.");
                return;
            }

            string entry = $"UUID={uuid} {d.MountPoint} auto _netdev 0 0";

            string mpEsc = d.MountPoint.Replace("/", "\\/");

            await Task.Run(() =>
            {
                ShellHelper.EjecutarComoRoot($"sed -i '\\#{mpEsc}#d' /etc/fstab");
                ShellHelper.EjecutarComoRoot($"bash -c 'echo \"{entry}\" >> /etc/fstab'");
            });

            TraceOut(id, "GuardarEnFstab_Original");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR GuardarEnFstab_Original: {ex.Message}");
        }
    }
}

// ======================================================================
//  CREAR SERVICIO SYSTEMD — EXACTAMENTE COMO EL ORIGINAL + PORTAL REAL
// ======================================================================





private static async Task CrearServicioPersistencia_Original(IscsiDestino d, long id)
{
    try
    {
        string safe = SystemdSafe(d.Iqn);

        string scriptPath = $"/usr/local/bin/mount-iscsi-{safe}.sh";
        string servicePath = $"/etc/systemd/system/iscsi-{safe}.service";

        string scriptContent =
$@"#!/bin/bash
# VMCF_2026
TARGET=""{d.Iqn}""
PORTAL=""{d.Ip}""
MOUNTPOINT=""{d.MountPoint}""

if [ ""{d.UsuarioChap}"" != """" ]; then
  iscsiadm -m node -T ""$TARGET"" -p ""$PORTAL"" --op=update --name node.session.auth.authmethod --value=CHAP
  iscsiadm -m node -T ""$TARGET"" -p ""$PORTAL"" --op=update --name node.session.auth.username --value=""{d.UsuarioChap}""
  iscsiadm -m node -T ""$TARGET"" -p ""$PORTAL"" --op=update --name node.session.auth.password --value=""{d.PasswordChap}""
fi

if [ ""{d.UsuarioMutualChap}"" != """" ]; then
  iscsiadm -m node -T ""$TARGET"" -p ""$PORTAL"" --op=update --name node.session.auth.username_in --value=""{d.UsuarioMutualChap}""
  iscsiadm -m node -T ""$TARGET"" -p ""$PORTAL"" --op=update --name node.session.auth.password_in --value=""{d.PasswordMutualChap}""
fi

if ! iscsiadm -m session | grep -q ""$TARGET""; then
  iscsiadm -m node -T ""$TARGET"" -p ""$PORTAL"" --login
  for i in {{1..10}}; do
    if ls /dev/disk/by-path/*""$TARGET""*lun* &>/dev/null; then
      break
    fi
    sleep 1
  done
fi

mount -a -O _netdev
exit 0
";

        // 🔥 ESCRIBIR DIRECTAMENTE DESDE C#
        File.WriteAllText("/tmp/tmp_script.sh", scriptContent);

        // 🔥 MOVERLO A /usr/local/bin CON PERMISOS ROOT
        ShellHelper.EjecutarComoRoot($"mv /tmp/tmp_script.sh {scriptPath}");
        ShellHelper.EjecutarComoRoot($"chmod 755 {scriptPath}");
        ShellHelper.EjecutarComoRoot($"chown root:root {scriptPath}");

        string serviceContent =
$@"[Unit]
Description=Conectar iSCSI y montar {d.Iqn}
After=network-online.target NetworkManager-wait-online.service iscsid.service iscsi.service remote-fs.target
Requires=network-online.target NetworkManager-wait-online.service iscsid.service iscsi.service
Before=remote-fs-pre.target
Wants=remote-fs-pre.target

[Service]
Type=oneshot
ExecStart={scriptPath}
RemainAfterExit=yes
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
";

        File.WriteAllText("/tmp/tmp_service.service", serviceContent);

        ShellHelper.EjecutarComoRoot($"mv /tmp/tmp_service.service {servicePath}");
        ShellHelper.EjecutarComoRoot($"chmod 644 {servicePath}");
        ShellHelper.EjecutarComoRoot($"chown root:root {servicePath}");

        TraceOut(id, "CrearServicioPersistencia_Original");
    }
    catch (Exception ex)
    {
        LogService.Error($"[ISCSI] #{id} ERROR CrearServicioPersistencia_Original: {ex.Message}");
    }

    await Task.CompletedTask;
}






// ======================================================================
//  ELIMINAR PERSISTENCIA — EXACTAMENTE COMO EL ORIGINAL
// ======================================================================

private static async Task EliminarPersistencia_Original(IscsiDestino d, long id)
{
    try
    {
        string safe = SystemdSafe(d.Iqn);

        string scriptPath = $"/usr/local/bin/mount-iscsi-{safe}.sh";
        string servicePath = $"/etc/systemd/system/iscsi-{safe}.service";

        // 1. Deshabilitar servicio
        ShellHelper.EjecutarComoRoot($"systemctl disable iscsi-{safe}.service");

        // 2. Eliminar servicio
        ShellHelper.EjecutarComoRoot($"rm -f {servicePath}");

        // 3. Eliminar script
        ShellHelper.EjecutarComoRoot($"rm -f {scriptPath}");

        // 4. Eliminar entrada fstab
        string mpEsc = d.MountPoint.Replace("/", "\\/");
        ShellHelper.EjecutarComoRoot($"sed -i '\\#{mpEsc}#d' /etc/fstab");

        // 5. Dejar node.startup en manual
        ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op update --name node.startup --value manual"
        );

        TraceOut(id, "EliminarPersistencia_Original");
    }
    catch (Exception ex)
    {
        LogService.Error($"[ISCSI] #{id} ERROR EliminarPersistencia_Original: {ex.Message}");
    }

    await Task.CompletedTask;
}

// ======================================================================
//  DETECTAR PERSISTENCIA — EXACTAMENTE COMO EL ORIGINAL
// ======================================================================

public static bool DetectarPersistencia(IscsiDestino d)
{
    if (d == null || string.IsNullOrWhiteSpace(d.MountPoint))
        return false;

    // FSTAB
    if (File.Exists("/etc/fstab"))
    {
        string fstab = File.ReadAllText("/etc/fstab");
        if (fstab.Contains(d.MountPoint))
            return true;
    }

    // Servicio systemd
    string safe = SystemdSafe(d.Iqn);
    string service = $"/etc/systemd/system/iscsi-{safe}.service";

    return File.Exists(service);
}

// ======================================================================
//  DESCONECTAR — desmontaje + logout + limpieza
// ======================================================================

public static async Task Desconectar(IscsiDestino d)
{
    long id = NextTraceId();
    TraceIn(id, "Desconectar", d.Iqn);

    using (LoadingService.Show($"Disconnecting {d.Iqn}..."))
    {
        try
        {
            // --------------------------------------------------------------
            // 1) Desmontar si está montado
            // --------------------------------------------------------------
            if (!string.IsNullOrWhiteSpace(d.MountPoint))
            {
                var mpCheck = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"");

                if (mpCheck.ExitCode == 0)
                {
                    // Lazy unmount primero
                    ShellHelper.EjecutarComoRoot($"umount -l \"{d.MountPoint}\"");
                    await Task.Delay(300);

                    // Si sigue montado, forzar
                    mpCheck = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"");
                    if (mpCheck.ExitCode == 0)
                    {
                        ShellHelper.EjecutarComoRoot($"umount -f \"{d.MountPoint}\"");
                        await Task.Delay(200);
                    }
                }
            }

            // --------------------------------------------------------------
            // 2) Eliminar directorio de montaje
            // --------------------------------------------------------------
            if (!string.IsNullOrWhiteSpace(d.MountPoint) &&
                Directory.Exists(d.MountPoint))
            {
                ShellHelper.EjecutarComoRoot($"rm -rf \"{d.MountPoint}\"");
            }

            // --------------------------------------------------------------
            // 3) Logout iSCSI (solo si existe sesión activa)
            // --------------------------------------------------------------
            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout;

            if (!string.IsNullOrWhiteSpace(sesiones) &&
                sesiones.Contains(d.Iqn, StringComparison.OrdinalIgnoreCase))
            {
                var logoutTask = Task.Run(() =>
                    ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --logout"
                    )
                );

                var completed = await Task.WhenAny(logoutTask, Task.Delay(5000));
                await Task.Delay(300);
            }

            // --------------------------------------------------------------
            // 4) Reset de propiedades (pero NO borrar nodo ni CHAP)
            // --------------------------------------------------------------
            d.Conectado = false;
            d.TieneFilesystem = false;
            d.DevicePath = null;
            d.PartitionPath = null;
            d.MountPoint = null;
            d.FsType = null;

            // Mantener CHAP detectado y configurado
            // Mantener persistencia (solo se aplica en Conectar)

            NotificadorLinux.Enviar($"Target {d.Iqn} disconnected");
            TraceOut(id, "Desconectar");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR Desconectar: {ex.Message}");
        }
    }
}


// ======================================================================
//  DESCONECTAR + BORRAR NODO — versión completa
// ======================================================================

public static async Task Desconectar_Borrar(IscsiDestino d)
{
    long id = NextTraceId();
    TraceIn(id, "Desconectar_Borrar", d.Iqn);

    using (LoadingService.Show($"Removing {d.Iqn}..."))
    {
        try
        {
            // --------------------------------------------------------------
            // 1) Desmontar si está montado
            // --------------------------------------------------------------
            if (!string.IsNullOrWhiteSpace(d.MountPoint))
            {
                var mpCheck = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"");

                if (mpCheck.ExitCode == 0)
                {
                    // Lazy unmount
                    ShellHelper.EjecutarComoRoot($"umount -l \"{d.MountPoint}\"");
                    await Task.Delay(300);

                    // Si sigue montado → forzar
                    mpCheck = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"");
                    if (mpCheck.ExitCode == 0)
                    {
                        ShellHelper.EjecutarComoRoot($"umount -f \"{d.MountPoint}\"");
                        await Task.Delay(200);
                    }
                }
            }

            // --------------------------------------------------------------
            // 2) Eliminar directorio de montaje
            // --------------------------------------------------------------
            if (!string.IsNullOrWhiteSpace(d.MountPoint) &&
                Directory.Exists(d.MountPoint))
            {
                ShellHelper.EjecutarComoRoot($"rm -rf \"{d.MountPoint}\"");
            }

            // --------------------------------------------------------------
            // 3) Logout iSCSI si hay sesión activa
            // --------------------------------------------------------------
            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout;

            if (!string.IsNullOrWhiteSpace(sesiones) &&
                sesiones.Contains(d.Iqn, StringComparison.OrdinalIgnoreCase))
            {
                var logoutTask = Task.Run(() =>
                    ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --logout"
                    )
                );

                await Task.WhenAny(logoutTask, Task.Delay(5000));
                await Task.Delay(300);
            }

            // --------------------------------------------------------------
            // 4) Eliminar persistencia (fstab + systemd)
            // --------------------------------------------------------------
            string safe = d.SafeName;

            // fstab
            ShellHelper.EjecutarComoRoot(
                $"sed -i '/{safe}/d' /etc/fstab"
            );

            // systemd mount unit
            ShellHelper.EjecutarComoRoot(
                $"rm -f /etc/systemd/system/{safe}.mount"
            );

            // systemd automount unit
            ShellHelper.EjecutarComoRoot(
                $"rm -f /etc/systemd/system/{safe}.automount"
            );

            // recargar systemd
            ShellHelper.EjecutarComoRoot("systemctl daemon-reload");

            // --------------------------------------------------------------
            // 5) Eliminar nodo iSCSI
            // --------------------------------------------------------------
            ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=delete"
            );

            // --------------------------------------------------------------
            // 6) Eliminar discoverydb (si existe)
            // --------------------------------------------------------------
            ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m discoverydb -t sendtargets -p {d.Ip} --op=delete"
            );

            // --------------------------------------------------------------
            // 7) Reset completo del objeto
            // --------------------------------------------------------------
            d.Conectado = false;
            d.TieneFilesystem = false;
            d.DevicePath = null;
            d.PartitionPath = null;
            d.MountPoint = null;
            d.FsType = null;

            // CHAP detectado → limpiar porque el nodo ya no existe
            d.RequiresChap = false;
            d.RequiresMutualChap = false;
            d.HasLocalChapConfigured = false;
            d.HasLocalMutualConfigured = false;

            d.LocalUser = "";
            d.LocalPass = "";
            d.LocalUserIn = "";
            d.LocalPassIn = "";

            // CHAP configurado por el usuario → limpiar también
            d.UsaChap = false;
            d.UsaMutualChap = false;

            d.UsuarioChap = "";
            d.PasswordChap = "";
            d.UsuarioMutualChap = "";
            d.PasswordMutualChap = "";

            // Persistencia
            d.Persistir = false;
            d.PersistenteReal = false;

            NotificadorLinux.Enviar($"Target {d.Iqn} fully removed");
            TraceOut(id, "Desconectar_Borrar");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR Desconectar_Borrar: {ex.Message}");
        }
    }
}


// ======================================================================
//  INICIALIZAR DESTINO — GPT + partición + formateo + montaje
// ======================================================================

public static async Task InicializarDestino(IscsiDestino d, string label, string fsType)
{
    long id = NextTraceId();
    TraceIn(id, "InicializarDestino", d.Iqn);

    using (LoadingService.Show($"Initializing disk ({fsType})..."))
    {
        try
        {
            if (!d.Conectado)
                await Conectar(d);

            var task = Task.Run(async () =>
            {
                // 1) Desmontar
                var mpCheck = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"");
                if (mpCheck.ExitCode == 0)
                {
                    ShellHelper.EjecutarComoRoot($"umount -l \"{d.MountPoint}\"");
                    await Task.Delay(300);
                }

                // 2) Borrar tabla
                ShellHelper.EjecutarComoRoot($"sgdisk --zap-all {d.PartitionPath}");

                // 3) Crear GPT
                ShellHelper.EjecutarComoRoot($"parted -s {d.PartitionPath} mklabel gpt");

                // 4) Crear partición
                ShellHelper.EjecutarComoRoot($"parted -s {d.PartitionPath} mkpart primary 0% 100%");
                await Task.Delay(1200);

                // 5) Detectar nueva partición
                var lsblk = ShellHelper.EjecutarComoRoot($"lsblk -rno NAME {d.DevicePath}");
                var lines = lsblk.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                d.PartitionPath = lines.Length > 1
                    ? "/dev/" + lines[1].Trim()
                    : d.DevicePath;

                // 6) Formatear
                string mkfs = fsType switch
                {
                    "ext4" => $"mkfs.ext4 -F {d.PartitionPath}",
                    "xfs" => $"mkfs.xfs -f {d.PartitionPath}",
                    "btrfs" => $"mkfs.btrfs -f {d.PartitionPath}",
                    _ => $"mkfs.ext4 -F {d.PartitionPath}"
                };

                ShellHelper.EjecutarComoRoot(mkfs);

                d.TieneFilesystem = true;
                d.FsType = fsType;

                // 7) Montar
                ShellHelper.EjecutarComoRoot(
                    $"mount -t {fsType} {d.PartitionPath} \"{d.MountPoint}\""
                );
            });

            await task;

            NotificadorLinux.Enviar($"Target {d.Iqn} initialized and mounted");
            TraceOut(id, "InicializarDestino");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR InicializarDestino: {ex.Message}");
        }
    }
}

// ======================================================================
//  SOPORTA FILESYSTEM — requerido por InitializeDiskDialogService
// ======================================================================
public static bool SoportaFs(string fs)
{
    if (string.IsNullOrWhiteSpace(fs))
        return false;

    fs = fs.ToLowerInvariant();

    return fs switch
    {
        "ext4" => true,
        "xfs"  => true,
        "btrfs" => true,
        "ext3" => true,
        "ext2" => true,
        _ => false
    };
}




}
