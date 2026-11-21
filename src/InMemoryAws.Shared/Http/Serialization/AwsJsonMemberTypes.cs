using System.Text.Json;

namespace InMemoryAws.Http.Serialization;

/// <summary>
/// The member type checks of SQS's and EventBridge's generated JSON deserializers, worded as the
/// AWS service framework words its <c>SerializationException</c> for them.
/// </summary>
internal static class AwsJsonMemberTypes
{
    /// <summary>
    /// Whether a list member holds a list: <see langword="false"/> for <c>null</c>, which reads as
    /// absent; anything else that is not an array is refused.
    /// </summary>
    public static bool IsList(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => true,
        JsonValueKind.Null => false,
        _ => throw new InternalSerializationException("Unexpected field type"),
    };

    /// <summary>One string of a list of strings.</summary>
    public static string ReadListString(JsonElement item) => item.ValueKind switch
    {
        JsonValueKind.String => item.GetString()!,
        JsonValueKind.Null => null!,
        JsonValueKind.Number => throw new InternalSerializationException("NUMBER_VALUE cannot be converted to String"),
        JsonValueKind.Object => throw new InternalSerializationException("Start of structure or map found where not expected"),
        _ => throw new InternalSerializationException(string.Empty),
    };
}
