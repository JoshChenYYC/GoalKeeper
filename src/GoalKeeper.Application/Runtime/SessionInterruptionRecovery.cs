using GoalKeeper.Domain;

namespace GoalKeeper.Application.Runtime;

public sealed class SessionInterruptionRecovery(
    IGoalKeeperRepository repository,
    IClock clock)
{
    private const int MaximumAttempts = 3;

    public async Task<FocusSessionRuntimeView?> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            var active = await repository.GetActiveSessionAsync(cancellationToken)
                .ConfigureAwait(false);
            if (active is null)
            {
                return null;
            }

            var goal = await repository.GetGoalAsync(active.GoalId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException("Goal not found.");
            var contract = await repository.GetContractAsync(
                    active.ContractId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException("Session Contract not found.");
            var session = RuntimeAggregateFactory.RehydrateSession(
                goal,
                contract,
                active.Runtime,
                clock);
            var fromState = session.State;
            session.EndAfterApplicationInterruption();
            var runtime = session.CreateSnapshot();
            var mutation = new RuntimeMutation(
                active.Version,
                runtime,
                [new RuntimeAuditWrite(
                    runtime.EndedAtUtc ?? clock.UtcNow,
                    "runtime.application_interrupted",
                    fromState,
                    runtime.State,
                    "{}")]);
            try
            {
                return await repository.UpdateSessionAsync(
                        active.Id,
                        mutation,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (PersistenceConflictException exception)
            {
                if (attempt == MaximumAttempts)
                {
                    throw new PersistenceConflictException(
                        "The interrupted Focus Session could not be reconciled after repeated concurrent changes.")
                    {
                        Source = exception.Source
                    };
                }

                // Another startup path changed the active session. Reload and
                // reconcile only if a nonterminal session still remains.
            }
        }

        throw new PersistenceConflictException(
            "The interrupted Focus Session could not be reconciled after repeated concurrent changes.");
    }
}
