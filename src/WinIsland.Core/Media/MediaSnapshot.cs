namespace WinIsland.Core.Media;

/// <summary>
/// Now-playing information. <see cref="Position"/> was sampled at <see cref="PositionSampledAt"/>;
/// the current position is interpolated from it rather than polled from the player.
/// </summary>
public sealed record MediaSnapshot(
    string Title,
    string Artist,
    string SourceAppId,
    bool IsPlaying,
    bool CanGoNext,
    bool CanGoPrevious,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset PositionSampledAt,
    MediaArtwork? Artwork = null,
    string? Album = null,
    bool CanShuffle = false,
    bool IsShuffleActive = false,
    bool CanRepeat = false,
    MediaRepeatMode RepeatMode = MediaRepeatMode.None,
    bool CanSeek = false)
{
    public TimeSpan PositionAt(DateTimeOffset now)
    {
        TimeSpan position = IsPlaying ? Position + (now - PositionSampledAt) : Position;
        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return Duration > TimeSpan.Zero && position > Duration ? Duration : position;
    }
}
