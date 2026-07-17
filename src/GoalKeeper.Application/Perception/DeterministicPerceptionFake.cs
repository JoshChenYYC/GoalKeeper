namespace GoalKeeper.Application.Perception;

public abstract record PerceptionFakeStep
{
    private PerceptionFakeStep()
    {
    }

    public static PerceptionFakeStep Return(PerceptionResult result) => new ReturnStep(result);

    public static PerceptionFakeStep Delayed(PerceptionResult result) => new DelayedStep(result);

    public static PerceptionFakeStep Cancelled() => new CancelledStep();

    public static PerceptionFakeStep Throw(Exception exception) => new ThrowStep(exception);

    internal sealed record ReturnStep(PerceptionResult Result) : PerceptionFakeStep;

    internal sealed record DelayedStep(PerceptionResult Result) : PerceptionFakeStep;

    internal sealed record CancelledStep : PerceptionFakeStep;

    internal sealed record ThrowStep(Exception Exception) : PerceptionFakeStep;
}

public sealed class DeterministicPerceptionFake : IPerceptionPort
{
    private readonly object _sync = new();
    private readonly Queue<PerceptionFakeStep> _steps;
    private readonly LinkedList<TaskCompletionSource> _delays = [];
    private readonly List<PerceptionRequest> _requests = [];

    public DeterministicPerceptionFake(IEnumerable<PerceptionFakeStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _steps = new Queue<PerceptionFakeStep>(steps);
    }

    public IReadOnlyList<PerceptionRequest> Requests
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

    public async Task<PerceptionResult> ObserveAsync(
        PerceptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        PerceptionFakeStep step;
        lock (_sync)
        {
            if (_steps.Count == 0)
            {
                throw new InvalidOperationException("The deterministic Perception fake has no scripted response.");
            }

            _requests.Add(request);
            step = _steps.Dequeue();
        }

        switch (step)
        {
            case PerceptionFakeStep.ReturnStep returned:
                return returned.Result;

            case PerceptionFakeStep.DelayedStep delayed:
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

            case PerceptionFakeStep.CancelledStep:
                throw new OperationCanceledException(new CancellationToken(canceled: true));

            case PerceptionFakeStep.ThrowStep failed:
                throw failed.Exception;

            default:
                throw new InvalidOperationException("Unsupported deterministic Perception fake step.");
        }
    }

    public void ReleaseNextDelay()
    {
        TaskCompletionSource signal;
        lock (_sync)
        {
            if (_delays.Count == 0)
            {
                throw new InvalidOperationException("The deterministic Perception fake has no pending delay.");
            }

            signal = _delays.First!.Value;
            _delays.RemoveFirst();
        }

        signal.SetResult();
    }
}
