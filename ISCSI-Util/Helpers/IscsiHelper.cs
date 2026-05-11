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
        var iqn = d.Iqn?.ToLowerInvariant() ?? string.Empty;

        if (iqn.Contains("mycloudex2ultra") || iqn.Contains("mycloud"))
        {
            d.UsaChap = false;
            d.UsaMutualChap = false;
            return;
        }

        if (iqn.Contains("mutualchap"))
        {
            d.UsaChap = true;
            d.UsaMutualChap = true;
            return;
        }

        if (iqn.Contains("bak") || iqn.Contains("chap"))
        {
            d.UsaChap = true;
            d.UsaMutualChap = false;
            return;
        }

        d.UsaChap = false;
        d.UsaMutualChap = false;
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
            var discoveryTask = Task.Run(() =>
                ShellHelper.EjecutarComoRoot($"iscsiadm -m discovery -t sendtargets -p {ip}")
            );

            var completed = await Task.WhenAny(discoveryTask, Task.Delay(5000));
            if (completed != discoveryTask)
            {
                LogService.Error($"[ISCSI] #{id} TIMEOUT en discovery");
                return destinos;
            }

            var discovery = discoveryTask.Result;
            if (string.IsNullOrWhiteSpace(discovery.Stdout))
                return destinos;

            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout;

            foreach (var line in discovery.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.Contains("iqn.")) continue;

                var partes = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string portal = partes[0]; // puede ser "IP" o "IP:PUERTO"

                // Si NO trae puerto → añadir 3260
                if (!portal.Contains(":"))
                    portal = $"{portal}:3260";

                string iqn = partes.LastOrDefault(s => s.StartsWith("iqn."));
                if (string.IsNullOrWhiteSpace(iqn))
                    continue;

                bool conectado = sesiones.Contains(iqn);

                if (destinos.Any(d => d.Iqn == iqn && d.Ip == portal))
                    continue;

                var d = new IscsiDestino
                {
                    Ip = portal,              // ✔ SIEMPRE IP:PUERTO
                    PortalReal = portal,      // ✔ Guardamos el portal real
                    Iqn = iqn,
                    Conectado = conectado,
                    Seleccionado = false,
                    TieneFilesystem = false
                };

                DetectarChap(d);
                destinos.Add(d);
            }

            foreach (var d in destinos.Where(x => x.Conectado))
            {
                try { await CompletarInformacionDestino(d, id); }
                catch { }
            }

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
            if (!d.Conectado)
            {
                d.TieneFilesystem = false;
                d.FsType = "";
                d.MountPoint = "";
                return;
            }

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

            var lsblk = ShellHelper.EjecutarComoRoot($"lsblk -rno NAME {d.DevicePath}");
            var lines = lsblk.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            d.PartitionPath = lines.Length > 1
                ? "/dev/" + lines[1].Trim()
                : d.DevicePath;

            var mounts = ShellHelper.EjecutarComoRoot("mount").Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);

            var mline = mounts.FirstOrDefault(l => l.Contains(d.PartitionPath));
            if (mline != null)
            {
                var parts = mline.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                    d.MountPoint = parts[2];
            }

            var blkid = ShellHelper.EjecutarComoRoot($"blkid -p {d.PartitionPath}");
            d.TieneFilesystem =
                !string.IsNullOrWhiteSpace(blkid.Stdout) &&
                blkid.Stdout.Contains("TYPE=");

            if (d.TieneFilesystem)
                d.FsType = DetectarFsType(blkid.Stdout);

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
            // 1) Crear mountpoint usando ConfigManager
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
            bool yaConectado = sesiones.Contains(d.Iqn);

            // --------------------------------------------------------------
            // 3) LOGIN iSCSI (si no está conectado)
            // --------------------------------------------------------------
            if (!yaConectado)
            {
                // Discover
                var discovery = ShellHelper.EjecutarComoRoot(
                    $"iscsiadm -m discovery -t sendtargets -p {d.Ip}"
                );

                var portals = new List<string>();

                foreach (var line in discovery.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.Contains(d.Iqn))
                    {
                        string portal = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                        portal = portal.Split(',')[0];
                        portals.Add(portal);
                    }
                }

                if (portals.Count == 0)
                    throw new Exception("No se encontraron portales para este IQN.");

                string? portalValido = null;

                foreach (var portal in portals)
                {
                    var result = ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m node -T {d.Iqn} -p {portal}"
                    );

                    if (!result.Stderr.Contains("No records found"))
                    {
                        portalValido = portal;
                        break;
                    }
                }

                if (portalValido == null)
                    throw new Exception("No se encontró ningún portal válido.");

                d.Ip = portalValido;

                // Crear nodo
                ShellHelper.EjecutarComoRoot(
                    $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=new"
                );

                // CHAP
                if (d.UsaChap || d.UsaMutualChap)
                {
                    ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=update --name node.session.auth.authmethod --value=CHAP"
                    );

                    if (d.UsaChap)
                    {
                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=update --name node.session.auth.username --value={d.UsuarioChap}"
                        );

                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=update --name node.session.auth.password --value={d.PasswordChap}"
                        );
                    }

                    if (d.UsaMutualChap)
                    {
                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=update --name node.session.auth.username_in --value={d.UsuarioMutualChap}"
                        );

                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=update --name node.session.auth.password_in --value={d.PasswordMutualChap}"
                        );
                    }
                }

                // LOGIN
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
            // 4) DETECTAR SYMLINK /dev/disk/by-path
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
            // 5) DETECTAR PARTICIÓN
            // --------------------------------------------------------------
            var lsblk = ShellHelper.EjecutarComoRoot($"lsblk -rno NAME {d.DevicePath}");
            var lines = lsblk.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            d.PartitionPath = lines.Length > 1
                ? "/dev/" + lines[1].Trim()
                : d.DevicePath;

            // --------------------------------------------------------------
            // 6) DETECTAR FILESYSTEM
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
            // 7) MONTAR
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

        // Ejemplo de línea:
        // 192.168.10.20:3260,1 iqn.2013-03.com.wdc:mycloudex2ultra:mjcc
        var line = result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(l => l.Contains(d.Iqn));

        if (line == null)
            return null;

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        // El portal es la primera columna
        return parts[0].Trim();
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
TARGET=""{d.Iqn}""
PORTAL=""{d.Ip}""
MOUNTPOINT=""{d.MountPoint}""

# --- CONFIGURAR CHAP SI EXISTE ---
if [ ""{d.UsuarioChap}"" != """" ]; then
  iscsiadm -m node -T ""$TARGET"" -p ""$PORTAL"" --op=update --name node.session.auth.authmethod --value=CHAP
  iscsiadm -m node -T ""$TARGET"" -p ""$PORTAL"" --op=update --name node.session.auth.username --value=""{d.UsuarioChap}""
  iscsiadm -m node -T ""$TARGET"" -p ""$PORTAL"" --op=update --name node.session.auth.password --value=""{d.PasswordChap}""
fi

# --- CONFIGURAR MUTUAL CHAP SI EXISTE ---
if [ ""{d.UsuarioMutualChap}"" != """" ]; then
  iscsiadm -m node -T ""$TARGET"" -p ""$PORTAL"" --op=update --name node.session.auth.username_in --value=""{d.UsuarioMutualChap}""
  iscsiadm -m node -T ""$TARGET"" -p ""$PORTAL"" --op=update --name node.session.auth.password_in --value=""{d.PasswordMutualChap}""
fi

# --- LOGIN ---
if ! iscsiadm -m session | grep -q ""$TARGET""; then
  iscsiadm -m node -T ""$TARGET"" -p ""$PORTAL"" --login
  for i in {{1..10}}; do
    if ls /dev/disk/by-path/*""$TARGET""*lun* &>/dev/null; then
      break
    fi
    sleep 1
  done
fi

# --- MONTAR ---
mount -a -O _netdev
exit 0
";

        ShellHelper.EjecutarComoRoot(
            $"bash -c \"cat > {scriptPath} << 'EOF'\n{scriptContent}\nEOF\""
        );

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

        ShellHelper.EjecutarComoRoot(
            $"bash -c \"cat > {servicePath} << 'EOF'\n{serviceContent}\nEOF\""
        );

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
                    ShellHelper.EjecutarComoRoot($"umount -l \"{d.MountPoint}\"");
                    await Task.Delay(300);

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
            // 3) Logout iSCSI
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
            // 4) Reset de propiedades
            // --------------------------------------------------------------
            d.Conectado = false;
            d.TieneFilesystem = false;
            d.DevicePath = null;
            d.PartitionPath = null;
            d.MountPoint = null;
            d.FsType = null;

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

    using (LoadingService.Show($"Disconnecting {d.Iqn}..."))
    {
        try
        {
            // 1) Desmontar
            if (!string.IsNullOrWhiteSpace(d.MountPoint))
            {
                var mpCheck = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"");
                if (mpCheck.ExitCode == 0)
                {
                    ShellHelper.EjecutarComoRoot($"umount -l \"{d.MountPoint}\"");
                    await Task.Delay(300);
                }
            }

            // 2) Eliminar directorio
            if (!string.IsNullOrWhiteSpace(d.MountPoint) &&
                Directory.Exists(d.MountPoint))
            {
                ShellHelper.EjecutarComoRoot($"rm -rf \"{d.MountPoint}\"");
            }

            // 3) Logout
            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout;
            if (!string.IsNullOrWhiteSpace(sesiones) &&
                sesiones.Contains(d.Iqn))
            {
                ShellHelper.EjecutarComoRoot(
                    $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --logout"
                );
                await Task.Delay(300);
            }

            // 4) Eliminar nodo
            ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=delete"
            );

            // 5) Reset
            d.Conectado = false;
            d.TieneFilesystem = false;
            d.DevicePath = null;
            d.PartitionPath = null;
            d.MountPoint = null;
            d.FsType = null;

            NotificadorLinux.Enviar($"Target {d.Iqn} disconnected and removed");
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
