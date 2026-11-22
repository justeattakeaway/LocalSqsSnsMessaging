// Translated from behavioral/s3/check_cors_max_age_absent_from_the_rule_is_not_invented_on_the_preflight.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-max-age-absent-from-the-rule-is-not-invented-on-the-preflight</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsMaxAgeAbsentFromTheRuleIsNotInventedOnThePreflight
{
    private const string Check = "cors-max-age-absent-from-the-rule-is-not-invented-on-the-preflight";
    private const string Site = "https://cache.example.com";
    private const string ObjectKey = "static/app.js";
    private const int CacheForS = 3000;
    private static readonly byte[] ObjectBody = "// a cross-origin script"u8.ToArray();

    /// <summary>
    /// A rule without MaxAgeSeconds leaves Access-Control-Max-Age off its preflight while the
    /// rest of the grant stands, where a rule that sets it answers that value on every matched request.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-max-age-absent-from-the-rule-is-not-invented-on-the-preflight")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var timed = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check, "sim2real-behav-age-");
        var untimed = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check, "sim2real-behav-age-");
        foreach (var bucket in new[] { timed, untimed })
        {
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = ObjectKey,
                InputStream = new MemoryStream(ObjectBody),
                ContentType = "application/javascript",
            });
        }

        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = timed,
            Configuration = new CORSConfiguration
            {
                Rules = [new CORSRule { AllowedOrigins = [Site], AllowedMethods = ["GET"], AllowedHeaders = ["content-type"], MaxAgeSeconds = CacheForS }],
            },
        });
        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = untimed,
            Configuration = new CORSConfiguration { Rules = [new CORSRule { AllowedOrigins = [Site], AllowedMethods = ["GET"], AllowedHeaders = ["content-type"] }] },
        });
        var cacheFor = CacheForS.ToString(CultureInfo.InvariantCulture);

        // The rule that sets the member is the legal neighbour of the one that leaves it out.
        var withMember = (await AwaitPreflight(ctx, S3Fidelity.ObjectUrl(ctx, timed, ObjectKey))).Headers;
        Ensure(
            withMember.GetValueOrDefault("access-control-max-age") == cacheFor,
            $"the rule carrying MaxAgeSeconds answered Access-Control-Max-Age {Py.Repr(withMember.GetValueOrDefault("access-control-max-age"))}, expected {Py.Repr(cacheFor)}");
        var withoutMember = (await AwaitPreflight(ctx, S3Fidelity.ObjectUrl(ctx, untimed, ObjectKey))).Headers;
        Ensure(
            withoutMember.GetValueOrDefault("access-control-allow-origin") == Site,
            $"the rule without MaxAgeSeconds answered Access-Control-Allow-Origin {Py.Repr(withoutMember.GetValueOrDefault("access-control-allow-origin"))}, expected {Py.Repr(Site)}");
        Ensure(
            withoutMember.GetValueOrDefault("access-control-allow-methods") == "GET",
            $"the rule without MaxAgeSeconds answered Access-Control-Allow-Methods {Py.Repr(withoutMember.GetValueOrDefault("access-control-allow-methods"))}, expected 'GET'");
        Ensure(
            !withoutMember.ContainsKey("access-control-max-age"),
            $"the rule without MaxAgeSeconds still answered Access-Control-Max-Age {Py.Repr(withoutMember.GetValueOrDefault("access-control-max-age"))}");

        var served = await S3Fidelity.AwaitAnswer(
            ctx,
            "GET",
            S3Fidelity.Presign(ctx, "GET", timed, ObjectKey, 600),
            new Dictionary<string, string> { ["Origin"] = Site },
            a => a.Headers.GetValueOrDefault("access-control-allow-origin") == Site,
            (a, _) => $"the GET answered {a.Status} with Allow-Origin {Py.Repr(a.Headers.GetValueOrDefault("access-control-allow-origin"))}, expected {Py.Repr(Site)}",
            share: 4);
        Ensure(served.Body.SequenceEqual(ObjectBody), $"the actual GET served {S3Fidelity.Head(served.Body)}, expected {S3Fidelity.Head(ObjectBody)}");

        // S3 answers the rule's Max-Age on an actual request as well, not only on the preflight.
        Ensure(
            served.Headers.GetValueOrDefault("access-control-max-age") == cacheFor,
            $"the actual GET under the rule carrying MaxAgeSeconds answered Access-Control-Max-Age {Py.Repr(served.Headers.GetValueOrDefault("access-control-max-age"))}, expected {Py.Repr(cacheFor)}");
    }

    /// <summary><c>_await_preflight(ctx, url)</c>: the GET preflight's answer once it is a 200.</summary>
    private static Task<(int Status, Dictionary<string, string> Headers, byte[] Body)> AwaitPreflight(FidelityContext ctx, string url) =>
        S3Fidelity.AwaitAnswer(
            ctx,
            "OPTIONS",
            url,
            new Dictionary<string, string> { ["Origin"] = Site, ["Access-Control-Request-Method"] = "GET" },
            a => a.Status == 200,
            (a, _) => $"the preflight was still answering {a.Status}, never 200: {S3Fidelity.Head(a.Body, 300)}",
            share: 4);
}
