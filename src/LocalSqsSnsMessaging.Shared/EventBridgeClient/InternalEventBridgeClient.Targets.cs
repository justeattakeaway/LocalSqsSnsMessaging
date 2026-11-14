#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable warnings - internal POCOs use nullable properties but values are set at runtime

using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

// Targets: PutTargets, RemoveTargets, ListTargetsByRule and ListRuleNamesByTarget.
internal sealed partial class InternalEventBridgeClient
{
    public Task<PutTargetsResponse> PutTargetsAsync(PutTargetsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Every refusal is for the whole request (none is a FailedEntries row), and leaves the rule as it was.
        var targets = request.Targets ?? [];
        EventBridgeTargetValidation.Validate(targets);
        var rule = GetRule(request.EventBusName, request.Rule);
        EventBridgeTargetInput.Validate(targets);
        rule.PutTargets(targets);

        _bus.RecordOperation(AwsServiceName.EventBridge, "PutTargets", rule.Arn);
        return Task.FromResult(new PutTargetsResponse
        {
            FailedEntryCount = 0,
            FailedEntries = []
        }.SetCommonProperties());
    }

    public Task<RemoveTargetsResponse> RemoveTargetsAsync(RemoveTargetsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Ids the rule does not hold are ignored, not reported as failed entries.
        var rule = GetRule(request.EventBusName, request.Rule);
        rule.RemoveTargets(request.Ids ?? []);

        _bus.RecordOperation(AwsServiceName.EventBridge, "RemoveTargets", rule.Arn);
        return Task.FromResult(new RemoveTargetsResponse
        {
            FailedEntryCount = 0,
            FailedEntries = []
        }.SetCommonProperties());
    }

    public Task<ListTargetsByRuleResponse> ListTargetsByRuleAsync(ListTargetsByRuleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rule = GetRule(request.EventBusName, request.Rule);
        var targets = EventBridgePage.Take(rule.Targets, request.Limit, request.NextToken, out var nextToken);

        _bus.RecordOperation(AwsServiceName.EventBridge, "ListTargetsByRule", rule.Arn);
        return Task.FromResult(new ListTargetsByRuleResponse { Targets = targets, NextToken = nextToken }.SetCommonProperties());
    }

    public Task<ListRuleNamesByTargetResponse> ListRuleNamesByTargetAsync(ListRuleNamesByTargetRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var eventBus = ResolveBus(request.EventBusName);
        var matching = eventBus.Rules.Values
            .Where(r => r.Targets.Any(t => string.Equals(t.Arn, request.TargetArn, StringComparison.Ordinal)))
            .Select(r => r.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        var names = EventBridgePage.Take(matching, request.Limit, request.NextToken, out var nextToken);

        _bus.RecordOperation(AwsServiceName.EventBridge, "ListRuleNamesByTarget");
        return Task.FromResult(new ListRuleNamesByTargetResponse { RuleNames = names, NextToken = nextToken }.SetCommonProperties());
    }
}
