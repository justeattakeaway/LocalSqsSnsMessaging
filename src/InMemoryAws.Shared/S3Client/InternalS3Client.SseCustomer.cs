using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

internal sealed partial class InternalS3Client
{
    /// <summary>
    /// SSE-C: an object written with a customer-provided key (<c>SseCustomerKeyMd5</c>) can only be
    /// read with that key, sent as the three <c>x-amz-server-side-encryption-customer-*</c> headers.
    /// </summary>
#pragma warning disable CA1822 // The partial method is declared on the instance.
    partial void CheckSseCustomerKey(S3ObjectVersion version, IS3ObjectRead request) =>
        CheckSseCustomerKey(version, request.SSECustomerAlgorithm, request.SSECustomerKey, request.SSECustomerKeyMD5);
#pragma warning restore CA1822

    private static void CheckSseCustomerKeyForAttributes(S3ObjectVersion version, GetObjectAttributesRequest request) =>
        CheckSseCustomerKey(version, request.SSECustomerAlgorithm, request.SSECustomerKey, request.SSECustomerKeyMD5);

    /// <summary>
    /// Reading an object: an SSE-C object needs its key, and the key must be the one it was written
    /// with; a key sent for an object not encrypted with one is refused.
    /// </summary>
    private static void CheckSseCustomerKey(S3ObjectVersion version, string? algorithm, string? key, string? keyMd5)
    {
        var given = SseCustomerKeyMd5(algorithm, key, keyMd5);
        if (version.SseCustomerKeyMd5 is null)
        {
            if (given is not null)
            {
                throw S3Errors.InvalidRequest("The encryption parameters are not applicable to this object.");
            }

            return;
        }

        if (given is null)
        {
            throw S3Errors.InvalidRequest("The object was stored using a form of Server Side Encryption. The correct parameters must be provided to retrieve the object.");
        }

        if (given != version.SseCustomerKeyMd5)
        {
            throw S3Errors.AccessDenied("Requests specifying Server Side Encryption with Customer provided keys must provide the correct secret key.");
        }
    }

    /// <summary>
    /// Copying from an SSE-C object (CopyObject, UploadPartCopy): the request must send the source's key
    /// as the <c>x-amz-copy-source-server-side-encryption-customer-*</c> headers, and it must be the one
    /// the source was written with.
    /// </summary>
    private static void CheckCopySourceSseCustomerKey(S3ObjectVersion source, Http.S3WireRequest request)
    {
        var given = SseCustomerKeyMd5(
            request.Header("x-amz-copy-source-server-side-encryption-customer-algorithm"),
            request.Header("x-amz-copy-source-server-side-encryption-customer-key"),
            request.Header("x-amz-copy-source-server-side-encryption-customer-key-MD5"));
        if (source.SseCustomerKeyMd5 is null)
        {
            if (given is not null)
            {
                throw S3Errors.InvalidRequest("The encryption parameters are not applicable to this object.");
            }

            return;
        }

        if (given is null)
        {
            throw S3Errors.InvalidRequest("The object was stored using a form of Server Side Encryption. The correct parameters must be provided to retrieve the object.");
        }

        if (given != source.SseCustomerKeyMd5)
        {
            throw S3Errors.InvalidRequest("The provided encryption parameters did not match the ones used originally.");
        }
    }

    /// <summary>
    /// A part of an upload initiated with SSE-C (UploadPart, UploadPartCopy) must send the key the upload
    /// was initiated with.
    /// </summary>
    private static void CheckUploadSseCustomerKey(S3MultipartUpload upload, Http.S3WireRequest request)
    {
        if (upload.SseCustomerKeyMd5 is null)
        {
            return;
        }

        var given = SseCustomerKeyMd5(
            request.Header("x-amz-server-side-encryption-customer-algorithm"),
            request.Header("x-amz-server-side-encryption-customer-key"),
            request.Header("x-amz-server-side-encryption-customer-key-MD5"));
        if (given is null)
        {
            throw S3Errors.InvalidRequest("The multipart upload initiate requested encryption. Subsequent part requests must include the appropriate encryption parameters.");
        }

        if (given != upload.SseCustomerKeyMd5)
        {
            throw S3Errors.InvalidRequest("The provided encryption parameters did not match the ones used originally.");
        }
    }

    /// <summary>
    /// The MD5 (base64) of the customer key a request sends, after checking the three headers agree:
    /// <c>AES256</c>, a 256-bit key, and the key's MD5. Null when the request sends none of them.
    /// </summary>
    internal static string? SseCustomerKeyMd5(string? algorithm, string? key, string? keyMd5)
    {
        if (algorithm is null && key is null && keyMd5 is null)
        {
            return null;
        }

        if (algorithm is null)
        {
            throw S3Errors.InvalidArgument(
                "Requests specifying Server Side Encryption with Customer provided keys must provide a valid encryption algorithm.",
                "x-amz-server-side-encryption-customer-algorithm",
                "null");
        }

        if (algorithm != "AES256")
        {
            throw new S3Exception(
                "InvalidEncryptionAlgorithmError",
                "The encryption request you specified is not valid. The valid value is AES256.",
                System.Net.HttpStatusCode.BadRequest,
                ("ArgumentName", "x-amz-server-side-encryption-customer-algorithm"),
                ("ArgumentValue", algorithm));
        }

        if (key is null)
        {
            throw S3Errors.InvalidArgument(
                "Requests specifying Server Side Encryption with Customer provided keys must provide an appropriate secret key.",
                "x-amz-server-side-encryption-customer-key",
                "null");
        }

        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(key);
        }
        catch (FormatException)
        {
            throw S3Errors.InvalidArgument("The secret key was invalid for the specified algorithm.", "x-amz-server-side-encryption-customer-key", key);
        }

        if (keyBytes.Length != 32)
        {
            throw S3Errors.InvalidArgument("The secret key was invalid for the specified algorithm.", "x-amz-server-side-encryption-customer-key", key);
        }

        var actual = Convert.ToBase64String(S3Checksums.Md5(keyBytes));
        if (keyMd5 is not null && keyMd5 != actual)
        {
            throw S3Errors.InvalidArgument(
                "The calculated MD5 hash of the key did not match the hash that was provided.",
                "x-amz-server-side-encryption-customer-key-MD5",
                keyMd5);
        }

        return actual;
    }
}
