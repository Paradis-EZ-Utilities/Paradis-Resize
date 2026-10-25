using NetVips;
using ParadisResize.Core.Util;

namespace ParadisResize.Core.Processing;

/// <summary>One-time libvips setup.</summary>
public static class VipsRuntime
{
    private static readonly object Gate = new();
    private static bool _initialized;

    /// <summary>"8.18.7".</summary>
    public static string Version { get; private set; } = "";

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized) return;
            if (!ModuleInitializer.VipsInitialized)
                throw new InvalidOperationException("libvips could not be loaded: " + ModuleInitializer.Exception?.Message, ModuleInitializer.Exception);

            // Each image is read once, so libvips' operation cache would only hold on to memory and file handles
            // (an open handle would also stop a temporary file from being renamed on Windows).
            Cache.Max = 0;
            Cache.MaxFiles = 0;
            Cache.MaxMem = 0;

            // Refuse to load anything through the libvips loaders marked untrusted (none are needed here).
            NetVips.NetVips.BlockUntrusted = true;

            Version = $"{NetVips.NetVips.Version(0)}.{NetVips.NetVips.Version(1)}.{NetVips.NetVips.Version(2)}";
            CoreLog.Info($"libvips {Version} ready (threads: {NetVips.NetVips.Concurrency})");
            _initialized = true;
        }
    }
}
