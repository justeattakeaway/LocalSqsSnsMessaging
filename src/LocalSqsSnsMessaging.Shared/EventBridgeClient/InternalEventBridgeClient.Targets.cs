#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable warnings - internal POCOs use nullable properties but values are set at runtime

using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

// Targets: PutTargets, RemoveTargets, ListTargetsByRule and ListRuleNamesByTarget.
internal sealed partial class InternalEventBridgeClient
{
    public Task<PutTargetsResponse> PutTargetsAsync(PutTargetsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rule = GetRule(request.EventBusName, request.Rule);
        EventBridgeTargetInput.Validate(request.Targets);
        lock (rule.Targets)
        {
            foreach (var target in request.Targets ?? [])
            {
                rule.Targets.RemoveAll(t => string.Equals(t.Id, target.Id, StringComparison.Ordinal));
                rule.Targets.Add(target);
            }
        }

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

        var rule = GetRule(request.EventBusName, request.Rule);
        lock (rule.Targets)
        {
            foreach (var id in request.Ids ?? [])
            {
                rule.Targets.RemoveAll(t => string.Equals(t.Id, id, StringComparison.Ordinal));
            }
        }

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
        List<Target> targets;
        lock (rule.Targets)
        {
            targets = [.. rule.Targets];
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "ListTargetsByRule", rule.Arn);
        return Task.FromResult(new ListTargetsByRuleResponse { Targets = targets }.SetCommonProperties());
    }

    public Task<ListRuleNamesByTargetResponse> ListRuleNamesByTargetAsync(ListRuleNamesByTargetRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var eventBus = ResolveBus(request.EventBusName);
        var names = eventBus.Rules.Values
            .Where(r =>
            {
                lock (r.Targets)
                {
                    return r.Targets.Any(t => string.Equals(t.Arn, request.TargetArn, StringComparison.Ordinal));
                }
            })
            .Select(r => r.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        _bus.RecordOperation(AwsServiceName.EventBridge, "ListRuleNamesByTarget");
        return Task.FromResult(new ListRuleNamesByTargetResponse { RuleNames = names }.SetCommonProperties());
    }
}
