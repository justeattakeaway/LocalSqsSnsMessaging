#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable warnings - internal POCOs use nullable properties but values are set at runtime

using System.Text.Json;
using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

// Event bus resource policies: PutPermission, RemovePermission, and their enforcement on PutEvents.
internal sealed partial class InternalEventBridgeClient
{
    /// <summary>The only action PutPermission grants.</summary>
    private const string PutEventsAction = "events:PutEvents";

    /// <summary>
    /// Adds a statement to the bus's policy, replacing the statement with the same id, or (with
    /// <c>Policy</c>) replaces the whole policy.
    /// </summary>
    public Task<PutPermissionResponse> PutPermissionAsync(PutPermissionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Policy is not null)
        {
            if (request.Action is not null || request.Principal is not null || request.StatementId is not null || request.Condition is not null)
            {
                throw new InternalValidationException("Action, Principal, StatementId and Condition could not be combined with Policy parameter.");
            }
        }
        else
        {
            RequireParameter(request.Action, "Action");
            RequireParameter(request.Principal, "Principal");
            RequireParameter(request.StatementId, "StatementId");
            if (!string.Equals(request.Action, PutEventsAction, StringComparison.Ordinal))
            {
                throw new InternalValidationException("Provided value in parameter 'action' is not supported.");
            }
        }

        var eventBus = ResolveBus(request.EventBusName);
        lock (eventBus)
        {
            eventBus.Policy = request.Policy ?? WithStatement(eventBus.Policy, PermissionStatement(request, eventBus.Arn));
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "PutPermission", eventBus.Arn);
        return Task.FromResult(new PutPermissionResponse().SetCommonProperties());
    }

    /// <summary>Removes one statement from the bus's policy, or (with <c>RemoveAllPermissions</c>) the whole policy.</summary>
    public Task<RemovePermissionResponse> RemovePermissionAsync(RemovePermissionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var removeAll = request.RemoveAllPermissions == true;
        if (removeAll && request.StatementId is not null)
        {
            throw new InternalValidationException("StatementId could not be combined with All parameter.");
        }
        if (!removeAll)
        {
            RequireParameter(request.StatementId, "StatementId");
        }

        var eventBus = ResolveBus(request.EventBusName);
        lock (eventBus)
        {
            eventBus.Policy = removeAll ? null : WithoutStatement(eventBus.Policy, request.StatementId);
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "RemovePermission", eventBus.Arn);
        return Task.FromResult(new RemovePermissionResponse().SetCommonProperties());
    }

    /// <summary>
    /// Refuses the whole PutEvents request when the policy of a bus it puts to explicitly denies
    /// <c>events:PutEvents</c> to the caller for any of its entries; no entry is then delivered.
    /// </summary>
    /// <remarks>
    /// As for SQS queue policies (<see cref="SqsQueuePolicy"/>), only explicit Deny statements are
    /// enforced: a bus with no policy, or one that only allows, accepts every event as before. The
    /// library does not see who sends an EventBridge request, so every request acts for the bus's
    /// account (its root user), as an unsigned SQS request does, and has no <c>aws:SourceIp</c>.
    /// Each entry is evaluated with its own <c>events:source</c> and <c>events:detail-type</c>.
    /// </remarks>
    private void AuthorizePutEvents(List<PutEventsRequestEntry>? entries)
    {
        var callerArn = $"arn:aws:iam::{_bus.CurrentAccountId}:root";
        foreach (var entry in entries ?? [])
        {
            if (!_resources.EventBuses.TryGetValue(ExtractBusName(entry.EventBusName), out var eventBus)
                || eventBus.Policy is not { } policy)
            {
                continue;
            }

            var context = new SqsQueuePolicy.RequestContext("PutEvents", eventBus.Arn, _bus.CurrentAccountId, callerArn, SourceIp: null, SecureTransport: true)
            {
                ActionPrefix = "events",
                ServiceKeys = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["events:source"] = entry.Source,
                    ["events:detail-type"] = entry.DetailType
                }
            };

            if (SqsQueuePolicy.DeniesExplicitly(policy, context))
            {
                throw new AwsServiceException(
                    $"User: {callerArn} is not authorized to perform: {PutEventsAction} on resource: {eventBus.Arn} " +
                    "with an explicit deny in a resource-based policy")
                {
                    ErrorCode = "AccessDeniedException"
                };
            }
        }
    }

    private static void RequireParameter(string? value, string name)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new InternalValidationException($"Parameter(s) {name} must be specified.");
        }
    }

    /// <summary>
    /// The IAM statement a PutPermission grant stands for: <c>"*"</c> or an account id as the
    /// principal, and its <c>Condition</c> as an IAM condition block.
    /// </summary>
    private static JsonObject PermissionStatement(PutPermissionRequest request, string busArn)
    {
        var statement = new JsonObject
        {
            ["Sid"] = request.StatementId,
            ["Effect"] = "Allow",
            ["Principal"] = string.Equals(request.Principal, "*", StringComparison.Ordinal)
                ? JsonValue.Create("*")
                : new JsonObject { ["AWS"] = request.Principal },
            ["Action"] = request.Action,
            ["Resource"] = busArn
        };

        if (request.Condition is { } condition)
        {
            statement["Condition"] = new JsonObject
            {
                [condition.Type ?? ""] = new JsonObject { [condition.Key ?? ""] = condition.Value }
            };
        }

        return statement;
    }

    /// <summary><paramref name="policy"/> with <paramref name="statement"/> added, replacing a statement with the same <c>Sid</c>.</summary>
    private static string WithStatement(string? policy, JsonObject statement)
    {
        var document = ParsePolicy(policy) ?? new JsonObject { ["Version"] = "2012-10-17", ["Statement"] = new JsonArray() };
        var statements = Statements(document);
        var sid = statement["Sid"]?.GetValue<string>();
        var existing = statements.FirstOrDefault(s => s is JsonObject o && string.Equals(StatementId(o), sid, StringComparison.Ordinal));
        if (existing is not null)
        {
            statements[statements.IndexOf(existing)] = statement;
        }
        else
        {
            statements.Add((JsonNode)statement);
        }

        return document.ToJsonString();
    }

    /// <summary>
    /// <paramref name="policy"/> without the statement <paramref name="statementId"/>, or
    /// <see langword="null"/> when no statement is left.
    /// </summary>
    private static string? WithoutStatement(string? policy, string? statementId)
    {
        var document = ParsePolicy(policy);
        var statements = document is null ? null : Statements(document);
        var existing = statements?.FirstOrDefault(s => s is JsonObject o && string.Equals(StatementId(o), statementId, StringComparison.Ordinal));
        if (existing is null)
        {
            throw new InternalResourceNotFoundException("Statement with the provided id does not exist.");
        }

        statements!.Remove(existing);
        return statements.Count == 0 ? null : document!.ToJsonString();
    }

    private static JsonObject? ParsePolicy(string? policy)
    {
        if (string.IsNullOrEmpty(policy))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(policy) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The policy's statements as a list, converting a single statement object into one.</summary>
    private static JsonArray Statements(JsonObject document)
    {
        switch (document["Statement"])
        {
            case JsonArray array:
                return array;
            case JsonObject single:
                document.Remove("Statement");
                var wrapped = new JsonArray(single);
                document["Statement"] = wrapped;
                return wrapped;
            default:
                var empty = new JsonArray();
                document["Statement"] = empty;
                return empty;
        }
    }

    private static string? StatementId(JsonObject statement) =>
        statement["Sid"] is JsonValue sid && sid.TryGetValue<string>(out var value) ? value : null;
}
