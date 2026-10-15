namespace OmtCaptureStudio.Models;

/// <summary>
/// A single ASIO input channel discovered by briefly probing a driver.
/// </summary>
/// <param name="ChannelIndex">Zero-based ASIO input channel index.</param>
/// <param name="DisplayName">Driver-reported channel name when available, else a positional name.</param>
public sealed record AsioInputChannel(int ChannelIndex, string DisplayName);
