using JumpSkip.Models;

namespace JumpSkip.Providers;

public interface ISegmentSource
{
    string Name { get; }
    string? GetPath(MediaLookup lookup);
    ProviderPayload Parse(string json);
}
