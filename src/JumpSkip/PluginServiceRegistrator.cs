using System.Net;
using JumpSkip.Configuration;
using JumpSkip.Providers;
using JumpSkip.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JumpSkip;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<Func<PluginConfiguration>>(_ => () => Plugin.Instance?.Configuration ?? new());
        serviceCollection.AddSingleton<Action<PluginConfiguration>>(_ => config => Plugin.Instance!.UpdateConfiguration(config));
        serviceCollection.AddHttpClient("JumpSkip", client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.MaxResponseContentBufferSize = 2 * 1024 * 1024;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = false });
        serviceCollection.AddSingleton(sp => new CacheStore(
            Path.Combine(sp.GetRequiredService<IApplicationPaths>().PluginConfigurationsPath, "JumpSkip", "cache-v1.json"),
            sp.GetRequiredService<ILogger<CacheStore>>()));
        serviceCollection.AddSingleton<ProviderTransport>();
        serviceCollection.AddSingleton<ISegmentSource, IntroDbClient>();
        serviceCollection.AddSingleton<ISegmentSource, TheIntroDbClient>();
        serviceCollection.AddSingleton<ISegmentSource, SkipDbClient>();
        serviceCollection.AddSingleton<MediaIdentity>();
        serviceCollection.AddSingleton<LibraryItems>();
        serviceCollection.AddSingleton<ItemFilter>();
        serviceCollection.AddSingleton<SegmentMerger>();
        serviceCollection.AddSingleton<SegmentService>();
        serviceCollection.AddSingleton<ChapterWriter>();
        serviceCollection.AddSingleton<ChapterFallback>();
        serviceCollection.AddSingleton<SegmentStorage>();
        serviceCollection.AddSingleton<SettingsService>();
        serviceCollection.AddSingleton<IMediaSegmentProvider, JumpSkipSegmentProvider>();
        serviceCollection.AddHostedService<OnDemandFetchService>();
        // Jellyfin discovers public IScheduledTask implementations from plugin assemblies.
    }
}
