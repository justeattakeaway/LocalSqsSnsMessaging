// Translated from behavioral/events/check_list_connections_paginates_with_limit_and_next_token.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.list-connections-paginates-with-limit-and-next-token</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListConnectionsPaginatesWithLimitAndNextToken
{
    private const int Connections = 3;
    private const int MaxPages = 10;

    /// <summary>
    /// ListConnections with Limit=1 over three connections returns one connection per page and
    /// together each connection exactly once; Limit above the maximum of 100 and an unissued
    /// NextToken are both refused.
    /// </summary>
    [Test]
    [DisplayName("events.list-connections-paginates-with-limit-and-next-token")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var prefix = FidelityContext.UniqueName("sim2real-behav-conn-");
        var names = Enumerable.Range(0, Connections).Select(i => $"{prefix}-{i}").ToHashSet(StringComparer.Ordinal);
        foreach (var name in Sorted(names))
        {
            await events.CreateConnectionAsync(new CreateConnectionRequest
            {
                Name = name,
                AuthorizationType = ConnectionAuthorizationType.API_KEY,
                AuthParameters = new CreateConnectionAuthRequestParameters
                {
                    ApiKeyAuthParameters = new CreateConnectionApiKeyAuthRequestParameters { ApiKeyName = "sim2real-key", ApiKeyValue = "sim2real-value" },
                },
            });
            ctx.OnCleanup(() => events.DeleteConnectionAsync(new DeleteConnectionRequest { Name = name }));
        }

        var pages = new List<List<string>>();
        string? token = null;
        while (true)
        {
            var request = new ListConnectionsRequest { NamePrefix = prefix, Limit = 1 };
            if (!string.IsNullOrEmpty(token))
            {
                request.NextToken = token;
            }

            var resp = await events.ListConnectionsAsync(request);
            var page = (resp.Connections ?? []).Select(c => c.Name).ToList();
            Ensure(page.Count <= 1, $"Limit=1 returned a page of {page.Count} connections: {Repr(page)}");
            pages.Add(page);
            token = resp.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                break;
            }

            Ensure(
                pages.Count < MaxPages,
                $"ListConnections kept returning a NextToken after {pages.Count} pages for " +
                $"{Connections} connections: {Repr(pages)} -- the token does not advance");
        }

        var listed = pages.SelectMany(p => p).ToList();
        Ensure(
            Sorted(listed).SequenceEqual(Sorted(names)),
            $"the pages together must return each of the {Connections} connections exactly once; " +
            $"got {Repr(listed)}");
        Ensure(
            pages.Count(p => p.Count > 0) == Connections,
            $"with Limit=1 the {Connections} connections must arrive on {Connections} separate " +
            $"pages, got {Repr(pages)}");

        var overMaximum = 101; // a variable: the SDK's analyzer refuses the literal
        var (code, message) = await Error(() => events.ListConnectionsAsync(new ListConnectionsRequest { NamePrefix = prefix, Limit = overMaximum }));
        Ensure(
            (code, message) == (
                "ValidationException",
                "1 validation error detected: Value '101' at 'limit' failed to satisfy constraint: " +
                "Member must have value less than or equal to 100"),
            $"Limit=101 must be refused, got '{code}' '{message}'");

        (code, message) = await Error(() => events.ListConnectionsAsync(new ListConnectionsRequest { NamePrefix = prefix, NextToken = "c2ltMnJlYWw=" }));
        Ensure(
            (code, message) == ("ValidationException", "Parameter NextToken is not valid. Reason: The request is malformed."),
            $"a NextToken the service never issued must be refused, got '{code}' '{message}'");
    }

    private static List<string> Sorted(IEnumerable<string> items) => [.. items.Order(StringComparer.Ordinal)];

    private static string Repr(object? value) => Py.Repr(value);

    /// <summary>(code, message) of the ClientError <paramref name="fn"/> raises. An unexpected success raises too.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }
}
