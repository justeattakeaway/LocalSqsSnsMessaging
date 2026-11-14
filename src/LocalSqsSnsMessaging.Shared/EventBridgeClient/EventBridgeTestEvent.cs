using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Reads the event <c>TestEventPattern</c> is given, which must be a whole EventBridge event
/// envelope, refusing it as EventBridge does with a <c>ValidationException</c>.
/// </summary>
internal static class EventBridgeTestEvent
{
    private const string NotValid = "Parameter Event is not valid.";

    /// <summary>The envelope fields an event must have; <c>resources</c> and <c>detail</c> may be left out.</summary>
    private static readonly string[] RequiredFields = ["id", "detail-type", "source", "account", "time", "region"];

    /// <summary>
    /// Parses <paramref name="eventJson"/>, throwing a <c>ValidationException</c> when it is not a JSON
    /// object, lacks a required envelope field, or has a <c>time</c> that is not a timestamp.
    /// </summary>
    public static JsonObject Parse(string? eventJson)
    {
        JsonObject? @event;
        try
        {
            @event = JsonNode.Parse(eventJson ?? string.Empty) as JsonObject;
        }
        catch (JsonException)
        {
            @event = null;
        }

        if (@event is null || RequiredFields.Any(field => !@event.ContainsKey(field)))
        {
            throw Invalid(NotValid);
        }

        if (@event["time"] is not JsonValue time ||
            time.GetValueKind() != JsonValueKind.String ||
            !DateTimeOffset.TryParse(time.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
        {
            throw Invalid($"{NotValid} Reason: Invalid time.");
        }

        return @event;
    }

    private static AwsServiceException Invalid(string message) => new(message) { ErrorCode = "ValidationException" };
}
