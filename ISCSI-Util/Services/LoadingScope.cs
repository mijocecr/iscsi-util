using System;
using System.Threading;

namespace ISCSI_Util.Services;

/// <summary>
/// Scope de una operación con diálogo de carga. Es idempotente: llamar a
/// Dispose varias veces no descuenta referencias de más.
/// </summary>
public class LoadingScope : IDisposable
{
    private readonly Action _onDispose;
    private int _disposed;

    public LoadingScope(Action onDispose)
    {
        _onDispose = onDispose;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _onDispose?.Invoke();
    }
}
