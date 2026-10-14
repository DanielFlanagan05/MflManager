namespace MflManager.Application;

/// <summary>UI-thread-owned lifetime for optional automatic draft advice.</summary>
public sealed class DraftAdviceSession
{
    private CancellationTokenSource? _request;
    public bool IsEnabled { get; private set; }

    public void SetEnabled(bool enabled)
    {
        IsEnabled = enabled;
        _request?.Cancel();
        _request = null;
    }

    public CancellationTokenSource? TryBegin()
    {
        if (!IsEnabled || _request is not null) return null;
        return _request = new CancellationTokenSource();
    }

    public void Complete(CancellationTokenSource request)
    {
        if (ReferenceEquals(_request, request)) _request = null;
    }
}
