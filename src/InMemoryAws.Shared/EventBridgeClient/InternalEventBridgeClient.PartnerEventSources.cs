#pragma warning disable CA1822 // The generated dispatcher calls every operation on a client instance.

using System.Text.RegularExpressions;
using InMemoryAws.EventBridge.Model;

namespace InMemoryAws;

// Partner event sources: the customer side (ActivateEventSource, DeactivateEventSource,
// DescribeEventSource, ListEventSources) and the SaaS partner side (CreatePartnerEventSource,
// DeletePartnerEventSource, DescribePartnerEventSource, ListPartnerEventSources,
// ListPartnerEventSourceAccounts, PutPartnerEvents).
//
// Only a registered SaaS partner can create a partner event source, and the cloud's account never is
// one: every partner-side operation is refused for the source's namespace, as AWS refuses it for an
// account outside it, so no source is ever shared with the account and the customer side finds none.
internal sealed partial class InternalEventBridgeClient
{
    private const string EventSourceNamePattern = @"aws\.partner(/[\.\-_A-Za-z0-9]+){2,}";
    private const string EventSourceNamePrefixPattern = @"[/\.\-_A-Za-z0-9]+";
    private const string PartnerEventSourceNamePrefixPattern = @"aws\.partner/[\.\-_A-Za-z0-9]+/[/\.\-_A-Za-z0-9]*";
    private const string AccountIdPattern = @"\d{12}";

    public Task<ActivateEventSourceResponse> ActivateEventSourceAsync(ActivateEventSourceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateEventSourceName(request.Name, "name");
        throw EventSourceOrBusNotFound(request.Name!);
    }

    public Task<DeactivateEventSourceResponse> DeactivateEventSourceAsync(DeactivateEventSourceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateEventSourceName(request.Name, "name");
        throw EventSourceOrBusNotFound(request.Name!);
    }

    public Task<DescribeEventSourceResponse> DescribeEventSourceAsync(DescribeEventSourceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateEventSourceName(request.Name, "name");
        throw new InternalResourceNotFoundException($"Event source {request.Name} does not exist.");
    }

    public Task<ListEventSourcesResponse> ListEventSourcesAsync(ListEventSourcesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var violations = new List<string>();
        CheckPattern(violations, request.NamePrefix, "namePrefix", EventSourceNamePrefixPattern, maxLength: 256);
        ThrowIfViolations(violations);
        _ = EventBridgePage.Take(Array.Empty<EventSource>(), request.Limit, nextToken: null, out _);

        // No partner ever shares a source with the account.
        return Task.FromResult(new ListEventSourcesResponse { EventSources = [] }.SetCommonProperties());
    }

    public Task<CreatePartnerEventSourceResponse> CreatePartnerEventSourceAsync(CreatePartnerEventSourceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var violations = new List<string>();
        CheckPattern(violations, request.Name, "name", EventSourceNamePattern, maxLength: 256);
        CheckPattern(violations, request.Account, "account", AccountIdPattern, maxLength: 12, minLength: 12);
        ThrowIfViolations(violations);
        throw NotPartnerNamespace(request.Name!);
    }

    public Task<DeletePartnerEventSourceResponse> DeletePartnerEventSourceAsync(DeletePartnerEventSourceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var violations = new List<string>();
        CheckPattern(violations, request.Name, "name", EventSourceNamePattern, maxLength: 256);
        CheckPattern(violations, request.Account, "account", AccountIdPattern, maxLength: 12, minLength: 12);
        ThrowIfViolations(violations);
        throw NotPartnerNamespace(request.Name!);
    }

    public Task<DescribePartnerEventSourceResponse> DescribePartnerEventSourceAsync(DescribePartnerEventSourceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateEventSourceName(request.Name, "name");
        throw NotPartnerNamespace(request.Name!);
    }

    public Task<ListPartnerEventSourcesResponse> ListPartnerEventSourcesAsync(ListPartnerEventSourcesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var violations = new List<string>();
        CheckPattern(violations, request.NamePrefix, "namePrefix", PartnerEventSourceNamePrefixPattern, maxLength: 256);
        ThrowIfViolations(violations);
        throw NotPartnerNamespace(request.NamePrefix!);
    }

    public Task<ListPartnerEventSourceAccountsResponse> ListPartnerEventSourceAccountsAsync(ListPartnerEventSourceAccountsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateEventSourceName(request.EventSourceName, "eventSourceName");
        throw NotPartnerNamespace(request.EventSourceName!);
    }

    public Task<PutPartnerEventsResponse> PutPartnerEventsAsync(PutPartnerEventsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entries = request.Entries ?? [];
        if (entries.Count is 0 or > 20)
        {
            var constraint = entries.Count == 0
                ? "Member must have length greater than or equal to 1"
                : "Member must have length less than or equal to 20";
            ThrowIfViolations([Violation($"[{string.Join(", ", entries.Select(_ => "PutPartnerEventsRequestEntry"))}]", "entries", constraint)]);
        }

        // The account is no partner's, so no source accepts its events: each entry fails in place.
        var results = entries.Select(_ => new PutPartnerEventsResultEntry
        {
            ErrorCode = "InvalidArgument",
            ErrorMessage = "Parameter Source is not valid. Reason: Caller AWS account is not whitelisted for the source."
        }).ToList();
        return Task.FromResult(new PutPartnerEventsResponse
        {
            FailedEntryCount = results.Count,
            Entries = results
        }.SetCommonProperties());
    }

    private static void ValidateEventSourceName(string? name, string member)
    {
        var violations = new List<string>();
        CheckPattern(violations, name, member, EventSourceNamePattern, maxLength: 256);
        ThrowIfViolations(violations);
    }

    /// <summary>A string member's length and pattern constraints, worded as EventBridge's request validation words them.</summary>
    private static void CheckPattern(List<string> violations, string? value, string member, string pattern, int maxLength, int minLength = 1)
    {
        if (value is null)
        {
            violations.Add(NullViolation(member));
            return;
        }

        if (value.Length < minLength)
        {
            violations.Add(Violation(value, member, $"Member must have length greater than or equal to {minLength}"));
        }
        else if (value.Length > maxLength)
        {
            violations.Add(Violation(value, member, $"Member must have length less than or equal to {maxLength}"));
        }

        if (!Regex.IsMatch(value, $"^(?:{pattern})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            violations.Add(Violation(value, member, $"Member must satisfy regular expression pattern: {pattern}"));
        }
    }

    /// <summary>
    /// The refusal of a partner-side operation for a caller outside the partner namespace: the
    /// source name's first two segments, <c>aws.partner/&lt;partner&gt;</c>.
    /// </summary>
    private static InternalValidationException NotPartnerNamespace(string name)
    {
        var segments = name.Split('/');
        var partnerNamespace = string.Join("/", segments.Take(2));
        return new InternalValidationException($"Caller is not whitelisted for the event source partner namespace {partnerNamespace}");
    }

    private static InternalResourceNotFoundException EventSourceOrBusNotFound(string name) =>
        new($"Event source {name} or event bus {name} does not exist.");
}
