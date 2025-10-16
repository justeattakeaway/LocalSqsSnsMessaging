using System.Text.Json;

namespace LocalSqsSnsMessaging.Http.Serialization;

/// <summary>
/// Deserializes AWS JSON protocol requests to AWS SDK request objects.
/// </summary>
internal static class AwsJsonRequestDeserializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Deserializes a JSON string to the specified request type.
    /// </summary>
    public static T Deserialize<T>(string json) where T : class
    {
        if (string.IsNullOrEmpty(json))
        {
            // Return a default instance for empty requests
            return Activator.CreateInstance<T>();
        }

        var result = JsonSerializer.Deserialize<T>(json, Options);
        return result ?? throw new InvalidOperationException($"Failed to deserialize request to type {typeof(T).Name}");
    }
}
