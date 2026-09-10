using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ShellStudio.Tools;

/// <summary>
/// Feature identifiers used by the WinSetView donor.  They are kept as
/// protocol data so the GUI can show exactly which Windows feature is being
/// reviewed without bundling ViVeTool or the GPL ViVe library.
/// </summary>
public static class FeatureFlagIds
{
    public const uint Windows10Search = 18_755_234;
    public const uint Windows11Explorer = 40_729_001;
}

/// <summary>
/// Native implementation of the small subset of the Windows feature store
/// used by WinSetView.  ViVeTool uses the same ntdll exports with a User
/// priority update; the signatures here are independently declared so no
/// ViVe source or binary is a runtime dependency.
/// </summary>
public sealed class WindowsFeatureFlagService : IToolFeatureFlagService
{
    private const uint RuntimeConfiguration = 1;
    private const uint UserPriority = 8;
    private const uint EnabledStateDisabled = 1;
    private const uint EnabledStateEnabled = 2;
    private const uint OperationFeatureAndVariant = 1 | 2;
    private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
    private const int StatusObjectPathNotFound = unchecked((int)0xC000003A);
    private const int StatusNotFound = unchecked((int)0xC0000225);

    private readonly IToolEnvironment _environment;

    public WindowsFeatureFlagService(IToolEnvironment environment) => _environment = environment;

    public FeatureFlagInspection Inspect(uint featureId)
    {
        if (!TryGetSupport(featureId, out var supportError))
            return new FeatureFlagInspection(featureId, false, false, null, supportError);

        try
        {
            ulong changeStamp = 0;
            var status = RtlQueryFeatureConfiguration(featureId, RuntimeConfiguration, ref changeStamp, out var configuration);
            if (status == 0)
            {
                var state = (configuration.CompactState & 0x30) >> 4;
                return new FeatureFlagInspection(featureId, true, true,
                    state == EnabledStateEnabled ? true : state == EnabledStateDisabled ? false : null,
                    Priority: configuration.CompactState & 0xF,
                    CompactState: configuration.CompactState,
                    VariantPayload: configuration.VariantPayload);
            }

            // ViVeTool treats a missing per-feature record as the disabled
            // default.  Preserve that behavior while surfacing unexpected
            // native failures to the caller.
            if (status is StatusObjectNameNotFound or StatusObjectPathNotFound or StatusNotFound)
                return new FeatureFlagInspection(featureId, true, false, null);
            return new FeatureFlagInspection(featureId, true, false, null,
                $"RtlQueryFeatureConfiguration failed with NTSTATUS 0x{unchecked((uint)status):X8} ({new Win32Exception(status).Message}).");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or SEHException)
        {
            return new FeatureFlagInspection(featureId, false, false, null,
                $"The Windows feature configuration API is unavailable: {ex.Message}");
        }
    }

    public FeatureFlagMutationResult Set(uint featureId, bool enabled)
    {
        var previous = Inspect(featureId);
        if (!previous.Supported)
            return new FeatureFlagMutationResult(false, previous, previous.Error ?? "The feature is not supported on this Windows build.");
        if (previous.Error is not null)
            return new FeatureFlagMutationResult(false, previous, previous.Error);
        if (previous.Enabled == enabled)
            return new FeatureFlagMutationResult(true, previous);

        try
        {
            // This mirrors ViVeTool's /enable and /disable defaults: Runtime
            // store, User priority, and FeatureState | VariantState with no
            // variant payload.  A previous change stamp of zero lets the
            // kernel obtain its current stamp atomically.
            ulong previousChangeStamp = 0;
            var update = new RtlFeatureConfigurationUpdate
            {
                FeatureId = featureId,
                Priority = UserPriority,
                EnabledState = enabled ? EnabledStateEnabled : EnabledStateDisabled,
                EnabledStateOptions = 0,
                Variant = 0,
                VariantPayloadKind = 0,
                VariantPayload = 0,
                Operation = OperationFeatureAndVariant
            };
            var status = RtlSetFeatureConfigurations(ref previousChangeStamp, RuntimeConfiguration, [update], 1);
            if (status != 0)
                return new FeatureFlagMutationResult(false, previous,
                    $"RtlSetFeatureConfigurations failed with NTSTATUS 0x{unchecked((uint)status):X8} ({new Win32Exception(status).Message}).");
            return new FeatureFlagMutationResult(true, previous);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or SEHException)
        {
            return new FeatureFlagMutationResult(false, previous,
                $"The Windows feature configuration API is unavailable: {ex.Message}");
        }
    }

    public FeatureFlagMutationResult Reset(uint featureId)
    {
        var previous = Inspect(featureId);
        if (!previous.Supported)
            return new FeatureFlagMutationResult(false, previous, previous.Error ?? "The feature is not supported on this Windows build.");
        if (previous.Error is not null)
            return new FeatureFlagMutationResult(false, previous, previous.Error);
        if (!previous.Exists)
            return new FeatureFlagMutationResult(true, previous);

        try
        {
            ulong previousChangeStamp = 0;
            var update = new RtlFeatureConfigurationUpdate
            {
                FeatureId = featureId,
                Priority = UserPriority,
                Operation = 4 // RTL_FEATURE_CONFIGURATION_OPERATION.ResetState
            };
            var status = RtlSetFeatureConfigurations(ref previousChangeStamp, RuntimeConfiguration, [update], 1);
            if (status != 0)
                return new FeatureFlagMutationResult(false, previous,
                    $"RtlSetFeatureConfigurations reset failed with NTSTATUS 0x{unchecked((uint)status):X8} ({new Win32Exception(status).Message}).");
            return new FeatureFlagMutationResult(true, previous);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or SEHException)
        {
            return new FeatureFlagMutationResult(false, previous,
                $"The Windows feature configuration API is unavailable: {ex.Message}");
        }
    }

    private bool TryGetSupport(uint featureId, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
        {
            error = "The WinSetView feature flags require a 64-bit Windows feature configuration API.";
            return false;
        }

        var build = Environment.OSVersion.Version.Build;
        var ubr = ReadUbr();
        switch (featureId)
        {
            case FeatureFlagIds.Windows10Search:
                if (build >= 19045 && build < 21996 && ubr >= 3754) return true;
                error = $"Feature {featureId} is donor-supported only on Windows 10 build 19045+ before build 21996 with UBR 3754+ (current build {build}.{ubr}).";
                return false;
            case FeatureFlagIds.Windows11Explorer:
                if (build == 22621 && ubr >= 3007 && ubr < 3085) return true;
                error = $"Feature {featureId} is donor-supported only on Windows 11 build 22621 UBR 3007-3084 (current build {build}.{ubr}).";
                return false;
            default:
                error = $"Feature {featureId} is not in the reviewed WinSetView feature set.";
                return false;
        }
    }

    private int ReadUbr()
    {
        try
        {
            var value = _environment.Registry.GetValue("HKLM", @"Software\Microsoft\Windows NT\CurrentVersion", "UBR");
            return value switch
            {
                int integer => integer,
                long longValue when longValue is >= 0 and <= int.MaxValue => (int)longValue,
                _ when int.TryParse(value?.ToString(), out var parsed) => parsed,
                _ => 0
            };
        }
        catch
        {
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RtlFeatureConfiguration
    {
        public uint FeatureId;
        public uint CompactState;
        public uint VariantPayload;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RtlFeatureConfigurationUpdate
    {
        public uint FeatureId;
        public uint Priority;
        public uint EnabledState;
        public uint EnabledStateOptions;
        public uint Variant;
        public uint VariantPayloadKind;
        public uint VariantPayload;
        public uint Operation;
    }

    [DllImport("ntdll.dll")]
    private static extern int RtlQueryFeatureConfiguration(
        uint featureId,
        uint featureConfigurationType,
        ref ulong changeStamp,
        out RtlFeatureConfiguration featureConfiguration);

    [DllImport("ntdll.dll")]
    private static extern int RtlSetFeatureConfigurations(
        ref ulong previousChangeStamp,
        uint featureConfigurationType,
        [In] RtlFeatureConfigurationUpdate[] featureConfigurations,
        int featureConfigurationCount);
}
