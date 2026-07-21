using Vortice.MediaFoundation;

namespace VoxOralExam.DesktopApp.Infra.Recording.VideoEncoding;

internal static class VideoSinkWriterFactory
{
    public static (IMFSinkWriter Writer, int StreamIndex) Create(
        string outputPath,
        int width,
        int height,
        int framesPerSecond,
        int bitrate)
    {
        using var attributes = MediaFactory.MFCreateAttributes(1);
        attributes.Set(
            SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms,
            true).CheckError();

        var writer = MediaFactory.MFCreateSinkWriterFromURL(
            outputPath,
            null,
            attributes);

        try
        {
            using var outputType = MediaFactory.MFCreateMediaType();
            outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
            outputType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate).CheckError();
            outputType.SetEnumValue(
                MediaTypeAttributeKeys.InterlaceMode,
                VideoInterlaceMode.Progressive).CheckError();
            outputType.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height)).CheckError();
            outputType.Set(MediaTypeAttributeKeys.FrameRate, Pack(framesPerSecond, 1)).CheckError();
            outputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1)).CheckError();

            var streamIndex = writer.AddStream(outputType);

            using var inputType = MediaFactory.MFCreateMediaType();
            inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Argb32).CheckError();
            inputType.SetEnumValue(
                MediaTypeAttributeKeys.InterlaceMode,
                VideoInterlaceMode.Progressive).CheckError();
            inputType.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height)).CheckError();
            inputType.Set(MediaTypeAttributeKeys.FrameRate, Pack(framesPerSecond, 1)).CheckError();
            inputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1)).CheckError();

            writer.SetInputMediaType(streamIndex, inputType, null);
            writer.BeginWriting();
            return (writer, streamIndex);
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    private static ulong Pack(int high, int low) =>
        ((ulong)(uint)high << 32) | (uint)low;
}
