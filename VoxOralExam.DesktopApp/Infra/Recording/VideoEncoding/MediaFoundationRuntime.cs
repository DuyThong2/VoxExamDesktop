using Vortice.MediaFoundation;

namespace VoxOralExam.DesktopApp.Infra.Recording.VideoEncoding;

internal static class MediaFoundationRuntime
{
    private static readonly object Sync = new();
    private static int _referenceCount;

    public static void Acquire()
    {
        lock (Sync)
        {
            if (_referenceCount == 0)
            {
                MediaFactory.MFStartup(false).CheckError();
            }

            _referenceCount++;
        }
    }

    public static void Release()
    {
        lock (Sync)
        {
            if (_referenceCount == 0)
            {
                return;
            }

            _referenceCount--;
            if (_referenceCount == 0)
            {
                MediaFactory.MFShutdown();
            }
        }
    }
}
