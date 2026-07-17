using System.Text.Json;
using System.Text.RegularExpressions;

namespace GoalKeeper.Application.Perception;

public static partial class ObservationValidator
{
    private static readonly IReadOnlyDictionary<string, ImageQualityValue> ImageQualityValues =
        new Dictionary<string, ImageQualityValue>(StringComparer.Ordinal)
        {
            ["adequate"] = ImageQualityValue.Adequate,
            ["limited"] = ImageQualityValue.Limited,
            ["unusable"] = ImageQualityValue.Unusable
        };

    private static readonly IReadOnlyDictionary<string, VisualSupport> VisualSupportValues =
        new Dictionary<string, VisualSupport>(StringComparer.Ordinal)
        {
            ["direct"] = VisualSupport.Direct,
            ["partial"] = VisualSupport.Partial,
            ["inferred"] = VisualSupport.Inferred,
            ["unavailable"] = VisualSupport.Unavailable
        };

    private static readonly IReadOnlyDictionary<string, PeopleCountStatus> PeopleCountStatusValues =
        new Dictionary<string, PeopleCountStatus>(StringComparer.Ordinal)
        {
            ["counted"] = PeopleCountStatus.Counted,
            ["not_visible"] = PeopleCountStatus.NotVisible,
            ["unknown"] = PeopleCountStatus.Unknown
        };

    private static readonly IReadOnlyDictionary<string, VisibleCueSubject> VisibleCueSubjectValues =
        new Dictionary<string, VisibleCueSubject>(StringComparer.Ordinal)
        {
            ["visible_person"] = VisibleCueSubject.VisiblePerson,
            ["scene"] = VisibleCueSubject.Scene
        };

    private static readonly IReadOnlyDictionary<string, VisibleCueKind> VisibleCueKindValues =
        new Dictionary<string, VisibleCueKind>(StringComparer.Ordinal)
        {
            ["position"] = VisibleCueKind.Position,
            ["orientation"] = VisibleCueKind.Orientation,
            ["posture"] = VisibleCueKind.Posture,
            ["gaze"] = VisibleCueKind.Gaze,
            ["hand_position"] = VisibleCueKind.HandPosition,
            ["object_relation"] = VisibleCueKind.ObjectRelation,
            ["motion"] = VisibleCueKind.Motion,
            ["visibility"] = VisibleCueKind.Visibility,
            ["other_visible"] = VisibleCueKind.OtherVisible
        };

    private static readonly IReadOnlyDictionary<string, VisibleCueState> VisibleCueStateValues =
        new Dictionary<string, VisibleCueState>(StringComparer.Ordinal)
        {
            ["observed"] = VisibleCueState.Observed,
            ["not_visible"] = VisibleCueState.NotVisible,
            ["not_occurring"] = VisibleCueState.NotOccurring,
            ["unknown"] = VisibleCueState.Unknown
        };

    private static readonly HashSet<string> RootFields =
        ["schema_version", "image_quality", "people_count", "objects", "visible_cues"];

    private static readonly HashSet<string> ImageQualityFields = ["value", "limitations"];

    private static readonly HashSet<string> PeopleCountFields = ["status", "value", "support", "limitations"];

    private static readonly HashSet<string> VisibleCueFields =
        ["subject", "kind", "state", "support", "description", "visual_basis", "limitations"];

    private static readonly string[] IdentityTerms =
    [
        "identity", "identified", "recognised", "recognized", "recognition",
        "biometric", "faceprint", "named person", "known person"
    ];

    private static readonly string[] BehavioralJudgmentTerms =
    [
        "goal", "deviation", "violation", "intervention", "off task", "off-task",
        "on task", "on-task", "distracted", "distraction", "unfocused", "focused",
        "productive", "unproductive", "working", "not working", "lazy", "compliant",
        "noncompliant", "engaged", "disengaged", "intentional", "intentionally",
        "should be", "must be"
    ];

    public static ObservationValidationResult Validate(
        ReadOnlyMemory<byte> utf8Json,
        PerceptionRequestOptions? options = null)
    {
        options ??= PerceptionRequestOptions.Default;
        if (utf8Json.IsEmpty)
        {
            return Invalid("$", ObservationValidationErrorCode.MalformedJson, "The response body is empty.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json);
        }
        catch (JsonException)
        {
            return Invalid("$", ObservationValidationErrorCode.MalformedJson,
                "The response is not valid JSON.");
        }

        using (document)
        {
            var issues = new List<ObservationValidationIssue>();
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new("$", ObservationValidationErrorCode.InvalidRoot,
                    "The response root must be an object."));
                return new InvalidObservation(new(issues));
            }

            ValidateFields(document.RootElement, "$", RootFields, issues);

            var schemaVersion = RequiredInteger(
                document.RootElement, "schema_version", "$.schema_version", issues);
            if (schemaVersion is not null && schemaVersion != ObservationSchemaVersions.V1)
            {
                issues.Add(new("$.schema_version", ObservationValidationErrorCode.InvalidEnum,
                    $"Only Observation schema {ObservationSchemaVersions.V1} is accepted."));
            }

            var imageQuality = ParseImageQuality(document.RootElement, issues);
            var peopleCount = ParsePeopleCount(document.RootElement, issues);
            var objects = ParseObjects(document.RootElement, options.MaximumObjects, issues);
            var visibleCues = ParseVisibleCues(document.RootElement, options.MaximumVisibleCues, issues);

            if (peopleCount is not null &&
                peopleCount.Status != PeopleCountStatus.Counted &&
                visibleCues.Any(cue => cue.Subject == VisibleCueSubject.VisiblePerson))
            {
                issues.Add(new("$.visible_cues", ObservationValidationErrorCode.InconsistentValue,
                    "Person-specific cues require exactly one counted visible person."));
            }

            if (peopleCount is not null &&
                peopleCount.Status == PeopleCountStatus.Counted &&
                peopleCount.Value != 1 &&
                visibleCues.Any(cue => cue.Subject == VisibleCueSubject.VisiblePerson))
            {
                issues.Add(new("$.visible_cues", ObservationValidationErrorCode.InconsistentValue,
                    "Person-specific cues are ambiguous unless exactly one person is visible."));
            }

            if (issues.Count > 0 ||
                schemaVersion is null ||
                imageQuality is null ||
                peopleCount is null)
            {
                return new InvalidObservation(new(issues));
            }

            return new ValidatedObservation(new Observation(
                schemaVersion.Value,
                imageQuality,
                peopleCount,
                objects,
                visibleCues));
        }
    }

    private static ImageQuality? ParseImageQuality(
        JsonElement root,
        List<ObservationValidationIssue> issues)
    {
        const string path = "$.image_quality";
        if (!RequiredObject(root, "image_quality", path, issues, out var element))
        {
            return null;
        }

        ValidateFields(element, path, ImageQualityFields, issues);
        var value = RequiredEnum(element, "value", $"{path}.value", ImageQualityValues, issues);
        var limitations = RequiredStringArray(
            element,
            "limitations",
            $"{path}.limitations",
            ObservationLimits.MaximumLimitations,
            ObservationLimits.MaximumLimitationLength,
            issues);
        ValidateNeutralTextValues(limitations, $"{path}.limitations", issues);

        if (value is ImageQualityValue.Limited or ImageQualityValue.Unusable &&
            limitations.Count == 0)
        {
            issues.Add(new($"{path}.limitations", ObservationValidationErrorCode.InconsistentValue,
                "Limited or unusable image quality requires at least one limitation."));
        }

        return value is null ? null : new ImageQuality(value.Value, limitations);
    }

    private static PeopleCount? ParsePeopleCount(
        JsonElement root,
        List<ObservationValidationIssue> issues)
    {
        const string path = "$.people_count";
        if (!RequiredObject(root, "people_count", path, issues, out var element))
        {
            return null;
        }

        ValidateFields(element, path, PeopleCountFields, issues);
        var status = RequiredEnum(element, "status", $"{path}.status", PeopleCountStatusValues, issues);
        var support = RequiredEnum(element, "support", $"{path}.support", VisualSupportValues, issues);
        var limitations = RequiredStringArray(
            element,
            "limitations",
            $"{path}.limitations",
            ObservationLimits.MaximumLimitations,
            ObservationLimits.MaximumLimitationLength,
            issues);
        ValidateNeutralTextValues(limitations, $"{path}.limitations", issues);
        var value = NullableInteger(element, "value", $"{path}.value", issues);

        if (status is not null && support is not null)
        {
            switch (status.Value)
            {
                case PeopleCountStatus.Counted when value is null or < 1 or > ObservationLimits.MaximumPeople:
                    issues.Add(new($"{path}.value", ObservationValidationErrorCode.InconsistentValue,
                        $"A counted result requires a value from 1 through {ObservationLimits.MaximumPeople}."));
                    break;
                case PeopleCountStatus.Counted when support == VisualSupport.Unavailable:
                    issues.Add(new($"{path}.support", ObservationValidationErrorCode.InconsistentValue,
                        "A counted result cannot have unavailable support."));
                    break;
                case PeopleCountStatus.NotVisible when value != 0:
                    issues.Add(new($"{path}.value", ObservationValidationErrorCode.InconsistentValue,
                        "A not-visible result requires a value of zero."));
                    break;
                case PeopleCountStatus.NotVisible when support == VisualSupport.Unavailable:
                    issues.Add(new($"{path}.support", ObservationValidationErrorCode.InconsistentValue,
                        "A not-visible result requires direct, partial, or inferred support."));
                    break;
                case PeopleCountStatus.Unknown when value is not null:
                    issues.Add(new($"{path}.value", ObservationValidationErrorCode.InconsistentValue,
                        "An unknown result requires a null value."));
                    break;
                case PeopleCountStatus.Unknown when support != VisualSupport.Unavailable:
                    issues.Add(new($"{path}.support", ObservationValidationErrorCode.InconsistentValue,
                        "An unknown result requires unavailable support."));
                    break;
            }
        }

        return status is null || support is null
            ? null
            : new PeopleCount(status.Value, value, support.Value, limitations);
    }

    private static List<string> ParseObjects(
        JsonElement root,
        int maximumObjects,
        List<ObservationValidationIssue> issues)
    {
        var objects = RequiredStringArray(
            root,
            "objects",
            "$.objects",
            maximumObjects,
            ObservationLimits.MaximumObjectLabelLength,
            issues);

        foreach (var (label, index) in objects.Select((value, index) => (value, index)))
        {
            ValidateNeutralText(label, $"$.objects[{index}]", issues);
        }

        return objects;
    }

    private static List<VisibleCue> ParseVisibleCues(
        JsonElement root,
        int maximumVisibleCues,
        List<ObservationValidationIssue> issues)
    {
        const string path = "$.visible_cues";
        if (!root.TryGetProperty("visible_cues", out var element))
        {
            issues.Add(new(path, ObservationValidationErrorCode.MissingField,
                "The required field is missing."));
            return [];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new(path, ObservationValidationErrorCode.InvalidType,
                "The field must be an array."));
            return [];
        }

        if (element.GetArrayLength() > maximumVisibleCues)
        {
            issues.Add(new(path, ObservationValidationErrorCode.TooManyItems,
                $"At most {maximumVisibleCues} visible cues are allowed."));
        }

        var cues = new List<VisibleCue>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPath = $"{path}[{index}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new(itemPath, ObservationValidationErrorCode.InvalidType,
                    "A visible cue must be an object."));
                index++;
                continue;
            }

            ValidateFields(item, itemPath, VisibleCueFields, issues);
            var subject = RequiredEnum(item, "subject", $"{itemPath}.subject", VisibleCueSubjectValues, issues);
            var kind = RequiredEnum(item, "kind", $"{itemPath}.kind", VisibleCueKindValues, issues);
            var state = RequiredEnum(item, "state", $"{itemPath}.state", VisibleCueStateValues, issues);
            var support = RequiredEnum(item, "support", $"{itemPath}.support", VisualSupportValues, issues);
            var description = RequiredString(
                item,
                "description",
                $"{itemPath}.description",
                ObservationLimits.MaximumDescriptionLength,
                issues);
            var visualBasis = RequiredNullableString(
                item,
                "visual_basis",
                $"{itemPath}.visual_basis",
                ObservationLimits.MaximumVisualBasisLength,
                issues);
            var limitations = RequiredStringArray(
                item,
                "limitations",
                $"{itemPath}.limitations",
                ObservationLimits.MaximumLimitations,
                ObservationLimits.MaximumLimitationLength,
                issues);

            if (description is not null)
            {
                ValidateNeutralText(description, $"{itemPath}.description", issues);
            }

            if (visualBasis is not null)
            {
                ValidateNeutralText(visualBasis, $"{itemPath}.visual_basis", issues);
            }

            ValidateNeutralTextValues(limitations, $"{itemPath}.limitations", issues);

            if (state is not null && support is not null)
            {
                var requiresBasis = state is VisibleCueState.Observed or VisibleCueState.NotOccurring;
                if (requiresBasis && support == VisualSupport.Unavailable)
                {
                    issues.Add(new($"{itemPath}.support", ObservationValidationErrorCode.InconsistentValue,
                        "Observed and not-occurring cues require available visual support."));
                }

                if (requiresBasis && visualBasis is null)
                {
                    issues.Add(new($"{itemPath}.visual_basis", ObservationValidationErrorCode.InconsistentValue,
                        "Observed and not-occurring cues require a visual basis."));
                }

                if (!requiresBasis && support != VisualSupport.Unavailable)
                {
                    issues.Add(new($"{itemPath}.support", ObservationValidationErrorCode.InconsistentValue,
                        "Unknown and not-visible cues require unavailable support."));
                }

                if (!requiresBasis && visualBasis is not null)
                {
                    issues.Add(new($"{itemPath}.visual_basis", ObservationValidationErrorCode.InconsistentValue,
                        "Unknown and not-visible cues cannot claim a visual basis."));
                }
            }

            if (subject is not null &&
                kind is not null &&
                state is not null &&
                support is not null &&
                description is not null)
            {
                cues.Add(new(
                    subject.Value,
                    kind.Value,
                    state.Value,
                    support.Value,
                    description,
                    visualBasis,
                    limitations));
            }

            index++;
        }

        return cues;
    }

    private static void ValidateFields(
        JsonElement element,
        string path,
        HashSet<string> allowed,
        List<ObservationValidationIssue> issues)
    {
        var encountered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!encountered.Add(property.Name))
            {
                issues.Add(new($"{path}.{property.Name}", ObservationValidationErrorCode.DuplicateField,
                    "Duplicate fields are not allowed."));
            }

            if (!allowed.Contains(property.Name))
            {
                issues.Add(new($"{path}.{property.Name}", ObservationValidationErrorCode.UnknownField,
                    "Unknown fields are not allowed."));
            }
        }
    }

    private static bool RequiredObject(
        JsonElement parent,
        string name,
        string path,
        List<ObservationValidationIssue> issues,
        out JsonElement value)
    {
        if (!parent.TryGetProperty(name, out value))
        {
            issues.Add(new(path, ObservationValidationErrorCode.MissingField,
                "The required field is missing."));
            return false;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new(path, ObservationValidationErrorCode.InvalidType,
                "The field must be an object."));
            return false;
        }

        return true;
    }

    private static string? RequiredString(
        JsonElement parent,
        string name,
        string path,
        int maximumLength,
        List<ObservationValidationIssue> issues)
    {
        if (!parent.TryGetProperty(name, out var element))
        {
            issues.Add(new(path, ObservationValidationErrorCode.MissingField,
                "The required field is missing."));
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            issues.Add(new(path, ObservationValidationErrorCode.InvalidType,
                "The field must be a string."));
            return null;
        }

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(new(path, ObservationValidationErrorCode.EmptyValue,
                "The field cannot be empty."));
            return null;
        }

        if (value.Length > maximumLength || value.Any(char.IsControl))
        {
            issues.Add(new(path, ObservationValidationErrorCode.OutOfRange,
                $"The field must contain at most {maximumLength} safe characters."));
            return null;
        }

        return value;
    }

    private static string? RequiredNullableString(
        JsonElement parent,
        string name,
        string path,
        int maximumLength,
        List<ObservationValidationIssue> issues)
    {
        if (!parent.TryGetProperty(name, out var element))
        {
            issues.Add(new(path, ObservationValidationErrorCode.MissingField,
                "The required field is missing."));
            return null;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            issues.Add(new(path, ObservationValidationErrorCode.InvalidType,
                "The field must be a string or null."));
            return null;
        }

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(new(path, ObservationValidationErrorCode.EmptyValue,
                "Use null instead of an empty string."));
            return null;
        }

        if (value.Length > maximumLength || value.Any(char.IsControl))
        {
            issues.Add(new(path, ObservationValidationErrorCode.OutOfRange,
                $"The field must contain at most {maximumLength} safe characters."));
            return null;
        }

        return value;
    }

    private static TEnum? RequiredEnum<TEnum>(
        JsonElement parent,
        string name,
        string path,
        IReadOnlyDictionary<string, TEnum> allowed,
        List<ObservationValidationIssue> issues)
        where TEnum : struct, Enum
    {
        var raw = RequiredString(parent, name, path, 40, issues);
        if (raw is null)
        {
            return null;
        }

        if (allowed.TryGetValue(raw, out var parsed))
        {
            return parsed;
        }

        issues.Add(new(path, ObservationValidationErrorCode.InvalidEnum,
            $"'{raw}' is not an allowed value."));
        return null;
    }

    private static int? NullableInteger(
        JsonElement parent,
        string name,
        string path,
        List<ObservationValidationIssue> issues)
    {
        if (!parent.TryGetProperty(name, out var element))
        {
            issues.Add(new(path, ObservationValidationErrorCode.MissingField,
                "The required field is missing."));
            return null;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            issues.Add(new(path, ObservationValidationErrorCode.InvalidType,
                "The field must be an integer or null."));
            return null;
        }

        if (value is < 0 or > ObservationLimits.MaximumPeople)
        {
            issues.Add(new(path, ObservationValidationErrorCode.OutOfRange,
                $"The field must be from 0 through {ObservationLimits.MaximumPeople}."));
        }

        return value;
    }

    private static int? RequiredInteger(
        JsonElement parent,
        string name,
        string path,
        List<ObservationValidationIssue> issues)
    {
        if (!parent.TryGetProperty(name, out var element))
        {
            issues.Add(new(path, ObservationValidationErrorCode.MissingField,
                "The required field is missing."));
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            issues.Add(new(path, ObservationValidationErrorCode.InvalidType,
                "The field must be an integer."));
            return null;
        }

        return value;
    }

    private static List<string> RequiredStringArray(
        JsonElement parent,
        string name,
        string path,
        int maximumItems,
        int maximumItemLength,
        List<ObservationValidationIssue> issues)
    {
        if (!parent.TryGetProperty(name, out var element))
        {
            issues.Add(new(path, ObservationValidationErrorCode.MissingField,
                "The required field is missing."));
            return [];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new(path, ObservationValidationErrorCode.InvalidType,
                "The field must be an array."));
            return [];
        }

        if (element.GetArrayLength() > maximumItems)
        {
            issues.Add(new(path, ObservationValidationErrorCode.TooManyItems,
                $"At most {maximumItems} items are allowed."));
        }

        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPath = $"{path}[{index}]";
            if (item.ValueKind != JsonValueKind.String)
            {
                issues.Add(new(itemPath, ObservationValidationErrorCode.InvalidType,
                    "The item must be a string."));
                index++;
                continue;
            }

            var value = item.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                issues.Add(new(itemPath, ObservationValidationErrorCode.EmptyValue,
                    "The item cannot be empty."));
            }
            else if (value.Length > maximumItemLength || value.Any(char.IsControl))
            {
                issues.Add(new(itemPath, ObservationValidationErrorCode.OutOfRange,
                    $"The item must contain at most {maximumItemLength} safe characters."));
            }
            else if (!seen.Add(value))
            {
                issues.Add(new(itemPath, ObservationValidationErrorCode.DuplicateValue,
                    "Duplicate values are not allowed."));
            }
            else
            {
                values.Add(value);
            }

            index++;
        }

        return values;
    }

    private static void ValidateNeutralText(
        string value,
        string path,
        List<ObservationValidationIssue> issues)
    {
        if (IdentityClaimPattern().IsMatch(value) ||
            IdentityTerms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new(path, ObservationValidationErrorCode.IdentityClaim,
                "Identity and recognition claims are outside the Perception boundary."));
        }

        if (BehavioralJudgmentTerms.Any(term =>
                WholeTerm(value, term)))
        {
            issues.Add(new(path, ObservationValidationErrorCode.BehavioralJudgment,
                "Goal, Deviation, intention, productivity, and focus judgments are outside the Perception boundary."));
        }
    }

    private static void ValidateNeutralTextValues(
        IReadOnlyList<string> values,
        string path,
        List<ObservationValidationIssue> issues)
    {
        foreach (var (value, index) in values.Select((value, index) => (value, index)))
        {
            ValidateNeutralText(value, $"{path}[{index}]", issues);
        }
    }

    private static bool WholeTerm(string value, string term)
    {
        var index = value.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var beforeBoundary = index == 0 || !char.IsLetterOrDigit(value[index - 1]);
            var afterIndex = index + term.Length;
            var afterBoundary = afterIndex == value.Length || !char.IsLetterOrDigit(value[afterIndex]);
            if (beforeBoundary && afterBoundary)
            {
                return true;
            }

            index = value.IndexOf(term, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static InvalidObservation Invalid(
        string path,
        ObservationValidationErrorCode code,
        string message) =>
        new(new ObservationValidationFailure([new(path, code, message)]));

    [GeneratedRegex(
        @"\b(?:person|subject|individual|face)\s+(?:is|was|matches|belongs to)\s+[A-Z][\p{L}'-]*\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex IdentityClaimPattern();
}
