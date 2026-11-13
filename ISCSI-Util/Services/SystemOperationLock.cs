using System;
using System.Threading;
using System.Threading.Tasks;

namespace ISCSI_Util.Services;

/// <summary>
/// Serializa las operaciones que modifican el estado iSCSI del sistema
/// (conectar, montar, desmontar, borrar nodos, persistencia, rescan, etc.).
///
/// El lock es global y compartido por todas las vistas. Antes cada vista tenía
/// su propio lock, así que SessionsView y TargetsView podían lanzar operaciones
/// incompatibles al mismo tiempo sobre el mismo target.
///
/// Las operaciones se ejecutan en el thread pool para que las llamadas
/// síncronas a sudo de los helpers no bloqueen el hilo de UI.
/// </summary>
public static class SystemOperationLock
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task RunAsync(Func<Task> operation)
    {
        await Gate.WaitAsync().ConfigureAwait(true);
        try
        {
            await Task.Run(operation).ConfigureAwait(true);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        await Gate.WaitAsync().ConfigureAwait(true);
        try
        {
            return await Task.Run(operation).ConfigureAwait(true);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<T> RunAsync<T>(Func<T> operation)
    {
        await Gate.WaitAsync().ConfigureAwait(true);
        try
        {
            return await Task.Run(operation).ConfigureAwait(true);
        }
        finally
        {
            Gate.Release();
        }
    }
}
