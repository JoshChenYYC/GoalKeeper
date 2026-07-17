using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GoalKeeper.Application.Perception;

namespace GoalKeeper.Application.Tests;

public sealed class ObservationValidatorTests
{
    public static TheoryData<string, string[]> ImageQualityCases => new()
    {
        { "adequate", [] },
        { "limited", ["lower portion is occluded"] },
        { "unusable", ["image is fully dark"] }
    };

    public static TheoryData<string, int?, string> PeopleCountCases => new()
    {
        { "counted", 1, "direct" },
        { "not_visible", 0, "partial" },
        { "unknown", null, "unavailable" }
    };

    public static TheoryData<string, string, string?> VisibleCueStateCases => new()
    {
        { "observed", "direct", "head is oriented toward the phone" },
        { "not_visible", "unavailable", null },
        { "not_occurring", "partial", "both hands remain separated from the phone" },
        { "unknown", "unavailable", null }
    };

    [Fact]
    public void Valid_v1_response_constructs_an_immutable_neutral_observation()
    {
        var result = ObservationValidator.Validate(Encoding.UTF8.GetBytes(ValidJson));

        var observation = Assert.IsType<ValidatedObservation>(result).Value;
        Assert.Equal(ObservationSchemaVersions.V1, observation.SchemaVersion);
        Assert.Equal(ImageQualityValue.Adequate, observation.ImageQuality.Value);
        Assert.Equal(1, observation.PeopleCount.Value);
        Assert.Equal(["phone", "laptop"], observation.Objects);
        var cue = Assert.Single(observation.VisibleCues);
        Assert.Equal(VisibleCueSubject.VisiblePerson, cue.Subject);
        Assert.Equal(VisibleCueKind.Gaze, cue.Kind);
        Assert.Equal(VisibleCueState.Observed, cue.State);
        Assert.Equal(VisualSupport.Partial, cue.Support);
    }

    [Fact]
    public void Validated_observation_serializes_back_to_the_canonical_v1_schema()
    {
        var observation = Assert.IsType<ValidatedObservation>(
            ObservationValidator.Validate(Encoding.UTF8.GetBytes(ValidJson))).Value;

        var canonicalJson = JsonSerializer.SerializeToUtf8Bytes(observation);

        var roundTrip = Assert.IsType<ValidatedObservation>(
            ObservationValidator.Validate(canonicalJson)).Value;
        Assert.Equal(observation.SchemaVersion, roundTrip.SchemaVersion);
        Assert.Equal(observation.PeopleCount.Status, roundTrip.PeopleCount.Status);
        Assert.Equal(observation.PeopleCount.Value, roundTrip.PeopleCount.Value);
        Assert.Equal(observation.PeopleCount.Support, roundTrip.PeopleCount.Support);
        Assert.Equal(observation.Objects, roundTrip.Objects);
        Assert.Equal(observation.VisibleCues[0].State, roundTrip.VisibleCues[0].State);
        Assert.Equal(observation.VisibleCues[0].Description, roundTrip.VisibleCues[0].Description);
    }

    [Theory]
    [MemberData(nameof(ImageQualityCases))]
    public void Every_image_quality_enum_value_is_accepted(string value, string[] limitations)
    {
        var json = Mutate(root =>
        {
            var quality = root["image_quality"]!.AsObject();
            quality["value"] = value;
            quality["limitations"] = new JsonArray(
                limitations.Select(value => JsonValue.Create(value)).ToArray());
        });

        Assert.IsType<ValidatedObservation>(ObservationValidator.Validate(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [MemberData(nameof(PeopleCountCases))]
    public void Every_people_count_state_is_distinct_and_accepted(
        string status,
        int? value,
        string support)
    {
        var json = Mutate(root =>
        {
            var count = root["people_count"]!.AsObject();
            count["status"] = status;
            count["value"] = value;
            count["support"] = support;
            root["visible_cues"] = new JsonArray();
        });

        var observation = Assert.IsType<ValidatedObservation>(
            ObservationValidator.Validate(Encoding.UTF8.GetBytes(json))).Value;
        Assert.Equal(value, observation.PeopleCount.Value);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("partial")]
    [InlineData("inferred")]
    [InlineData("unavailable")]
    public void Every_visual_support_enum_value_is_accepted(string support)
    {
        var json = support == "unavailable"
            ? Mutate(root =>
            {
                var count = root["people_count"]!.AsObject();
                count["status"] = "unknown";
                count["value"] = null;
                count["support"] = support;
                root["visible_cues"] = new JsonArray();
            })
            : Mutate(root => root["people_count"]!["support"] = support);

        Assert.IsType<ValidatedObservation>(ObservationValidator.Validate(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("visible_person")]
    [InlineData("scene")]
    public void Every_visible_cue_subject_is_accepted(string subject)
    {
        var json = Mutate(root => FirstCue(root)["subject"] = subject);

        Assert.IsType<ValidatedObservation>(ObservationValidator.Validate(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("position")]
    [InlineData("orientation")]
    [InlineData("posture")]
    [InlineData("gaze")]
    [InlineData("hand_position")]
    [InlineData("object_relation")]
    [InlineData("motion")]
    [InlineData("visibility")]
    [InlineData("other_visible")]
    public void Every_visible_cue_kind_is_accepted(string kind)
    {
        var json = Mutate(root => FirstCue(root)["kind"] = kind);

        Assert.IsType<ValidatedObservation>(ObservationValidator.Validate(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [MemberData(nameof(VisibleCueStateCases))]
    public void Cue_states_distinguish_observed_not_visible_not_occurring_and_unknown(
        string state,
        string support,
        string? visualBasis)
    {
        var json = Mutate(root =>
        {
            var cue = FirstCue(root);
            cue["state"] = state;
            cue["support"] = support;
            cue["visual_basis"] = visualBasis;
        });

        var observation = Assert.IsType<ValidatedObservation>(
            ObservationValidator.Validate(Encoding.UTF8.GetBytes(json))).Value;
        Assert.Single(observation.VisibleCues);
    }

    [Theory]
    [InlineData("schema_version")]
    [InlineData("image_quality")]
    [InlineData("people_count")]
    [InlineData("objects")]
    [InlineData("visible_cues")]
    public void Every_root_field_is_required(string field)
    {
        var result = Invalid(Mutate(root => root.Remove(field)));

        Assert.Contains(result.Failure.Issues,
            issue => issue.Code == ObservationValidationErrorCode.MissingField);
    }

    [Theory]
    [InlineData("image_quality", "value")]
    [InlineData("image_quality", "limitations")]
    [InlineData("people_count", "status")]
    [InlineData("people_count", "value")]
    [InlineData("people_count", "support")]
    [InlineData("people_count", "limitations")]
    public void Every_nested_fixed_field_is_required(string container, string field)
    {
        var result = Invalid(Mutate(root => root[container]!.AsObject().Remove(field)));

        Assert.Contains(result.Failure.Issues,
            issue => issue.Code == ObservationValidationErrorCode.MissingField);
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("kind")]
    [InlineData("state")]
    [InlineData("support")]
    [InlineData("description")]
    [InlineData("visual_basis")]
    [InlineData("limitations")]
    public void Every_visible_cue_field_is_required(string field)
    {
        var result = Invalid(Mutate(root => FirstCue(root).Remove(field)));

        Assert.Contains(result.Failure.Issues,
            issue => issue.Code == ObservationValidationErrorCode.MissingField);
    }

    [Theory]
    [InlineData("image_quality", "value")]
    [InlineData("people_count", "status")]
    [InlineData("people_count", "support")]
    [InlineData("visible_cue", "subject")]
    [InlineData("visible_cue", "kind")]
    [InlineData("visible_cue", "state")]
    [InlineData("visible_cue", "support")]
    public void Every_enum_boundary_rejects_unknown_values(string container, string field)
    {
        var result = Invalid(Mutate(root =>
        {
            var target = container == "visible_cue"
                ? FirstCue(root)
                : root[container]!.AsObject();
            target[field] = "invented";
        }));

        Assert.Contains(result.Failure.Issues,
            issue => issue.Code == ObservationValidationErrorCode.InvalidEnum);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("image_quality")]
    [InlineData("people_count")]
    [InlineData("visible_cue")]
    public void Unknown_fields_are_rejected_at_every_object_level(string container)
    {
        var result = Invalid(Mutate(root =>
        {
            var target = container switch
            {
                "root" => root,
                "visible_cue" => FirstCue(root),
                _ => root[container]!.AsObject()
            };
            target["surprise"] = true;
        }));

        Assert.Contains(result.Failure.Issues,
            issue => issue.Code == ObservationValidationErrorCode.UnknownField);
    }

    [Fact]
    public void Duplicate_fields_malformed_json_and_non_object_roots_are_typed_invalid_results()
    {
        const string duplicate = """
            {
              "schema_version": 1,
              "schema_version": 1,
              "image_quality": {"value":"adequate","limitations":[]},
              "people_count": {"status":"counted","value":1,"support":"direct","limitations":[]},
              "objects": [],
              "visible_cues": []
            }
            """;

        Assert.Contains(Invalid(duplicate).Failure.Issues,
            issue => issue.Code == ObservationValidationErrorCode.DuplicateField);
        Assert.Contains(Invalid("{no").Failure.Issues,
            issue => issue.Code == ObservationValidationErrorCode.MalformedJson);
        Assert.Contains(Invalid("[]").Failure.Issues,
            issue => issue.Code == ObservationValidationErrorCode.InvalidRoot);
    }

    [Fact]
    public void Required_data_rejects_null_wrong_type_empty_and_out_of_range_values()
    {
        AssertIssue(Mutate(root => root["schema_version"] = null), ObservationValidationErrorCode.InvalidType);
        AssertIssue(Mutate(root => root["schema_version"] = "1"), ObservationValidationErrorCode.InvalidType);
        AssertIssue(Mutate(root => root["objects"] = "phone"), ObservationValidationErrorCode.InvalidType);
        AssertIssue(Mutate(root => FirstCue(root)["description"] = " "), ObservationValidationErrorCode.EmptyValue);
        AssertIssue(Mutate(root => root["people_count"]!["value"] = 21), ObservationValidationErrorCode.OutOfRange);
        AssertIssue(Mutate(root => root["objects"] = ArrayOf("object", 33)),
            ObservationValidationErrorCode.TooManyItems);
        AssertIssue(Mutate(root => root["visible_cues"] = ArrayOf(FirstCue(root), 21)),
            ObservationValidationErrorCode.TooManyItems);
        AssertIssue(Mutate(root => root["objects"] = new JsonArray("phone", "PHONE")),
            ObservationValidationErrorCode.DuplicateValue);
    }

    [Fact]
    public void Cross_field_rules_preserve_uncertainty_as_uncertainty()
    {
        AssertIssue(Mutate(root =>
        {
            var count = root["people_count"]!.AsObject();
            count["status"] = "unknown";
            count["value"] = 1;
            count["support"] = "unavailable";
            root["visible_cues"] = new JsonArray();
        }), ObservationValidationErrorCode.InconsistentValue);

        AssertIssue(Mutate(root =>
        {
            var cue = FirstCue(root);
            cue["state"] = "unknown";
            cue["support"] = "direct";
            cue["visual_basis"] = "head direction can be seen";
        }), ObservationValidationErrorCode.InconsistentValue);

        AssertIssue(Mutate(root =>
        {
            root["image_quality"]!["value"] = "unusable";
            root["image_quality"]!["limitations"] = new JsonArray();
        }), ObservationValidationErrorCode.InconsistentValue);
    }

    [Fact]
    public void Provider_safe_request_options_can_tighten_but_not_expand_schema_bounds()
    {
        var options = new PerceptionRequestOptions(maximumObjects: 1, maximumVisibleCues: 0);
        var result = Assert.IsType<InvalidObservation>(
            ObservationValidator.Validate(Encoding.UTF8.GetBytes(ValidJson), options));

        Assert.Equal(2, result.Failure.Issues.Count(
            issue => issue.Code == ObservationValidationErrorCode.TooManyItems));
    }

    [Fact]
    public void Multiple_people_are_countable_but_person_specific_identity_is_indeterminate()
    {
        var countOnly = Mutate(root =>
        {
            root["people_count"]!["value"] = 2;
            root["visible_cues"] = new JsonArray();
        });
        Assert.IsType<ValidatedObservation>(ObservationValidator.Validate(Encoding.UTF8.GetBytes(countOnly)));

        var ambiguousCue = Mutate(root => root["people_count"]!["value"] = 2);
        AssertIssue(ambiguousCue, ObservationValidationErrorCode.InconsistentValue);
    }

    [Theory]
    [InlineData("the visible person is recognized as Alice", ObservationValidationErrorCode.IdentityClaim)]
    [InlineData("the person appears distracted by the phone", ObservationValidationErrorCode.BehavioralJudgment)]
    [InlineData("the person is working on the goal", ObservationValidationErrorCode.BehavioralJudgment)]
    [InlineData("the posture matches a deviation", ObservationValidationErrorCode.BehavioralJudgment)]
    public void Identity_claims_and_behavioral_judgments_are_rejected(
        string description,
        ObservationValidationErrorCode expected)
    {
        AssertIssue(Mutate(root => FirstCue(root)["description"] = description), expected);
    }

    [Fact]
    public void Object_labels_and_all_limitation_fields_are_also_neutral()
    {
        AssertIssue(Mutate(root => root["objects"] = new JsonArray("distracted")),
            ObservationValidationErrorCode.BehavioralJudgment);
        AssertIssue(Mutate(root =>
                root["image_quality"]!["limitations"] = new JsonArray("identity recognized")),
            ObservationValidationErrorCode.IdentityClaim);
        AssertIssue(Mutate(root =>
                root["people_count"]!["limitations"] = new JsonArray("person is off task")),
            ObservationValidationErrorCode.BehavioralJudgment);
    }

    [Fact]
    public void Unknown_identity_and_judgment_fields_are_rejected_even_with_neutral_values()
    {
        AssertIssue(Mutate(root => root["identity"] = "unknown"),
            ObservationValidationErrorCode.UnknownField);
        AssertIssue(Mutate(root => root["goal_alignment"] = "unknown"),
            ObservationValidationErrorCode.UnknownField);
        AssertIssue(Mutate(root => FirstCue(root)["deviation"] = false),
            ObservationValidationErrorCode.UnknownField);
    }

    private static InvalidObservation Invalid(string json) =>
        Assert.IsType<InvalidObservation>(ObservationValidator.Validate(Encoding.UTF8.GetBytes(json)));

    private static void AssertIssue(string json, ObservationValidationErrorCode code) =>
        Assert.Contains(Invalid(json).Failure.Issues, issue => issue.Code == code);

    private static string Mutate(Action<JsonObject> mutation)
    {
        var root = JsonNode.Parse(ValidJson)!.AsObject();
        mutation(root);
        return root.ToJsonString();
    }

    private static JsonObject FirstCue(JsonObject root) =>
        root["visible_cues"]!.AsArray()[0]!.AsObject();

    private static JsonArray ArrayOf(string value, int count) =>
        new(Enumerable.Repeat(value, count).Select(item => JsonValue.Create(item)).ToArray());

    private static JsonArray ArrayOf(JsonObject value, int count) =>
        new(Enumerable.Range(0, count).Select(_ => value.DeepClone()).ToArray());

    private const string ValidJson = """
        {
          "schema_version": 1,
          "image_quality": {
            "value": "adequate",
            "limitations": []
          },
          "people_count": {
            "status": "counted",
            "value": 1,
            "support": "direct",
            "limitations": []
          },
          "objects": ["phone", "laptop"],
          "visible_cues": [
            {
              "subject": "visible_person",
              "kind": "gaze",
              "state": "observed",
              "support": "partial",
              "description": "head oriented toward phone",
              "visual_basis": "head and phone are oriented toward each other",
              "limitations": ["hands are partly occluded"]
            }
          ]
        }
        """;
}
