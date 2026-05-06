using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Text;
using ISCSI_Util.Models;
using ISCSI_Util.Utils;

namespace ISCSI_Util.Helpers;

public static class IscsiHelper
{
    // ============================================================
    // BLOQUE 0: Infraestructura de trazas
    // ============================================================

    private static long _traceCounter = 0;

    private static long NextTraceId() => ++_traceCounter;

    private static Stopwatch StartTrace(long id, string method, string details = "")
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"[ISCSI] #{id} → {method} {details}");
        return sw;
    }

    private static void EndTrace(long id, string method, Stopwatch sw, string result = "OK")
    {
        sw.Stop();
        Console.WriteLine($"[ISCSI] #{id} ← {method} [{result}] en {sw.ElapsedMilliseconds} ms");
    }

    private static void Log(long id, string message)
    {
        Console.WriteLine($"[ISCSI] #{id} {message}");
    }

    // ============================================================
    // Sanitización del IQN para usar en nombres de archivo/servicio
    // ============================================================

    public static string SanitizarNombre(string iqn)
    {
        long id = NextTraceId();
        var sw = StartTrace(id, nameof(SanitizarNombre), $"iqn='{iqn}'");

        try
        {
            char[] invalid = Path.GetInvalidFileNameChars()
                .Concat(new[] { ':', '/', '\\', ' ' })
                .ToArray();

            var result = new string(iqn.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            Log(id, $"Resultado sanitizado='{result}'");
            EndTrace(id, nameof(SanitizarNombre), sw);
            return result;
        }
        catch (Exception ex)
        {
            Log(id, $"[ERROR] {ex}");
            EndTrace(id, nameof(SanitizarNombre), sw, "ERROR");
            throw;
        }
    }

    #region Discover iSCSI Targets

    public static List<IscsiDestino> Descubrir(string ip)
    {
        long id = NextTraceId();
        var sw = StartTrace(id, nameof(Descubrir), $"ip='{ip}'");

        var destinos = new List<IscsiDestino>();
        try
        {
            Log(id, "Ejecutando discovery iscsiadm...");
            string output = Ejecutar("sudo", $"-S iscsiadm -m discovery -t sendtargets -p {ip}");
            Log(id, $"Discovery output:\n{output}");

            Log(id, "Obteniendo sesiones actuales...");
            string sesionesOut = Ejecutar("sudo", "-S iscsiadm -m session");
            Log(id, $"Sesiones:\n{sesionesOut}");

            var sesiones = string.IsNullOrWhiteSpace(sesionesOut)
                ? Array.Empty<string>()
                : sesionesOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Log(id, "Listando /dev/disk/by-path/...");
            var byPath = Ejecutar("ls", "-1 /dev/disk/by-path/")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                Log(id, $"Procesando línea discovery: '{line}'");
                var tokens = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 2)
                {
                    Log(id, "Línea ignorada: tokens insuficientes.");
                    continue;
                }

                string iqn = tokens.LastOrDefault(t => t.StartsWith("iqn."));
                if (string.IsNullOrEmpty(iqn))
                {
                    Log(id, "No se encontró IQN en la línea, se ignora.");
                    continue;
                }

                bool conectado = sesiones.Any(s => s.Contains(iqn));
                Log(id, $"IQN='{iqn}', conectado={conectado}");

                if (destinos.Any(d => d.Iqn == iqn && d.Ip == ip))
                {
                    Log(id, "Destino duplicado, se ignora.");
                    continue;
                }

                var destino = new IscsiDestino
                {
                    Ip = ip,
                    Iqn = iqn,
                    Conectado = conectado,
                    Seleccionado = false,
                    TieneFilesystem = false
                };

                destino.DevicePath = byPath.FirstOrDefault(dev => dev.Contains(ip) && dev.Contains("lun"))
                    ?.Trim();

                if (!string.IsNullOrEmpty(destino.DevicePath))
                {
                    destino.DevicePath = Path.Combine("/dev/disk/by-path", destino.DevicePath);
                    Log(id, $"DevicePath detectado='{destino.DevicePath}'");
                }
                else
                {
                    Log(id, "No se encontró DevicePath en by-path.");
                }

                destinos.Add(destino);
                Log(id, $"Destino añadido: IQN='{destino.Iqn}', IP='{destino.Ip}', Conectado={destino.Conectado}");

                if (destino.Conectado)
                {
                    Log(id, "Destino conectado, completando información...");
                    CompletarInformacionDestino(destino);
                }
            }
        }
        catch (Exception ex)
        {
            Log(id, $"Error al descubrir destinos: {ex}");
        }

        Log(id, $"Total destinos descubiertos: {destinos.Count}");
        EndTrace(id, nameof(Descubrir), sw);
        return destinos;
    }

    #endregion

    // ============================================================
    // Helpers
    // ============================================================

    private static string Ejecutar(string fileName, string args)
    {
        long id = NextTraceId();
        var sw = StartTrace(id, nameof(Ejecutar), $"fileName='{fileName}', args='{args}'");

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (s, e) =>
        {
            if (e.Data != null) outputBuilder.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (s, e) =>
        {
            if (e.Data != null) errorBuilder.AppendLine(e.Data);
        };

        try
        {
            Log(id, "Iniciando proceso...");
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Enviar contraseña si es sudo -S
            if (fileName == "sudo" && args.Contains("-S") && !string.IsNullOrEmpty(Credenciales.AdminPassword))
            {
                var pass = Credenciales.AdminPassword?.TrimEnd('\r', '\n');
                Log(id, "Enviando contraseña sudo...");
                process.StandardInput.WriteLine(pass);
                process.StandardInput.Flush();
                process.StandardInput.Close();
            }

            const int timeoutMs = 15000;
            if (!process.WaitForExit(timeoutMs))
            {
                Log(id, $"Timeout tras {timeoutMs} ms, matando proceso...");
                try { process.Kill(); } catch { }
                EndTrace(id, nameof(Ejecutar), sw, "TIMEOUT");
                return string.Empty;
            }

            process.WaitForExit();

            string output = outputBuilder.ToString();
            string error = errorBuilder.ToString();

            Log(id, $"ExitCode={process.ExitCode}");
            Log(id, $"STDOUT='{output.Trim()}'");
            Log(id, $"STDERR='{error.Trim()}'");

            // ============================================================
            // 🔥 FILTRO DE ERRORES ESPERADOS (NO SE REPORTAN)
            // ============================================================

            if (fileName == "sudo" && process.ExitCode != 0)
            {
                bool esErrorEsperado =
                    // Errores típicos de iscsiadm
                    error.Contains("No active sessions", StringComparison.OrdinalIgnoreCase) ||
                    error.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
                    error.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                    error.Contains("not installed", StringComparison.OrdinalIgnoreCase) ||
                    error.Contains("Unknown operation", StringComparison.OrdinalIgnoreCase) ||

                    // Archivos inexistentes (rm, rmdir, sed)
                    error.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
                    error.Contains("No existe el fichero", StringComparison.OrdinalIgnoreCase) ||
                    error.Contains("failed to remove", StringComparison.OrdinalIgnoreCase) ||
                    error.Contains("cannot remove", StringComparison.OrdinalIgnoreCase) ||
                    error.Contains("rmdir:", StringComparison.OrdinalIgnoreCase) ||
                    error.Contains("rm:", StringComparison.OrdinalIgnoreCase) ||

                    // Si el comando es rm o rmdir, nunca es error
                    args.Contains("rm ", StringComparison.OrdinalIgnoreCase) ||
                    args.Contains("rmdir", StringComparison.OrdinalIgnoreCase) ||

                    // Caso especial: dos2unix no instalado
                    args.Contains("command -v dos2unix", StringComparison.OrdinalIgnoreCase);

                if (esErrorEsperado)
                {
                    Log(id, "Error esperado, devolviendo STDOUT silenciosamente.");
                    EndTrace(id, nameof(Ejecutar), sw, "OK_EXPECTED_ERROR");
                    return output;
                }

                // ============================================================
                // 🔥 ERRORES REALES (SÍ SE REPORTAN)
                // ============================================================
                Console.WriteLine($"[Ejecutar] Comando sudo falló: {fileName} {args}\n{error}");
                EndTrace(id, nameof(Ejecutar), sw, "ERROR");
                return string.Empty;
            }

            EndTrace(id, nameof(Ejecutar), sw);
            return output;
        }
        catch (Exception ex)
        {
            Log(id, $"[EXCEPTION] {ex}");
            EndTrace(id, nameof(Ejecutar), sw, "EXCEPTION");
            return string.Empty;
        }
    }

    private static int EjecutarConCodigo(string fileName, string args)
    {
        long id = NextTraceId();
        var sw = StartTrace(id, nameof(EjecutarConCodigo), $"fileName='{fileName}', args='{args}'");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        try
        {
            Log(id, "Iniciando proceso...");
            process.Start();

            if (fileName == "sudo" && args.Contains("-S") && !string.IsNullOrEmpty(Credenciales.AdminPassword))
            {
                Log(id, "Enviando contraseña sudo...");
                process.StandardInput.Write(Credenciales.AdminPassword + "\n");
                process.StandardInput.Flush();
            }

            process.WaitForExit();

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            Log(id, $"ExitCode={process.ExitCode}");
            Log(id, $"STDOUT='{stdout.Trim()}'");
            Log(id, $"STDERR='{stderr.Trim()}'");

            EndTrace(id, nameof(EjecutarConCodigo), sw);
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Log(id, $"[EXCEPTION] {ex}");
            EndTrace(id, nameof(EjecutarConCodigo), sw, "EXCEPTION");
            return -1;
        }
    }

    private static string ObtenerGrupoUsuario()
    {
        long id = NextTraceId();
        var sw = StartTrace(id, nameof(ObtenerGrupoUsuario));

        try
        {
            var grupo = Ejecutar("id", "-gn").Trim();
            if (string.IsNullOrWhiteSpace(grupo))
            {
                Log(id, "Grupo vacío, usando 'users' por defecto.");
                grupo = "users";
            }
            else
            {
                Log(id, $"Grupo detectado='{grupo}'");
            }

            EndTrace(id, nameof(ObtenerGrupoUsuario), sw);
            return grupo;
        }
        catch (Exception ex)
        {
            Log(id, $"[EXCEPTION] {ex}");
            EndTrace(id, nameof(ObtenerGrupoUsuario), sw, "EXCEPTION");
            return "users";
        }
    }

    private static string DetectarFsType(string blkidOut)
    {
        long id = NextTraceId();
        var sw = StartTrace(id, nameof(DetectarFsType), $"blkidOut='{blkidOut}'");

        try
        {
            string result;
            if (blkidOut.Contains("TYPE=\"ext2\"")) result = "ext2";
            else if (blkidOut.Contains("TYPE=\"ext3\"")) result = "ext3";
            else if (blkidOut.Contains("TYPE=\"ext4\"")) result = "ext4";
            else if (blkidOut.Contains("TYPE=\"xfs\"")) result = "xfs";
            else if (blkidOut.Contains("TYPE=\"btrfs\"")) result = "btrfs";
            else if (blkidOut.Contains("TYPE=\"f2fs\"")) result = "f2fs";
            else if (blkidOut.Contains("TYPE=\"ntfs\"")) result = "ntfs";
            else if (blkidOut.Contains("TYPE=\"vfat\"")) result = "vfat";
            else if (blkidOut.Contains("TYPE=\"exfat\"")) result = "exfat";
            else if (blkidOut.Contains("TYPE=\"iso9660\"")) result = "iso9660";
            else result = "ext4";

            Log(id, $"FsType detectado='{result}'");
            EndTrace(id, nameof(DetectarFsType), sw);
            return result;
        }
        catch (Exception ex)
        {
            Log(id, $"[EXCEPTION] {ex}");
            EndTrace(id, nameof(DetectarFsType), sw, "EXCEPTION");
            return "ext4";
        }
    }

    // ============================================================
    // Conectar
    // ============================================================

    public static void Conectar(IscsiDestino destino)
    {
        long id = NextTraceId();
        var sw = StartTrace(id, nameof(Conectar), $"IQN='{destino?.Iqn}', IP='{destino?.Ip}'");

        try
        {
            Log(id, "Obteniendo sesiones actuales...");
            var sesionesOut = Ejecutar("sudo", "-S iscsiadm -m session");
            bool yaConectado = sesionesOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any(s => s.Contains(destino.Iqn));

            Log(id, $"yaConectado={yaConectado}");

            if (!yaConectado)
            {
                Log(id, "Registrando nodo iSCSI...");
                Ejecutar("sudo", $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip}");

                if (destino.UsaChap || destino.UsaMutualChap)
                {
                    Log(id, $"Configurando CHAP: UsaChap={destino.UsaChap}, UsaMutualChap={destino.UsaMutualChap}");

                    Ejecutar("sudo",
                        $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} " +
                        "--op=update --name node.session.auth.authmethod --value=CHAP");

                    if (destino.UsaChap)
                    {
                        Log(id, "Aplicando usuario/password CHAP...");
                        Ejecutar("sudo",
                            $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} " +
                            $"--op=update --name node.session.auth.username --value={destino.UsuarioChap}");

                        Ejecutar("sudo",
                            $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} " +
                            $"--op=update --name node.session.auth.password --value={destino.PasswordChap}");
                    }

                    if (destino.UsaMutualChap)
                    {
                        Log(id, "Aplicando usuario/password MUTUAL CHAP...");
                        Ejecutar("sudo",
                            $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} " +
                            $"--op=update --name node.session.auth.username_in --value={destino.UsuarioMutualChap}");

                        Ejecutar("sudo",
                            $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} " +
                            $"--op=update --name node.session.auth.password_in --value={destino.PasswordMutualChap}");
                    }
                }

                Log(id, "Realizando login iSCSI...");
                Ejecutar("sudo", $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} --login");
            }

            destino.MountPoint = $"/mnt/iscsi/{SanitizarNombre(destino.Iqn)}";
            Log(id, $"MountPoint='{destino.MountPoint}'");

            Ejecutar("sudo", $"-S mkdir -p {destino.MountPoint}");

            destino.DevicePath = null;
            for (int i = 0; i < 10; i++)
            {
                Log(id, $"Intento {i + 1}/10: buscando symlink en /dev/disk/by-path/...");
                var output = Ejecutar("ls", "-1 /dev/disk/by-path/");
                var match = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                  .FirstOrDefault(line => line.Contains(destino.Ip) && line.Contains("lun"));
                if (match != null)
                {
                    destino.DevicePath = "/dev/disk/by-path/" + match.Trim();
                    Log(id, $"DevicePath encontrado='{destino.DevicePath}'");
                    break;
                }
                System.Threading.Thread.Sleep(1000);
            }

            if (string.IsNullOrWhiteSpace(destino.DevicePath))
            {
                Log(id, $"[ERROR] No se encontró symlink para {destino.Iqn}");
                throw new InvalidOperationException($"No se encontró symlink para {destino.Iqn}");
            }

            Log(id, "Obteniendo particiones con lsblk...");
            var lsblkOut = Ejecutar("lsblk", "-rno NAME " + destino.DevicePath);
            var lines = lsblkOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            destino.PartitionPath = lines.Length > 1
                ? "/dev/" + lines[1].Trim()
                : destino.DevicePath;

            Log(id, $"PartitionPath='{destino.PartitionPath}'");

            Log(id, "Ejecutando blkid para detectar filesystem...");
            var blkidOut = Ejecutar("sudo", "-S blkid " + destino.PartitionPath);
            Log(id, $"blkidOut='{blkidOut}'");

            if (string.IsNullOrWhiteSpace(blkidOut))
            {
                Log(id, "No se detectó filesystem. Marcando como conectado sin montar.");
                destino.Conectado = true;
                EndTrace(id, nameof(Conectar), sw, "OK_NO_FS");
                return;
            }

            string fsType = DetectarFsType(blkidOut);
            Log(id, $"Filesystem detectado='{fsType}'");

            Log(id, "Comprobando si ya está montado con mountpoint...");
            int rcMount = EjecutarConCodigo("mountpoint", $"-q {destino.MountPoint}");
            Log(id, $"mountpoint exitCode={rcMount}");

            if (rcMount != 0)
            {
                Log(id, "No está montado, ejecutando mount...");
                Ejecutar("sudo", $"-S mount -t {fsType} {destino.PartitionPath} {destino.MountPoint}");
            }
            else
            {
                Log(id, "Ya estaba montado.");
            }

            string grupo = ObtenerGrupoUsuario();
            Log(id, $"Aplicando permisos: grupo='{grupo}'");

            Ejecutar("sudo", $"-S chgrp {grupo} {destino.MountPoint}");
            Ejecutar("sudo", $"-S chmod 770 {destino.MountPoint}");
            Ejecutar("sudo", $"-S chmod g+s {destino.MountPoint}");

            destino.Conectado = true;
            NotificadorLinux.Enviar($"Destino {destino.Iqn} montado en {destino.MountPoint}");

            EndTrace(id, nameof(Conectar), sw);
        }
        catch (Exception ex)
        {
            Log(id, $"[ERROR] Error al conectar destino {destino.Iqn}: {ex}");
            Console.WriteLine($"[ERROR] Error al conectar destino {destino.Iqn}: {ex.Message}");
            NotificadorLinux.Enviar($"[ERROR] Fallo al conectar destino {destino.Iqn}");
            EndTrace(id, nameof(Conectar), sw, "ERROR");
        }
    }

    // ============================================================
    // Desconectar
    // ============================================================

    public static void Desconectar(IscsiDestino destino, bool eliminarPersistencia = true)
    {
        long id = NextTraceId();
        var sw = StartTrace(id, nameof(Desconectar),
            $"IQN='{destino?.Iqn}', IP='{destino?.Ip}', eliminarPersistencia={eliminarPersistencia}");

        try
        {
            // 1. Desmontar si está montado
            if (!string.IsNullOrEmpty(destino.MountPoint))
            {
                Log(id, $"Comprobando mountpoint '{destino.MountPoint}'...");
                int rcMount = EjecutarConCodigo("mountpoint", $"-q {destino.MountPoint}");
                Log(id, $"mountpoint exitCode={rcMount}");

                if (rcMount == 0)
                {
                    Log(id, "Está montado, ejecutando umount...");
                    Ejecutar("sudo", $"-S umount {destino.MountPoint}");
                }
                else
                {
                    Log(id, "No estaba montado.");
                }
            }

            // 2. Logout iSCSI si está conectado
            Log(id, "Comprobando sesiones iSCSI...");
            var sesionesOut = Ejecutar("sudo", "-S iscsiadm -m session");
            bool conectado = sesionesOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any(s => s.Contains(destino.Iqn));

            Log(id, $"conectado={conectado}");

            if (conectado)
            {
                Log(id, "Ejecutando logout iSCSI...");
                Ejecutar("sudo", $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} --logout");
            }

            destino.Conectado = false;

            // 3. Eliminar persistencia SOLO si se solicita
            if (eliminarPersistencia)
            {
                Log(id, "Eliminando persistencia (servicio, fstab, etc.)...");
                EliminarServicioPersistencia(destino);
            }
            else
            {
                Log(id, "eliminarPersistencia=false, se mantiene configuración persistente.");
            }

            // 4. Eliminar punto de montaje
            if (!string.IsNullOrEmpty(destino.MountPoint))
            {
                Log(id, $"Eliminando directorio de montaje '{destino.MountPoint}'...");
                Ejecutar("sudo", $"-S rmdir {destino.MountPoint}");
            }

            NotificadorLinux.Enviar($"Destino {destino.Iqn} desconectado.");
            EndTrace(id, nameof(Desconectar), sw);
        }
        catch (Exception ex)
        {
            Log(id, $"Error al desconectar destino {destino.Iqn}: {ex}");
            Console.WriteLine($"Error al desconectar destino {destino.Iqn}: {ex.Message}");
            EndTrace(id, nameof(Desconectar), sw, "ERROR");
        }
    }

    // ============================================================
    // Completar información
    // ============================================================

    public static void CompletarInformacionDestino(IscsiDestino d)
    {
        long id = NextTraceId();
        var sw = StartTrace(id, nameof(CompletarInformacionDestino),
            $"IQN='{d?.Iqn}', IP='{d?.Ip}'");

        try
        {
            Log(id, "Listando /dev/disk/by-path/...");
            var byPath = Ejecutar("ls", "-1 /dev/disk/by-path/")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);

            var match = byPath.FirstOrDefault(line =>
                line.Contains(d.Ip) && line.Contains("lun"));

            if (match != null)
            {
                d.DevicePath = "/dev/disk/by-path/" + match.Trim();
                Log(id, $"DevicePath='{d.DevicePath}'");
            }
            else
            {
                Log(id, "No se encontró DevicePath.");
            }

            if (!string.IsNullOrWhiteSpace(d.DevicePath))
            {
                Log(id, "Obteniendo particiones con lsblk...");
                var lsblkOut = Ejecutar("lsblk", "-rno NAME " + d.DevicePath);
                var lines = lsblkOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                d.PartitionPath = lines.Length > 1
                    ? "/dev/" + lines[1].Trim()
                    : d.DevicePath;

                Log(id, $"PartitionPath='{d.PartitionPath}'");
            }

            if (!string.IsNullOrWhiteSpace(d.PartitionPath))
            {
                Log(id, "Buscando mountpoint en 'mount'...");
                var mounts = Ejecutar("mount", "");
                var line = mounts.Split('\n')
                    .FirstOrDefault(l => l.Contains(d.PartitionPath));

                if (line != null)
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        d.MountPoint = parts[2];
                        Log(id, $"MountPoint detectado='{d.MountPoint}'");
                    }
                }
                else
                {
                    Log(id, "No se encontró entrada en mount.");
                }
            }

            if (!string.IsNullOrWhiteSpace(d.PartitionPath))
            {
                try
                {
                    Log(id, "Ejecutando blkid -p para detectar filesystem...");
                    var blkidOut = Ejecutar("sudo", $"-S blkid -p {d.PartitionPath}");
                    d.TieneFilesystem = !string.IsNullOrWhiteSpace(blkidOut) && blkidOut.Contains("TYPE=");
                    Log(id, $"TieneFilesystem={d.TieneFilesystem}");
                }
                catch (Exception ex)
                {
                    Log(id, $"[WARN] Error en blkid -p: {ex}");
                    d.TieneFilesystem = false;
                }
            }

            EndTrace(id, nameof(CompletarInformacionDestino), sw);
        }
        catch (Exception ex)
        {
            Log(id, $"[ERROR] {ex}");
            EndTrace(id, nameof(CompletarInformacionDestino), sw, "ERROR");
        }
    }

    // ============================================================
    // Inicializar destino
    // ============================================================

    public static void InicializarDestino(IscsiDestino destino)
    {
        long id = NextTraceId();
        var sw = StartTrace(id, nameof(InicializarDestino),
            $"IQN='{destino?.Iqn}', PartitionPath='{destino?.PartitionPath}'");

        if (string.IsNullOrWhiteSpace(destino.PartitionPath))
        {
            Log(id, $"Error: No se encontró ruta de partición para {destino.Iqn}");
            Console.WriteLine($"Error: No se encontró ruta de partición para {destino.Iqn}");
            EndTrace(id, nameof(InicializarDestino), sw, "NO_PARTITION");
            return;
        }

        try
        {
            Log(id, "Ejecutando mkfs.ext4...");
            Ejecutar("sudo", $"-S mkfs.ext4 -F {destino.PartitionPath}");
            destino.TieneFilesystem = true;
            NotificadorLinux.Enviar($"Destino {destino.Iqn} inicializado con éxito");
            EndTrace(id, nameof(InicializarDestino), sw);
        }
        catch (Exception ex)
        {
            Log(id, $"Error al inicializar {destino.Iqn}: {ex}");
            Console.WriteLine($"Error al inicializar {destino.Iqn}: {ex.Message}");
            destino.TieneFilesystem = false;
            EndTrace(id, nameof(InicializarDestino), sw, "ERROR");
        }
    }

    // ============================================================
    // Configurar persistencia (fstab)
    // ============================================================

    public static void ConfigurarPersistencia(IscsiDestino destino, string fsType)
    {
        long id = NextTraceId();
        var sw = StartTrace(id, nameof(ConfigurarPersistencia),
            $"IQN='{destino?.Iqn}', PartitionPath='{destino?.PartitionPath}', fsType='{fsType}'");

        try
        {
            Log(id, "Configurando node.startup=automatic en iscsiadm...");
            Ejecutar("sudo",
                $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} --op update --name node.startup --value automatic");

            Log(id, "Obteniendo UUID con blkid...");
            var blkidOut = Ejecutar("sudo", $"-S blkid {destino.PartitionPath}");
            Log(id, $"blkidOut='{blkidOut}'");

            string uuid = blkidOut.Split(' ')
                .FirstOrDefault(s => s.StartsWith("UUID="))?
                .Replace("UUID=", "")
                .Trim('"');

            Log(id, $"UUID='{uuid}'");

            if (string.IsNullOrEmpty(uuid))
                throw new Exception($"No se pudo obtener UUID para {destino.PartitionPath}");

            // Asegurar MountPoint si viene vacío
            if (string.IsNullOrEmpty(destino.MountPoint))
            {
                destino.MountPoint = $"/mnt/iscsi/{SanitizarNombre(destino.Iqn)}";
                Log(id, $"MountPoint vacío, usando '{destino.MountPoint}'");
            }

            Log(id, $"Creando directorio de montaje '{destino.MountPoint}'...");
            Ejecutar("sudo", $"-S mkdir -p {destino.MountPoint}");

            string fstabEntry =
                $"UUID={uuid} {destino.MountPoint} {fsType} defaults,_netdev,x-systemd.requires=iscsid.service,x-systemd.after=iscsid.service 0 0";

            Log(id, $"Entrada fstab: {fstabEntry}");

            string fstabContent = Ejecutar("cat", "/etc/fstab");
            bool uuidExists = fstabContent.Split('\n').Any(line => line.Contains($"UUID={uuid}"));

            Log(id, $"UUID ya existe en fstab={uuidExists}");

            if (!uuidExists)
            {
                Log(id, "Creando copia de seguridad de /etc/fstab...");
                Ejecutar("sudo", "-S cp /etc/fstab /etc/fstab.bak");

                Log(id, "Añadiendo entrada a /etc/fstab...");
                Ejecutar("sudo", $"-S bash -c \"echo '{fstabEntry}' | tee -a /etc/fstab\"");

                Log(id, "Recargando systemd y ejecutando mount -a...");
                Ejecutar("sudo", "-S systemctl daemon-reload");
                Ejecutar("sudo", "-S mount -a");
            }
            else
            {
                Log(id, "El UUID ya existe en /etc/fstab, no se añadió duplicado.");
                Console.WriteLine($"El UUID {uuid} ya existe en /etc/fstab, no se añadió duplicado.");
            }

            EndTrace(id, nameof(ConfigurarPersistencia), sw);
        }
        catch (Exception ex)
        {
            Log(id, $"Error al configurar persistencia para {destino.Iqn}: {ex}");
            Console.WriteLine($"Error al configurar persistencia para {destino.Iqn}: {ex.Message}");
            EndTrace(id, nameof(ConfigurarPersistencia), sw, "ERROR");
        }
    }

    // ============================================================
    // Crear servicio systemd + script
    // ============================================================

    public static void CrearServicioPersistencia(IscsiDestino destino)
    {
        long id = NextTraceId();
        var sw = StartTrace(id, nameof(CrearServicioPersistencia),
            $"IQN='{destino?.Iqn}', MountPoint='{destino?.MountPoint}'");

        try
        {
            string safeName = SanitizarNombre(destino.Iqn);

            string rawServiceName = $"iscsi-{safeName}.service";
            string servicePath = $"/etc/systemd/system/{rawServiceName}";
            string scriptPath = $"/usr/local/bin/mount-iscsi-{safeName}.sh";

            Log(id, $"safeName='{safeName}', servicePath='{servicePath}', scriptPath='{scriptPath}'");

            string scriptContent = $@"#!/bin/bash
TARGET=""{destino.Iqn}""
PORTAL=""{destino.Ip}""
MOUNTPOINT=""{destino.MountPoint}""

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

            Log(id, "Creando script de persistencia...");
            Ejecutar("sudo",
                $"-S bash -c \"cat > {scriptPath} <<'EOF'\n{scriptContent}\nEOF\"");

            Ejecutar("sudo", $"-S chmod 755 {scriptPath}");
            Ejecutar("sudo", $"-S chown root:root {scriptPath}");

            Ejecutar("sudo",
                $"-S bash -c \"command -v dos2unix >/dev/null 2>&1 && dos2unix {scriptPath}\"");

            string serviceContent = $@"
[Unit]
Description=Conectar iSCSI y montar {destino.Iqn}
After=network-online.target iscsid.service
Requires=network-online.target iscsid.service
Before=remote-fs-pre.target
Wants=remote-fs-pre.target

[Service]
Type=oneshot
ExecStart={scriptPath}
RemainAfterExit=yes

[Install]
WantedBy=multi-user.target
";

            Log(id, "Creando unit de systemd...");
            Ejecutar("sudo",
                $"-S bash -c \"cat > {servicePath} <<'EOF'\n{serviceContent}\nEOF\"");

            Log(id, "Recargando systemd y habilitando servicio...");
            Ejecutar("sudo", "-S systemctl daemon-reload");
            Ejecutar("sudo", $"-S systemctl enable {rawServiceName}");

            EndTrace(id, nameof(CrearServicioPersistencia), sw);
        }
        catch (Exception ex)
        {
            Log(id, $"Error al crear servicio persistente para {destino.Iqn}: {ex}");
            Console.WriteLine($"Error al crear servicio persistente para {destino.Iqn}: {ex.Message}");
            EndTrace(id, nameof(CrearServicioPersistencia), sw, "ERROR");
        }
    }

    // ============================================================
    // Eliminar persistencia
    // ============================================================

    public static void EliminarServicioPersistencia(IscsiDestino destino)
    {
        long id = NextTraceId();
        var sw = StartTrace(id, nameof(EliminarServicioPersistencia),
            $"IQN='{destino?.Iqn}', MountPoint='{destino?.MountPoint}'");

        try
        {
            string safeName = SanitizarNombre(destino.Iqn);

            string rawServiceName = $"iscsi-{safeName}.service";
            string servicePath = $"/etc/systemd/system/{rawServiceName}";
            string scriptPath = $"/usr/local/bin/mount-iscsi-{safeName}.sh";
            string wantsPath = $"/etc/systemd/system/multi-user.target.wants/{rawServiceName}";

            Log(id, $"safeName='{safeName}', servicePath='{servicePath}', scriptPath='{scriptPath}', wantsPath='{wantsPath}'");

            // 1. Deshabilitar servicio solo si existe
            Log(id, "Comprobando estado del servicio...");
            var checkService = Ejecutar("systemctl", $"status {rawServiceName}");
            if (!string.IsNullOrWhiteSpace(checkService))
            {
                Log(id, "Servicio existe, deshabilitando...");
                Ejecutar("sudo", $"-S systemctl disable {rawServiceName}");
            }
            else
            {
                Log(id, "Servicio no existe, se omite disable.");
            }

            // 2. Eliminar symlink en wants si existe
            Log(id, "Eliminando symlink en wants (si existe)...");
            Ejecutar("sudo", $"-S bash -c \"[ -e '{wantsPath}' ] && rm -f '{wantsPath}'\"");

            // 3. Eliminar servicio si existe
            Log(id, "Eliminando unit de servicio (si existe)...");
            Ejecutar("sudo", $"-S bash -c \"[ -e '{servicePath}' ] && rm -f '{servicePath}'\"");

            // 4. Eliminar script si existe
            Log(id, "Eliminando script (si existe)...");
            Ejecutar("sudo", $"-S bash -c \"[ -e '{scriptPath}' ] && rm -f '{scriptPath}'\"");

            // 5. Eliminar entrada de fstab (todas las coincidencias)
            Log(id, "Haciendo backup de /etc/fstab y limpiando entradas del mountpoint...");
            Ejecutar("sudo", "-S cp /etc/fstab /etc/fstab.bak");
            if (!string.IsNullOrEmpty(destino.MountPoint))
            {
                Ejecutar("sudo",
                    $"-S bash -c \"sed -i '\\|{destino.MountPoint}|d' /etc/fstab\"");
            }

            // 6. Recargar systemd
            Log(id, "Recargando systemd...");
            Ejecutar("sudo", "-S systemctl daemon-reload");

            // 7. Ejecutar mount -a para limpiar montajes residuales
            Log(id, "Ejecutando mount -a...");
            Ejecutar("sudo", "-S mount -a");

            // 8. Dejar node.startup en manual (evita reconexiones automáticas)
            Log(id, "Estableciendo node.startup=manual en iscsiadm...");
            Ejecutar("sudo",
                $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} --op update --name node.startup --value manual");

            // 9. Eliminar directorio de montaje si está vacío
            if (!string.IsNullOrEmpty(destino.MountPoint))
            {
                Log(id, $"Intentando eliminar directorio de montaje '{destino.MountPoint}' si está vacío...");
                Ejecutar("sudo", $"-S bash -c \"rmdir '{destino.MountPoint}' 2>/dev/null || true\"");
            }

            EndTrace(id, nameof(EliminarServicioPersistencia), sw);
        }
        catch (Exception ex)
        {
            Log(id, $"Error al eliminar servicio persistente para {destino.Iqn}: {ex}");
            Console.WriteLine($"Error al eliminar servicio persistente para {destino.Iqn}: {ex.Message}");
            EndTrace(id, nameof(EliminarServicioPersistencia), sw, "ERROR");
        }
    }

    // ============================================================
    // Asegurar iscsid
    // ============================================================

    public static void AsegurarServicioIscsid()
    {
        long id = NextTraceId();
        var sw = StartTrace(id, nameof(AsegurarServicioIscsid));

        try
        {
            Log(id, "Comprobando estado de iscsid...");
            var estado = Ejecutar("systemctl", "is-active iscsid").Trim();
            Log(id, $"iscsid estado='{estado}'");

            if (estado != "active")
            {
                NotificadorLinux.Enviar("El servicio iscsid no está activo. Habilitando...");
                Log(id, "Habilitando y arrancando iscsid...");

                Ejecutar("sudo", "-S systemctl enable --now iscsid");
                Ejecutar("sudo", "-S systemctl daemon-reexec");

                Console.WriteLine("[INFO] Servicio iscsid habilitado y arrancado.");
            }

            EndTrace(id, nameof(AsegurarServicioIscsid), sw);
        }
        catch (Exception ex)
        {
            Log(id, $"[ERROR] No se pudo asegurar el servicio iscsid: {ex}");
            Console.WriteLine($"[ERROR] No se pudo asegurar el servicio iscsid: {ex.Message}");
            NotificadorLinux.Enviar("[ERROR] Fallo al comprobar/arrancar iscsid.");
            EndTrace(id, nameof(AsegurarServicioIscsid), sw, "ERROR");
        }
    }

    // ============================================================
    // Obtener destinos conectados
    // ============================================================

    public static List<IscsiDestino> ObtenerDestinosConectados()
    {
        long id = NextTraceId();
        var sw = StartTrace(id, nameof(ObtenerDestinosConectados));

        var destinos = new List<IscsiDestino>();

        try
        {
            Log(id, "Ejecutando iscsiadm -m session...");
            string sesionesOut = Ejecutar("sudo", "-S iscsiadm -m session");
            Log(id, $"Sesiones:\n{sesionesOut}");

            if (string.IsNullOrWhiteSpace(sesionesOut))
            {
                Log(id, "No hay sesiones activas.");
                EndTrace(id, nameof(ObtenerDestinosConectados), sw);
                return destinos;
            }

            var sesiones = sesionesOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var s in sesiones)
            {
                Log(id, $"Procesando sesión: '{s}'");
                var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 3)
                {
                    Log(id, "Línea ignorada: tokens insuficientes.");
                    continue;
                }

                string ip = tokens[2].Split(':')[0];
                string iqn = tokens.LastOrDefault(t => t.StartsWith("iqn."));
                if (string.IsNullOrEmpty(iqn))
                {
                    Log(id, "No se encontró IQN en la línea, se ignora.");
                    continue;
                }

                destinos.Add(new IscsiDestino
                {
                    Ip = ip,
                    Iqn = iqn,
                    Conectado = true,
                    Seleccionado = false
                });

                Log(id, $"Destino conectado añadido: IP='{ip}', IQN='{iqn}'");
            }
        }
        catch (Exception ex)
        {
            Log(id, $"Error al obtener destinos conectados: {ex}");
            Console.WriteLine($"Error al obtener destinos conectados: {ex.Message}");
        }

        Log(id, $"Total destinos conectados: {destinos.Count}");
        EndTrace(id, nameof(ObtenerDestinosConectados), sw);
        return destinos;
    }
}
