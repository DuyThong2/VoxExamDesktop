using SharpGen.Runtime;
using Vortice.MediaFoundation;
using VoxOralExam.DesktopApp.Services;

namespace VoxOralExam.DesktopApp.Infra.Recording.VideoEncoding;

internal readonly record struct AudioFormatSpec(int SampleRate, int Channels);

internal static class VideoSinkWriterFactory
{
    // MFT_ENUM_FLAG_ALL from mftransform.h -- Vortice does not wrap this flag as an enum, and
    // MFTranscodeGetAudioOutputAvailableTypes takes it as a raw int.
    private const int MftEnumFlagAll = 0x3F;

    public static (IMFSinkWriter Writer, int VideoStreamIndex, int? AudioStreamIndex) Create(
        string outputPath,
        int width,
        int height,
        int framesPerSecond,
        int bitrate,
        AudioFormatSpec? audio = null)
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

            int? audioStreamIndex = null;
            if (audio is { } audioSpec)
            {
                try
                {
                    audioStreamIndex = AddAudioStream(writer, audioSpec);
                }
                catch (Exception ex)
                {
                    // IMFSinkWriter has no way to remove an already-added stream, so a partially
                    // configured audio stream (e.g. AddStream succeeded but no matching AAC output
                    // type/SetInputMediaType failed) can't be undone in place -- the only safe
                    // recovery is to abandon this writer and build a fresh, video-only one rather
                    // than risk BeginWriting() failing on a half-configured stream and taking the
                    // video recording down with it.
                    LocalFileLogger.Error("video_sink_writer", "audio_stream_unavailable", ex);
                    writer.Dispose();
                    return Create(outputPath, width, height, framesPerSecond, bitrate, audio: null);
                }
            }

            writer.BeginWriting();
            return (writer, streamIndex, audioStreamIndex);
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    private static int AddAudioStream(IMFSinkWriter writer, AudioFormatSpec audio)
    {
        using var outputType = FindAacOutputType(audio.SampleRate, audio.Channels) ??
            throw new InvalidOperationException(
                $"No AAC encoder output type is available on this machine for " +
                $"{audio.SampleRate}Hz/{audio.Channels}ch audio.");

        var audioStreamIndex = writer.AddStream(outputType);

        using var inputType = MediaFactory.MFCreateMediaType();
        var blockAlign = (uint)(audio.Channels * 2);
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
        inputType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm).CheckError();
        inputType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, (uint)16).CheckError();
        inputType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)audio.SampleRate).CheckError();
        inputType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)audio.Channels).CheckError();
        inputType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, blockAlign).CheckError();
        inputType.Set(
            MediaTypeAttributeKeys.AudioAvgBytesPerSecond,
            (uint)audio.SampleRate * blockAlign).CheckError();
        inputType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, true).CheckError();

        writer.SetInputMediaType(audioStreamIndex, inputType, null);
        return audioStreamIndex;
    }

    // The built-in AAC encoder MFT only accepts a fixed set of (sample rate, channel count, bit
    // rate) combinations -- guessing a bitrate risks MF_E_INVALIDMEDIATYPE on some machines/driver
    // configurations, so enumerate what this machine's encoder actually supports and pick a type
    // that matches our fixed PCM input (see AudioMixer/TurnAudioRecorder: always 16-bit mono) rather
    // than hardcoding one.
    private static IMFMediaType? FindAacOutputType(int sampleRate, int channels)
    {
        MediaFactory.MFTranscodeGetAudioOutputAvailableTypes(
            AudioFormatGuids.Aac,
            MftEnumFlagAll,
            null,
            out var collection).CheckError();

        using (collection)
        {
            for (var i = 0; i < collection.ElementCount; i++)
            {
                using var element = collection.GetElement(i);
                var candidate = ((ComObject)element).QueryInterfaceOrNull<IMFMediaType>();
                if (candidate is null)
                {
                    continue;
                }

                var rate = candidate.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond);
                var candidateChannels = candidate.GetUInt32(MediaTypeAttributeKeys.AudioNumChannels);
                if (rate == (uint)sampleRate && candidateChannels == (uint)channels)
                {
                    return candidate;
                }

                candidate.Dispose();
            }
        }

        return null;
    }

    private static ulong Pack(int high, int low) =>
        ((ulong)(uint)high << 32) | (uint)low;
}
