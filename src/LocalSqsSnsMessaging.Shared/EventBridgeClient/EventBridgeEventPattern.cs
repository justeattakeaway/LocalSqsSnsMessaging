using System.Text.Json.Nodes;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Validates EventBridge event patterns and matches events against them, on top of the Event Ruler
/// grammar (<see cref="EventRulerGrammar"/>) and matcher (<see cref="JsonPatternMatcher"/>) SNS filter
/// policies share.
/// See https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-event-patterns.html
/// </summary>
internal static class EventBridgeEventPattern
{
    /// <summary>The longest event pattern a rule may have, in characters as sent.</summary>
    public const int MaxRulePatternLength = 2048;

    /// <summary>
    /// Validates the event pattern <c>PutRule</c> was given, throwing
    /// <see cref="InternalInvalidEventPatternException"/> with EventBridge's message when it is too
    /// long or malformed. A rule without a pattern (a scheduled rule) is valid.
    /// </summary>
    /// <remarks>
    /// PutRule reads a <c>$or</c> that is not a valid or-relationship as an ordinary field name, so
    /// such a pattern is refused (or accepted) on what follows the <c>$or</c>, where
    /// <see cref="ValidateTestPattern"/> refuses the or-relationship itself.
    /// </remarks>
    public static void ValidateRulePattern(string? ruleName, string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return;
        }

        if (pattern!.Length > MaxRulePatternLength)
        {
            throw new InternalInvalidEventPatternException(
                $"Parameter EventPattern for rule {ruleName} exceeds limit of {MaxRulePatternLength}.");
        }

        Validate(pattern, orFallback: true);
    }

    /// <summary>
    /// Validates an archive's event pattern, throwing <see cref="InternalInvalidEventPatternException"/>
    /// with EventBridge's message when it is malformed. The pattern becomes the archive's managed
    /// rule's, so it is read as a rule's is. An archive without a pattern archives every event.
    /// </summary>
    public static void ValidateArchivePattern(string? pattern)
    {
        if (!string.IsNullOrEmpty(pattern))
        {
            Validate(pattern!, orFallback: true);
        }
    }

    /// <summary>
    /// Validates the event pattern <c>TestEventPattern</c> was given, throwing
    /// <see cref="InternalInvalidEventPatternException"/> with EventBridge's message when it is malformed.
    /// </summary>
    public static void ValidateTestPattern(string? pattern) => Validate(pattern ?? string.Empty, orFallback: false);

    private static void Validate(string pattern, bool orFallback)
    {
        try
        {
            EventRulerJsonReader.Read(pattern, quoteSource: true);
            try
            {
                EventRulerGrammar.Compile(pattern);
            }
            catch (EventRulerPatternException ex) when (orFallback && ex.IsOrRelationship)
            {
                EventRulerGrammar.Compile(pattern, orAsFieldName: true);
            }
        }
        catch (EventRulerPatternException ex)
        {
            throw new InternalInvalidEventPatternException($"Event pattern is not valid. Reason: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns true if the given event matches the pattern. A pattern that is missing or malformed
    /// matches nothing.
    /// </summary>
    public static bool Matches(string? patternJson, JsonNode? @event)
    {
        // A rule with no event pattern (e.g. a scheduled rule) never matches PutEvents traffic.
        if (string.IsNullOrWhiteSpace(patternJson))
        {
            return false;
        }

        JsonNode? pattern;
        try
        {
            pattern = EventRulerJsonReader.Read(patternJson!, quoteSource: true);
        }
        catch (EventRulerPatternException)
        {
            return false;
        }

        return pattern is JsonObject obj && JsonPatternMatcher.Matches(obj, @event as JsonObject);
    }
}
