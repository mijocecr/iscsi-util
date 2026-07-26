using System.Linq;

namespace ISCSI_Util.Helpers;



//------

using System;
using System.IO;
using System.Threading.Tasks;
using ISCSI_Util.Models;
using ISCSI_Util.Services;




public static class IscsiPersistenceManager
{
    // Infra de trazas local
    private static long _traceCounter = 0;
    private static long NextTraceId() => ++_traceCounter;

    private static void TraceIn(long id, string method, string details = "")
        => LogService.Debug($"[PERSIST] #{id} → {method} {details}");

    private static void TraceOut(long id, string method, string result = "OK")
        => LogService.Debug($"[PERSIST] #{id} ← {method} [{result}]");

    // --------------------------------------------------------------
    // API PÚBLICA
    // --------------------------------------------------------------

    public static async Task ApplyAsync(IscsiDestino d)
    {
        long id = NextTraceId();
        TraceIn(id, "Apply", d.Iqn);

        // Validación mínima
        if (d == null)
        {
            LogService.Error($"[PERSIST] #{id} Destino NULL. Abortando.");
            return;
        }

        if (string.IsNullOrWhiteSpace(d.Iqn))
        {
            LogService.Error($"[PERSIST] #{id} IQN vacío. Abortando.");
            return;
        }

        // Asegurar mountpoint
        EnsureMountPoint(d, id);

        using (LoadingService.Show($"Applying persistence for {d.Iqn}..."))
        {
            try
            {
                bool esCachyos = EsCachyOS();
                LogService.Debug($"[PERSIST] #{id} CachyOS={esCachyos}");

                // Asegurar directorio de montaje
                EnsureMountDirectory(d, id);

                // Portal para persistencia (no tocar d.Ip)
                string portalPersistencia = ObtenerPortalPersistencia(d, id);

                // Guardar en fstab
                await GuardarEnFstab(d, id);

                // Crear script + servicio
                await CrearScriptYServicio(d, portalPersistencia, id);

                // Fix CachyOS (presets)
                if (esCachyos)
                    FixCachyOSPresets(id);

                // Reload + enable + symlink seguro
                await EnableServicio(d, id);

                NotificadorLinux.Enviar($"Persistence applied for {d.Iqn}", 4000, "normal");
                TraceOut(id, "Apply");
            }
            catch (Exception ex)
            {
                LogService.Error($"[PERSIST] #{id} ERROR Apply: {ex.Message}");
                LogService.Error($"[PERSIST] #{id} STACK: {ex.StackTrace}");
                NotificadorLinux.Enviar($"[ERROR] Failed to apply persistence for {d.Iqn}", 6000, "critical");
                TraceOut(id, "Apply", "ERROR");
            }
        }
    }

    public static async Task RemoveAsync(IscsiDestino d)
    {
        long id = NextTraceId();
        TraceIn(id, "Remove", d.Iqn);

        if (d == null || string.IsNullOrWhiteSpace(d.Iqn))
        {
            LogService.Error($"[PERSIST] #{id} Destino inválido. Abortando.");
            return;
        }

        using (LoadingService.Show($"Removing persistence for {d.Iqn}..."))
        {
            try
            {
                string safe = SystemdSafe(d.Iqn);
                string scriptPath = $"/usr/local/bin/mount-iscsi-{safe}.sh";
                string servicePath = $"/etc/systemd/system/iscsi-{safe}.service";

                LogService.Debug($"[PERSIST] #{id} Deshabilitando servicio iscsi-{safe}.service");
                ShellHelper.EjecutarComoRoot($"systemctl disable iscsi-{safe}.service");

                LogService.Debug($"[PERSIST] #{id} Eliminando service={servicePath}, script={scriptPath}");
                ShellHelper.EjecutarComoRoot($"rm -f {servicePath}");
                ShellHelper.EjecutarComoRoot($"rm -f {scriptPath}");

                if (!string.IsNullOrWhiteSpace(d.MountPoint))
                {
                    string mpEsc = d.MountPoint.Replace("/", "\\/");
                    LogService.Debug($"[PERSIST] #{id} Limpiando fstab para mountpoint={d.MountPoint}");
                    ShellHelper.EjecutarComoRoot($"sed -i '\\#{mpEsc}#d' /etc/fstab");
                }

                LogService.Debug($"[PERSIST] #{id} systemctl daemon-reload");
                ShellHelper.EjecutarComoRoot("systemctl daemon-reload");

                TraceOut(id, "Remove");
                NotificadorLinux.Enviar($"Persistence removed for {d.Iqn}", 4000, "normal");
            }
            catch (Exception ex)
            {
                LogService.Error($"[PERSIST] #{id} ERROR Remove: {ex.Message}");
                TraceOut(id, "Remove", "ERROR");
            }
        }
    }

    public static bool Detect(IscsiDestino d)
    {
        long id = NextTraceId();
        TraceIn(id, "Detect", d.Iqn);

        if (d == null || string.IsNullOrWhiteSpace(d.Iqn))
        {
            TraceOut(id, "Detect", "INVALID");
            return false;
        }

        try
        {
            // fstab
            if (!string.IsNullOrWhiteSpace(d.MountPoint) && File.Exists("/etc/fstab"))
            {
                string fstab = File.ReadAllText("/etc/fstab");
                string pattern = $" {d.MountPoint} ";

                if (fstab.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    TraceOut(id, "Detect", "FSTAB");
                    return true;
                }
            }
        }
        catch { }

        try
        {
            string safe = SystemdSafe(d.Iqn);
            string service = $"/etc/systemd/system/iscsi-{safe}.service";

            if (File.Exists(service))
            {
                TraceOut(id, "Detect", "SERVICE");
                return true;
            }
        }
        catch { }

        TraceOut(id, "Detect", "NONE");
        return false;
    }

    // --------------------------------------------------------------
    // Helpers internos
    // --------------------------------------------------------------

    private static bool EsCachyOS()
    {
        try
        {
            if (File.Exists("/etc/cachyos-release"))
                return true;

            if (File.Exists("/usr/lib/os-release"))
            {
                var txt = File.ReadAllText("/usr/lib/os-release");
                return txt.Contains("CachyOS", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { }

        return false;
    }

    private static void EnsureMountPoint(IscsiDestino d, long id)
    {
        if (!string.IsNullOrWhiteSpace(d.MountPoint))
        {
            LogService.Debug($"[PERSIST] #{id} MountPoint ya definido: {d.MountPoint}");
            return;
        }

        string basePath = ConfigManager.MountBasePath;
        string safe = SystemdSafe(d.Iqn);

        d.MountPoint = Path.Combine(basePath, safe);
        LogService.Debug($"[PERSIST] #{id} MountPoint regenerado automáticamente: {d.MountPoint}");
    }

    private static void EnsureMountDirectory(IscsiDestino d, long id)
    {
        LogService.Debug($"[PERSIST] #{id} Verificando directorio de montaje: {d.MountPoint}");

        if (!Directory.Exists(d.MountPoint))
        {
            LogService.Debug($"[PERSIST] #{id} Creando directorio: {d.MountPoint}");
            Directory.CreateDirectory(d.MountPoint);

            string chmodCmd = $"chmod {ConfigManager.DefaultPermissions} \"{d.MountPoint}\"";
            LogService.Debug($"[PERSIST] #{id} Ejecutando: {chmodCmd}");
            ShellHelper.EjecutarComoRoot(chmodCmd);
        }
        else
        {
            LogService.Debug($"[PERSIST] #{id} Directorio ya existe: {d.MountPoint}");
        }
    }

    private static string ObtenerPortalPersistencia(IscsiDestino d, long id)
    {
        LogService.Debug($"[PERSIST] #{id} Detectando portal real para IQN={d.Iqn}");

        string? portalReal = IscsiHelper.ObtenerPortalReal(d);

        if (!string.IsNullOrWhiteSpace(portalReal))
        {
            LogService.Debug($"[PERSIST] #{id} Portal real detectado: {portalReal}");
            return portalReal;
        }

        LogService.Debug($"[PERSIST] #{id} Usando portal actual: {d.Ip}");
        return d.Ip;
    }

    private static async Task GuardarEnFstab(IscsiDestino d, long id)
    {
        LogService.Debug($"[PERSIST] #{id} GuardarEnFstab → {d.Iqn}");

        if (string.IsNullOrWhiteSpace(d.PartitionPath))
        {
            LogService.Error($"[PERSIST] #{id} PartitionPath vacío. No se puede generar fstab.");
            return;
        }

        var blkid = ShellHelper.EjecutarComoRoot($"blkid {d.PartitionPath}");
        string uuid = blkid.Stdout.Split(' ')
            .FirstOrDefault(s => s.StartsWith("UUID=", StringComparison.OrdinalIgnoreCase))?
            .Replace("UUID=", "")
            .Trim('"');

        if (string.IsNullOrWhiteSpace(uuid))
        {
            LogService.Error($"[PERSIST] #{id} No se pudo obtener UUID para {d.PartitionPath}");
            return;
        }

        LogService.Debug($"[PERSIST] #{id} UUID={uuid}");

        string entry = $"UUID={uuid} {d.MountPoint} auto _netdev 0 0";
        string mpEsc = d.MountPoint.Replace("/", "\\/");

        LogService.Debug($"[PERSIST] #{id} Entrada fstab: {entry}");

        await Task.Run(() =>
        {
            ShellHelper.EjecutarComoRoot($"sed -i '\\#{mpEsc}#d' /etc/fstab");
            ShellHelper.EjecutarComoRoot($"bash -c 'echo \"{entry}\" >> /etc/fstab'");
        });

        LogService.Debug($"[PERSIST] #{id} fstab actualizado.");
    }

    private static async Task CrearScriptYServicio(IscsiDestino d, string portal, long id)
{
    LogService.Debug($"[PERSIST] #{id} CrearScriptYServicio → {d.Iqn}");

    string safe = SystemdSafe(d.Iqn);

    string scriptPath = $"/usr/local/bin/mount-iscsi-{safe}.sh";
    string servicePath = $"/etc/systemd/system/iscsi-{safe}.service";

    LogService.Debug($"[PERSIST] #{id} script={scriptPath}, service={servicePath}");

    string scriptContent =
$@"#!/bin/bash
TARGET=""{d.Iqn}""
PORTAL=""{portal}""
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
  for i in {{1..30}}; do
    if ls /dev/disk/by-path/*""$TARGET""*lun* &>/dev/null; then
      break
    fi
    sleep 1
  done
fi

mount -a -O _netdev
exit 0
";

    File.WriteAllText("/tmp/tmp_script.sh", scriptContent);
    ShellHelper.EjecutarComoRoot($"mv /tmp/tmp_script.sh {scriptPath}");
    ShellHelper.EjecutarComoRoot($"chmod 755 {scriptPath}");
    ShellHelper.EjecutarComoRoot($"chown root:root {scriptPath}");

    // ============================================================
    // UNIT FILE CORREGIDO (NO BLOQUEA EL ARRANQUE)
    // ============================================================

    string serviceContent =
$@"[Unit]
Description=Connect iSCSI target and mount {d.Iqn}
Wants=network-online.target iscsid.service iscsi.service
After=network-online.target iscsid.service iscsi.service

[Service]
Type=oneshot
ExecStart={scriptPath}
RemainAfterExit=yes
TimeoutSec=30

[Install]
WantedBy=multi-user.target
";

    File.WriteAllText("/tmp/tmp_service.service", serviceContent);
    ShellHelper.EjecutarComoRoot($"mv /tmp/tmp_service.service {servicePath}");
    ShellHelper.EjecutarComoRoot($"chmod 644 {servicePath}");
    ShellHelper.EjecutarComoRoot($"chown root:root {servicePath}");

    ShellHelper.EjecutarComoRoot($"systemd-analyze verify {servicePath}");

    LogService.Debug($"[PERSIST] #{id} Script y servicio creados.");
    await Task.CompletedTask;
}

  
    private static void FixCachyOSPresets(long id)
    {
        LogService.Debug($"[PERSIST] #{id} Aplicando FIX de presets para CachyOS...");

        string presetPath = "/etc/systemd/system-preset/99-iscsi.preset";
        string presetContent = "enable iscsi-*.service\nenable iscsi.service\n";

        string cmdPreset = $"bash -c \"echo '{presetContent}' > {presetPath}\"";
        LogService.Debug($"[PERSIST] #{id} Ejecutando: {cmdPreset}");
        ShellHelper.EjecutarComoRoot(cmdPreset);

        LogService.Debug($"[PERSIST] #{id} Ejecutando: systemctl preset-all --verbose");
        ShellHelper.EjecutarComoRoot("systemctl preset-all --verbose");
    }

    private static async Task EnableServicio(IscsiDestino d, long id)
    {
        string safe = SystemdSafe(d.Iqn);
        string unitPath = $"/etc/systemd/system/iscsi-{safe}.service";
        string symlinkPath = $"/etc/systemd/system/multi-user.target.wants/iscsi-{safe}.service";

        LogService.Debug($"[PERSIST] #{id} daemon-reload + enable iscsi-{safe}.service");

        ShellHelper.EjecutarComoRoot("systemctl daemon-reload");
        ShellHelper.EjecutarComoRoot($"systemctl enable --force iscsi-{safe}.service");

        await Task.Delay(300);

        if (!File.Exists(symlinkPath))
        {
            LogService.Debug($"[PERSIST] #{id} Symlink NO existe tras enable. Re-creando: {symlinkPath}");
            ShellHelper.EjecutarComoRoot($"ln -s {unitPath} {symlinkPath}");
        }
        else
        {
            LogService.Debug($"[PERSIST] #{id} Symlink OK: {symlinkPath}");
        }
    }

    // Reutiliza tu helper existente
    private static string SystemdSafe(string s)
        => IscsiHelper.SanitizarNombre(s);
    
    

    public static bool DetectFstab(IscsiDestino d)
    {
        try
        {
            if (d == null || string.IsNullOrWhiteSpace(d.Iqn))
                return false;

            // Partimos del IQN y lo convertimos al formato que aparece en las rutas de fstab
            // Ej: iqn.2013-03.com.wdc:mycloudex2ultra:mjcc
            // → iqn_2013_03_com_wdc_mycloudex2ultra_mjcc
            string token = IscsiHelper.SanitizarNombre(d.Iqn)
                .Replace('.', '_')
                .Replace('-', '_');

            var lines = File.ReadAllLines("/etc/fstab");

            foreach (var raw in lines)
            {
                string line = raw.Trim();

                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                    continue;

                if (line.Contains(token, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }








    
}

//------