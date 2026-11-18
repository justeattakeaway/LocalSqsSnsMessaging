// Translated from behavioral/sns/check_get_endpoint_attributes_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.get-endpoint-attributes-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetEndpointAttributesNotFound
{
    /// <summary>
    /// GetEndpointAttributes on a well-formed but nonexistent endpoint under a real platform
    /// application raises NotFound with "Endpoint does not exist".
    /// </summary>
    [Test]
    [DisplayName("sns.get-endpoint-attributes-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;

        // BAIDU accepts arbitrary key pairs (GCM/ADM reject fake credentials), and a real
        // endpoint cannot be created without a live Baidu credential, so only the app is real.
        var appArn = (await sns.CreatePlatformApplicationAsync(new CreatePlatformApplicationRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-platapp-"),
            Platform = "BAIDU",
            Attributes = new()
            {
                ["PlatformPrincipal"] = FidelityContext.UniqueName("baidu-api-key-"),
                ["PlatformCredential"] = FidelityContext.UniqueName("baidu-secret-key-"),
            },
        })).PlatformApplicationArn;
        ctx.OnCleanup(() => sns.DeletePlatformApplicationAsync(new DeletePlatformApplicationRequest { PlatformApplicationArn = appArn }));

        var control = (await sns.GetPlatformApplicationAttributesAsync(new GetPlatformApplicationAttributesRequest { PlatformApplicationArn = appArn })).Attributes ?? [];
        Ensure(control.GetValueOrDefault("Enabled") is "true" or "false",
            $"positive control: expected a real platform application to be readable, got {Py.Repr(control)}");

        var ghost = $"{ReplaceFirst(appArn, ":app/", ":endpoint/")}/{Guid.NewGuid()}";
        var (code, message) = await Error(() => sns.GetEndpointAttributesAsync(new GetEndpointAttributesRequest { EndpointArn = ghost }));
        Ensure(code == "NotFound", $"GetEndpointAttributes: expected NotFound, got {code}");
        const string expected = "Endpoint does not exist";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
    }

    private static string ReplaceFirst(string s, string oldValue, string newValue)
    {
        var i = s.IndexOf(oldValue, StringComparison.Ordinal);
        return i < 0 ? s : string.Concat(s.AsSpan(0, i), newValue, s.AsSpan(i + oldValue.Length));
    }

    /// <summary>
    /// Call fn(); return (code, message) of the ClientError it raises. An unexpected success
    /// raises, so a target that accepts what AWS refuses never reads as a code mismatch.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"expected the call to be refused, but it succeeded and returned {result}")
            : (error.Code, error.Message);
    }
}
