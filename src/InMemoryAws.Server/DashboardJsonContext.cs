using System.Text.Json.Serialization;
using static InMemoryAws.Server.DashboardApi;

namespace InMemoryAws.Server;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(CloudState))]
[JsonSerializable(typeof(QueueMessages))]
[JsonSerializable(typeof(PublishTopicRequest))]
[JsonSerializable(typeof(PublishTopicResponse))]
[JsonSerializable(typeof(RedriveResponse))]
internal sealed partial class DashboardJsonContext : JsonSerializerContext;
