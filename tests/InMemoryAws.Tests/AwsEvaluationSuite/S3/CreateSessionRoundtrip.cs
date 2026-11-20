// Translated from behavioral/s3/check_create_session_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.create-session-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateSessionRoundtrip
{
    /// <summary>
    /// CreateSession on a directory bucket returns full temporary credentials for both SessionMode
    /// values, with distinct access keys, and an object written afterwards reads back byte for byte
    /// through a separate GetObject.
    /// </summary>
    [Test]
    [DisplayName("s3.create-session-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.NewDirectoryBucket(ctx, s3);
        var rw = await S3Fidelity.CreateSession(s3, name);
        var ro = await S3Fidelity.CreateSession(s3, name, "ReadOnly");

        var rwCreds = rw.Credentials;
        var roCreds = ro.Credentials;
        foreach (var (label, creds) in new[] { ("ReadWrite", rwCreds), ("ReadOnly", roCreds) })
        {
            Ensure(
                creds is { AccessKeyId: not null, SecretAccessKey: not null, SessionToken: not null, Expiration: not null },
                $"{label} CreateSession response missing a credential field");
        }

        Ensure(
            roCreds.AccessKeyId != rwCreds.AccessKeyId,
            "CreateSession(SessionMode='ReadOnly') must mint a session distinct from the default " +
            "ReadWrite session, not echo the same credentials back");

        // The independent read: PutObject (mutation) followed by a separate GetObject call proves the
        // directory bucket is genuinely usable for session-authorized data-plane calls right after
        // minting -- not just a shape-valid CreateSession response with credentials nothing exercises.
        const string key = "roundtrip-obj.txt";
        var payload = "create-session-roundtrip-payload"u8.ToArray();
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = name, Key = key, InputStream = new MemoryStream(payload) });
        Ensure(
            (await S3Fidelity.GetBytes(s3, name, key)).SequenceEqual(payload),
            "directory bucket did not round-trip an object written right after CreateSession");
    }
}
