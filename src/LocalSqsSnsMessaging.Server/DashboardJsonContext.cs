using System.Text.Json.Serialization;
using static LocalSqsSnsMessaging.Server.DashboardApi;

namespace LocalSqsSnsMessaging.Server;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(BusState))]
[JsonSerializable(typeof(QueueMessages))]
[JsonSerializable(typeof(PublishTopicRequest))]
[JsonSerializable(typeof(PublishTopicResponse))]
internal sealed partial class DashboardJsonContext : JsonSerializerContext;
