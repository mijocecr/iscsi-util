using System;

namespace ISCSI_Util.Services;

public class LoadingScope : IDisposable
{
    private readonly Action _onDispose;

    public LoadingScope(Action onDispose)
    {
        _onDispose = onDispose;
    }

    public void Dispose()
    {
        _onDispose?.Invoke();
    }
}