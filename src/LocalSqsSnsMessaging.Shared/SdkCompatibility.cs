namespace LocalSqsSnsMessaging;

/// <summary>
/// Helper methods to handle differences between AWS SDK v3 (non-nullable) and v4 (nullable) types.
/// </summary>
internal static class SdkCompatibility
{
#if AWS_SDK_V3
    // AWS SDK v3 uses non-nullable value types for most request properties.
    // These methods provide a consistent API that works with both SDKs.

    // Methods for SDK request types (int in v3)
    public static int GetValueOrZero(int value) => value;
    public static long GetValueOrZero(long value) => value;
    public static bool GetValueOrFalse(bool value) => value;
    public static int GetValueOrDefault(int value, int defaultValue) => value == 0 ? defaultValue : value;
    public static long GetValueOrDefault(long value, long defaultValue) => value == 0 ? defaultValue : value;

    public static bool HasNonDefaultValue(int value) => value != 0;
    public static bool HasNonDefaultValue(long value) => value != 0;
    public static bool HasNonDefaultValue(bool value) => value;

    public static int GetValue(int value) => value;
    public static long GetValue(long value) => value;
    public static bool GetValue(bool value) => value;

    public static double GetValueOrZero(double value) => value;
    public static bool HasNonDefaultValue(double value) => value != 0;
    public static double GetValue(double value) => value;

    public static bool HasNonDefaultValue(DateTime value) => value != default;
    public static DateTime GetValue(DateTime value) => value;

    // Methods to convert our internal int? to SDK's int (v3)
    public static int ToSdkValue(int? value) => value.GetValueOrDefault();
    public static long ToSdkValue(long? value) => value.GetValueOrDefault();
#else
    // AWS SDK v4 uses nullable value types for most request properties.

    public static int GetValueOrZero(int? value) => value.GetValueOrDefault();
    public static long GetValueOrZero(long? value) => value.GetValueOrDefault();
    public static bool GetValueOrFalse(bool? value) => value.GetValueOrDefault();
    public static int GetValueOrDefault(int? value, int defaultValue) => value ?? defaultValue;
    public static long GetValueOrDefault(long? value, long defaultValue) => value ?? defaultValue;

    public static bool HasNonDefaultValue(int? value) => value.HasValue;
    public static bool HasNonDefaultValue(long? value) => value.HasValue;
    public static bool HasNonDefaultValue(bool? value) => value.HasValue;

    public static int GetValue(int? value) => value!.Value;
    public static long GetValue(long? value) => value!.Value;
    public static bool GetValue(bool? value) => value!.Value;

    public static double GetValueOrZero(double? value) => value.GetValueOrDefault();
    public static bool HasNonDefaultValue(double? value) => value.HasValue;
    public static double GetValue(double? value) => value!.Value;

    public static bool HasNonDefaultValue(DateTime? value) => value.HasValue;
    public static DateTime GetValue(DateTime? value) => value!.Value;

    // Methods to convert our internal int? to SDK's int? (v4) - just pass through
    public static int? ToSdkValue(int? value) => value;
    public static long? ToSdkValue(long? value) => value;
#endif
}
