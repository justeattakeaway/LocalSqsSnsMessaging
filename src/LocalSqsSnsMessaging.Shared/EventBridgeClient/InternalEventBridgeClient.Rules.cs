#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable warnings - internal POCOs use nullable properties but values are set at runtime

using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

// Rules: PutRule, DescribeRule, DeleteRule, EnableRule, DisableRule and ListRules.
internal sealed partial class InternalEventBridgeClient
{
    public Task<PutRuleResponse> PutRuleAsync(PutRuleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrEmpty(request.EventPattern) && !EventBridgeEventPattern.IsValid(request.EventPattern, out var error))
        {
            throw new InternalInvalidEventPatternException($"Event pattern is not valid. Reason: {error}");
        }

        var eventBus = ResolveBus(request.EventBusName);
        var arn = RuleArn(eventBus.Name, request.Name);

        eventBus.Rules.AddOrUpdate(request.Name,
            _ => new RuleResource
            {
                Name = request.Name,
                Arn = arn,
                EventBusName = eventBus.Name,
                EventPattern = request.EventPattern,
                ScheduleExpression = request.ScheduleExpression,
                State = request.State ?? "ENABLED",
                Description = request.Description,
                RoleArn = request.RoleArn
            },
            (_, existing) =>
            {
                existing.EventPattern = request.EventPattern;
                existing.ScheduleExpression = request.ScheduleExpression;
                existing.State = request.State ?? "ENABLED";
                existing.Description = request.Description;
                existing.RoleArn = request.RoleArn;
                return existing;
            });

        _bus.RecordOperation(AwsServiceName.EventBridge, "PutRule", arn);
        return Task.FromResult(new PutRuleResponse { RuleArn = arn }.SetCommonProperties());
    }

    public Task<DeleteRuleResponse> DeleteRuleAsync(DeleteRuleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var eventBus = ResolveBus(request.EventBusName);
        eventBus.Rules.TryRemove(request.Name, out _);

        _bus.RecordOperation(AwsServiceName.EventBridge, "DeleteRule");
        return Task.FromResult(new DeleteRuleResponse().SetCommonProperties());
    }

    public Task<DescribeRuleResponse> DescribeRuleAsync(DescribeRuleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rule = GetRule(request.EventBusName, request.Name);

        _bus.RecordOperation(AwsServiceName.EventBridge, "DescribeRule", rule.Arn);
        return Task.FromResult(new DescribeRuleResponse
        {
            Name = rule.Name,
            Arn = rule.Arn,
            EventPattern = rule.EventPattern,
            ScheduleExpression = rule.ScheduleExpression,
            State = rule.State,
            Description = rule.Description,
            RoleArn = rule.RoleArn,
            EventBusName = rule.EventBusName
        }.SetCommonProperties());
    }

    public Task<EnableRuleResponse> EnableRuleAsync(EnableRuleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        GetRule(request.EventBusName, request.Name).State = "ENABLED";
        _bus.RecordOperation(AwsServiceName.EventBridge, "EnableRule");
        return Task.FromResult(new EnableRuleResponse().SetCommonProperties());
    }

    public Task<DisableRuleResponse> DisableRuleAsync(DisableRuleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        GetRule(request.EventBusName, request.Name).State = "DISABLED";
        _bus.RecordOperation(AwsServiceName.EventBridge, "DisableRule");
        return Task.FromResult(new DisableRuleResponse().SetCommonProperties());
    }

    public Task<ListRulesResponse> ListRulesAsync(ListRulesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var eventBus = ResolveBus(request.EventBusName);
        var rules = eventBus.Rules.Values
            .Where(r => string.IsNullOrEmpty(request.NamePrefix) || r.Name.StartsWith(request.NamePrefix, StringComparison.Ordinal))
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => new Rule
            {
                Name = r.Name,
                Arn = r.Arn,
                EventPattern = r.EventPattern,
                State = r.State,
                Description = r.Description,
                ScheduleExpression = r.ScheduleExpression,
                RoleArn = r.RoleArn,
                EventBusName = r.EventBusName
            })
            .ToList();

        _bus.RecordOperation(AwsServiceName.EventBridge, "ListRules");
        return Task.FromResult(new ListRulesResponse { Rules = rules }.SetCommonProperties());
    }
}
