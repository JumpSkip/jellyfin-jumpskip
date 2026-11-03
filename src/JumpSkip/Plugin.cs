using JumpSkip.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace JumpSkip;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginName = PluginMetadata.Name;
    public static readonly Guid PluginId = PluginMetadata.Id;
    public static ReleaseMetadata Release => PluginMetadata.Release;
    public static Plugin? Instance { get; private set; }

    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;
    public override string Name => PluginName;
    public override Guid Id => PluginId;
    public override string Description => PluginMetadata.Description;

    public override PluginInfo GetPluginInfo()
    {
        var info = base.GetPluginInfo();
        info.Version = Release.Version;
        return info;
    }

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        ((PluginConfiguration)configuration).Validate();
        base.UpdateConfiguration(configuration);
    }

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new() { Name = "JumpSkip", EmbeddedResourcePath = "JumpSkip.Configuration.settings.html", EnableInMainMenu = true, DisplayName = PluginName }
    ];
}
