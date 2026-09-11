// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Media;

public sealed record MediaServiceOptions(bool PauseOtherSessionsOnPlay, bool EnableITunes = true)
{
    public static MediaServiceOptions Default { get; } = new(false, true);
}