namespace GoalKeeper.Application.Recovery;

public abstract record RecoveryFakeStep
{
    private RecoveryFakeStep()
    {
    }

    public static RecoveryFakeStep Return(RecoveryPortResult result) => new ReturnStep(result);

    public static RecoveryFakeStep Delayed(RecoveryPortResult result) => new DelayedStep(result);

    public static RecoveryFakeStep Cancelled() => new CancelledStep();

    public static RecoveryFakeStep Throw(Exception exception) => new ThrowStep(exception);

    internal sealed record ReturnStep(RecoveryPortResult Result) : RecoveryFakeStep;

    internal sealed record DelayedStep(RecoveryPortResult Result) : RecoveryFakeStep;

    internal sealed record CancelledStep : RecoveryFakeStep;

    internal sealed record ThrowStep(Exception Exception) : RecoveryFakeStep;
}

public sealed class DeterministicTextRecoveryFake : IRecoveryPort
{
    private readonly object _sync = new();
    private readonly Queue<RecoveryFakeStep> _steps;
    private readonly LinkedList<TaskCompletionSource> _delays = [];
    private readonly List<RecoveryRequest> _requests = [];

    public DeterministicTextRecoveryFake(IEnumerable<RecoveryFakeStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _steps = new Queue<RecoveryFakeStep>(steps);
    }

    public IReadOnlyList<RecoveryRequest> Requests
    {
        get
        {
            lock (_sync)
            {
                return _requests.ToArray();
            }
        }
    }

    public int PendingDelayCount
    {
        get
        {
            lock (_sync)
            {
                return _delays.Count;
            }
        }
    }

    public async Task<RecoveryPortResult> ProposeAsync(
        RecoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        RecoveryFakeStep step;
        lock (_sync)
        {
            if (_steps.Count == 0)
            {
                throw new InvalidOperationException("The deterministic Recovery fake has no scripted response.");
            }

            _requests.Add(request);
            step = _steps.Dequeue();
        }

        switch (step)
        {
            case RecoveryFakeStep.ReturnStep returned:
                return returned.Result;

            case RecoveryFakeStep.DelayedStep delayed:
                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                LinkedListNode<TaskCompletionSource> node;
                lock (_sync)
                {
                    node = _delays.AddLast(signal);
                }

                try
                {
                    await signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    lock (_sync)
                    {
                        if (node.List is not null)
                        {
                            _delays.Remove(node);
                        }
                    }
                }

                return delayed.Result;

            case RecoveryFakeStep.CancelledStep:
                throw new OperationCanceledException(new CancellationToken(canceled: true));

            case RecoveryFakeStep.ThrowStep failed:
                throw failed.Exception;

            default:
                throw new InvalidOperationException("Unsupported deterministic Recovery fake step.");
        }
    }

    public void ReleaseNextDelay()
    {
        TaskCompletionSource signal;
        lock (_sync)
        {
            if (_delays.Count == 0)
            {
                throw new InvalidOperationException("The deterministic Recovery fake has no pending delay.");
            }

            signal = _delays.First!.Value;
            _delays.RemoveFirst();
        }

        signal.SetResult();
    }
}
