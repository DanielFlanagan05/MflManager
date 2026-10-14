using MflManager.Application;

namespace MflManager.UnitTests;

public class DraftAdviceSessionTests
{
    [Fact]
    public void DefaultOffCannotStartAndTurningOffCancelsActiveAdvice()
    {
        var session = new DraftAdviceSession();
        Assert.Null(session.TryBegin());
        session.SetEnabled(true);
        using var request = session.TryBegin();
        Assert.NotNull(request);
        Assert.Null(session.TryBegin());
        session.SetEnabled(false);
        Assert.True(request.IsCancellationRequested);
        Assert.False(session.IsEnabled);
        Assert.Null(session.TryBegin());
        session.Complete(request);
        Assert.Null(session.TryBegin());
    }

    [Fact]
    public void OldCompletionCannotClearNewRequestAfterOnOffOn()
    {
        var session = new DraftAdviceSession();
        session.SetEnabled(true);
        using var oldRequest = session.TryBegin()!;
        session.SetEnabled(false);
        session.SetEnabled(true);
        using var newRequest = session.TryBegin()!;
        session.Complete(oldRequest);
        Assert.Null(session.TryBegin());
        Assert.False(newRequest.IsCancellationRequested);
        session.SetEnabled(false);
        Assert.True(newRequest.IsCancellationRequested);
    }
}
