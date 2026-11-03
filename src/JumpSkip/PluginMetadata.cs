using System.Reflection;
using System.Text.Json;

[assembly: AssemblyCompany(JumpSkip.PluginMetadata.Owner)]
[assembly: AssemblyDescription(JumpSkip.PluginMetadata.Description)]

namespace JumpSkip;

public sealed record ReleaseMetadata(Version Version, Version TargetAbi, DateTimeOffset Timestamp, string Changelog);

public static class PluginMetadata
{
    // Stable identity and installation defaults. Release-specific values belong in the root meta.json.
    public const string Name = "JumpSkip";
    public static readonly Guid Id = new("729820ed-19c7-48ce-aaca-42ffe3e9f4a1");
    public const string Category = "General";
    public const string Description = "Detect and skip media segments (intros, recaps, outros/credits, and previews) using both chapter titles and crowdsourced timestamp data from TheIntroDB, IntroDB, and SkipDB.\n";
    public const string Overview = Description;
    public const string Owner = "ElectricArdvark";
    public const string ImagePath = "icon.svg";
    public const string AssemblyFileName = "JumpSkip.dll";
    public const string InitialStatus = "Active";
    public const bool AutoUpdateByDefault = true;

    public static ReleaseMetadata Release { get; } = LoadEmbeddedRelease();

    private static ReleaseMetadata LoadEmbeddedRelease()
    {
        var assembly = typeof(PluginMetadata).Assembly;
        using var stream = assembly.GetManifestResourceStream("JumpSkip.release.json")
            ?? throw new InvalidDataException("Embedded JumpSkip release metadata is missing.");
        var release = ReadRelease(stream);
        if (release.Version != assembly.GetName().Version)
            throw new InvalidDataException("The DLL version must match the embedded release metadata.");
        return release;
    }

    internal static ReleaseMetadata ReadRelease(Stream stream)
    {
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Release metadata must be a JSON object.");

        Version ReadVersion(string name)
        {
            if (!root.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String
                || !Version.TryParse(field.GetString(), out var version) || version.Build < 0 || version.Revision < 0)
                throw new InvalidDataException($"{name} must have four numeric parts.");
            return version;
        }

        if (!root.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.String
            || !timestamp.TryGetDateTimeOffset(out var date))
            throw new InvalidDataException("timestamp must be an ISO 8601 date and time.");
        if (!root.TryGetProperty("changelog", out var changelog) || changelog.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("changelog must be a string.");
        return new(ReadVersion("version"), ReadVersion("targetAbi"), date, changelog.GetString()!);
    }

    // Used by the build to produce the complete meta.json installed beside the DLL.
    public static string CreateInstallMetadata() => JsonSerializer.Serialize(new
    {
        category = Category, name = Name, guid = Id, description = Description, overview = Overview, owner = Owner,
        version = Release.Version.ToString(), targetAbi = Release.TargetAbi.ToString(), timestamp = Release.Timestamp,
        imagePath = ImagePath, assemblies = new[] { AssemblyFileName }, status = InitialStatus,
        autoUpdate = AutoUpdateByDefault, changelog = Release.Changelog
    }, new JsonSerializerOptions { WriteIndented = true });
}
