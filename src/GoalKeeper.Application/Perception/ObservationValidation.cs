namespace GoalKeeper.Application.Perception;

public enum ObservationValidationErrorCode
{
    MalformedJson,
    InvalidRoot,
    UnknownField,
    DuplicateField,
    MissingField,
    InvalidType,
    InvalidEnum,
    OutOfRange,
    EmptyValue,
    TooManyItems,
    DuplicateValue,
    IdentityClaim,
    BehavioralJudgment,
    InconsistentValue
}

public sealed record ObservationValidationIssue(
    string Path,
    ObservationValidationErrorCode Code,
    string Message);

public sealed record ObservationValidationFailure
{
    public ObservationValidationFailure(IEnumerable<ObservationValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        Issues = Array.AsReadOnly(issues.ToArray());
        if (Issues.Count == 0)
        {
            throw new ArgumentException("At least one validation issue is required.", nameof(issues));
        }
    }

    public IReadOnlyList<ObservationValidationIssue> Issues { get; }
}

public abstract record ObservationValidationResult;

public sealed record ValidatedObservation(Observation Value) : ObservationValidationResult;

public sealed record InvalidObservation(ObservationValidationFailure Failure) : ObservationValidationResult;
