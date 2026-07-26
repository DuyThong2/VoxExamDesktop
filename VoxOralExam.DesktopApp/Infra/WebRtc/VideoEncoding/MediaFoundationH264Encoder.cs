using System.Runtime.InteropServices;
using SharpGen.Runtime;
using SIPSorceryMedia.Abstractions;
using Vortice.MediaFoundation;
using VoxOralExam.DesktopApp.Infra.Recording.VideoEncoding;

namespace VoxOralExam.DesktopApp.Infra.WebRtc.VideoEncoding;

/// <summary>
/// H.264 encoder for the live monitor stream, driving a Media Foundation encoder MFT directly.
///
/// Replaces SIPSorceryMedia.FFmpeg's FFmpegVideoEncoder, which reached H.264 through libx264 in a
/// GPL-configured FFmpeg build -- shipping those DLLs alongside a closed-source app would have put
/// the whole app under the GPL. Media Foundation is part of Windows, so there is nothing to bundle,
/// nothing to attribute, and the H.264 patent licence for the encoder is covered by Windows itself.
///
/// The recording path already encodes H.264 through Media Foundation (see VideoSinkWriterFactory),
/// but through IMFSinkWriter, which only ever writes to a file/byte stream. WebRTC needs the
/// encoded elementary stream in memory, frame by frame, so this drives the encoder MFT itself
/// rather than going through a sink writer.
///
/// Not thread-safe: MonitorStreamClient calls this from its single video worker only (see the
/// _videoQueue field's comment there for why encode must never run concurrently).
/// </summary>
internal sealed class MediaFoundationH264Encoder : IDisposable
{
    // MFT_CATEGORY_VIDEO_ENCODER, mfapi.h.
    private static readonly Guid VideoEncoderCategory = new("f79eac7d-e545-4387-bdee-d647d7bde42a");

    // codecapi.h. Vortice exposes no ICodecAPI wrapper, but the Microsoft H.264 encoder MFT also
    // honours these as plain attributes on its own attribute store, which IMFAttributes can set.
    private static readonly Guid AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    private static readonly Guid AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    private static readonly Guid AVEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");

    // CODECAPI_AVLowLatencyMode and MF_LOW_LATENCY are deliberately the same GUID in the Windows
    // headers; setting it on the MFT attribute store is what tells the encoder not to buffer frames
    // for lookahead, which is the difference between sub-frame and multi-frame encode latency.
    private static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    // eAVEncCommonRateControlMode_CBR. A live stream has a fixed pipe, so constant bitrate is the
    // right mode -- VBR would let a busy screen balloon the bitrate and stall the connection.
    private const uint RateControlModeCbr = 0;

    // eAVEncH264VProfile_Base, matching the profile-level-id=42e01f (constrained baseline) that
    // MonitorStreamClient advertises in its SDP. The level is deliberately NOT pinned: screen
    // capture streams at the monitor's full resolution (see LiveMonitorStreamService.OnScreenFrame),
    // which can exceed what level 3.1 permits, and forcing too low a level makes the encoder reject
    // the media type outright. Letting it derive the level from the frame size is also exactly what
    // libx264 did here before.
    private const uint H264ProfileBaseline = 66;

    // Seconds between IDR frames. SIPSorcery exposes no hook for the receiver's PLI, so a viewer
    // opening the monitor UI mid-exam cannot ask for a keyframe and instead waits for the next
    // scheduled one -- 2 seconds keeps that wait short without spending much bitrate on IDRs. The
    // FFmpeg encoder this replaces behaved the same way, for the same reason.
    private const int GopSeconds = 2;

    // MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_LOCALMFT | MFT_ENUM_FLAG_SORTANDFILTER.
    //
    // MFT_ENUM_FLAG_HARDWARE is deliberately excluded. Hardware H.264 encoders register as async
    // MFTs, which cannot be driven by the straightforward ProcessInput/ProcessOutput loop below --
    // they require the event-driven METransformNeedInput/METransformHaveOutput model and bring
    // per-GPU-driver quirks. This is a best-effort thumbnail-grade live view running next to the
    // real recording, so the uniform, predictable software encoder is worth more than the GPU's
    // throughput. MFT_ENUM_FLAG_FIELDOFUSE is excluded too: those are encoders that require a
    // separate field-of-use licence unlock, which is the sort of obligation this class exists to
    // get away from.
    private const uint EncoderEnumFlags = 0x01 | 0x10 | 0x40;

    private const uint MF_E_TRANSFORM_NEED_MORE_INPUT = 0xC00D6D72;
    private const uint MF_E_TRANSFORM_STREAM_CHANGE = 0xC00D6D61;

    private readonly int _frameRate;
    private readonly int _bitrate;

    private IMFTransform? _transform;
    private int _width;
    private int _height;
    private int _inputStreamId;
    private int _outputStreamId;
    private int _outputBufferSize;
    private byte[]? _nv12Buffer;

    // SPS/PPS as the encoder reports them on its output media type. Kept so an IDR that arrives
    // without an inline parameter set can still be decoded by a viewer that joined after the
    // stream started -- see EnsureParameterSets.
    private byte[]? _sequenceHeader;

    private long _lastSampleTime = -1;
    private bool _mediaFoundationAcquired;
    private bool _disposed;

    public MediaFoundationH264Encoder(int frameRate, int bitrate)
    {
        _frameRate = Math.Clamp(frameRate, 1, 60);
        _bitrate = bitrate;
        MediaFoundationRuntime.Acquire();
        _mediaFoundationAcquired = true;
    }

    /// <summary>
    /// Encodes one raw frame and returns its H.264 Annex B bytes, ready to hand to
    /// RTCPeerConnection.SendVideo (SIPSorcery's H.264 packetiser splits on Annex B start codes).
    ///
    /// Returns null when the encoder produced no output for this frame, which is normal and not an
    /// error -- an encoder is allowed to hold a frame back and emit it later.
    /// </summary>
    public byte[]? Encode(
        byte[] pixels, int width, int height, VideoPixelFormatsEnum pixelFormat, TimeSpan timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var bytesPerPixel = pixelFormat switch
        {
            VideoPixelFormatsEnum.Bgr or VideoPixelFormatsEnum.Rgb => 3,
            VideoPixelFormatsEnum.Bgra or VideoPixelFormatsEnum.Rgba => 4,
            _ => throw new NotSupportedException($"Pixel format {pixelFormat} cannot be encoded.")
        };

        var targetWidth = Nv12Converter.RoundDownToEven(width);
        var targetHeight = Nv12Converter.RoundDownToEven(height);
        if (targetWidth == 0 || targetHeight == 0)
        {
            return null;
        }

        // A capture source can change resolution mid-stream (the student unplugs a monitor, the
        // camera renegotiates a mode). An encoder MFT's frame size is fixed once its media types are
        // set, so the only way through is a fresh encoder -- which restarts the GOP and therefore
        // emits an IDR immediately, exactly what a viewer needs after a resolution change anyway.
        if (_transform is not null && (targetWidth != _width || targetHeight != _height))
        {
            DestroyTransform();
        }

        if (_transform is null)
        {
            CreateTransform(targetWidth, targetHeight);
        }

        var nv12 = _nv12Buffer!;
        Nv12Converter.Convert(pixels, width, height, bytesPerPixel, nv12, _width, _height);

        // Strictly increasing sample times: the MFT rejects a sample that does not advance, and
        // ScreenCaptureSource's keep-alive timer can re-emit a frame carrying a timestamp already
        // seen (see its own comments). MonitorStreamClient computes the RTP clock independently
        // from the same capture timestamps, so nudging a duplicate forward here cannot desync it.
        var sampleTime = Math.Max(Math.Max(0, timestamp.Ticks), _lastSampleTime + 1);
        _lastSampleTime = sampleTime;

        ProcessInput(nv12, sampleTime);
        return DrainOutput();
    }

    private void ProcessInput(byte[] nv12, long sampleTime)
    {
        var frameSize = Nv12Converter.FrameSize(_width, _height);

        using var buffer = MediaFactory.MFCreateMemoryBuffer(frameSize);
        buffer.Lock(out var destination, out _, out _);
        try
        {
            Marshal.Copy(nv12, 0, destination, frameSize);
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = frameSize;

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = sampleTime;
        sample.SampleDuration = TimeSpan.TicksPerSecond / _frameRate;

        _transform!.ProcessInput(_inputStreamId, sample, 0);
    }

    /// <summary>
    /// Pulls every sample the encoder is willing to give up for the frame just submitted. More than
    /// one is possible (the encoder can release a frame it had been holding at the same time as the
    /// new one), so the caller gets them concatenated -- which is fine, because Annex B start codes
    /// already delimit them and SIPSorcery re-splits on those.
    /// </summary>
    private byte[]? DrainOutput()
    {
        List<byte[]>? collected = null;

        while (true)
        {
            var streamInfo = _transform!.GetOutputStreamInfo(_outputStreamId);

            // The software encoder does not allocate its own output samples, so the caller must
            // supply one big enough for a whole encoded frame. GetOutputStreamInfo's size can grow
            // after a stream change, hence re-reading it each pass.
            using var outputBuffer = MediaFactory.MFCreateMemoryBuffer(
                Math.Max(streamInfo.Size, _outputBufferSize));
            using var outputSample = MediaFactory.MFCreateSample();
            outputSample.AddBuffer(outputBuffer);

            var dataBuffer = new OutputDataBuffer
            {
                StreamID = _outputStreamId,
                Sample = outputSample
            };

            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref dataBuffer, out _);

            if (result.Code == unchecked((int)MF_E_TRANSFORM_NEED_MORE_INPUT))
            {
                break;
            }

            if (result.Code == unchecked((int)MF_E_TRANSFORM_STREAM_CHANGE))
            {
                // The encoder wants to renegotiate its output type (it may do this once, right
                // after the first frames, to settle on a final type). Re-applying the output type
                // and retrying is the documented recovery.
                ApplyOutputType();
                _sequenceHeader = null;
                continue;
            }

            result.CheckError();

            var encoded = ReadSample(dataBuffer.Sample ?? outputSample);
            if (encoded.Length > 0)
            {
                (collected ??= []).Add(encoded);
            }
        }

        if (collected is null)
        {
            return null;
        }

        var frame = collected.Count == 1 ? collected[0] : Concat(collected);
        return EnsureParameterSets(frame);
    }

    private static byte[] ReadSample(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var source, out _, out var currentLength);
        try
        {
            var bytes = new byte[currentLength];
            Marshal.Copy(source, bytes, 0, currentLength);
            return bytes;
        }
        finally
        {
            buffer.Unlock();
        }
    }

    /// <summary>
    /// Guarantees an IDR is preceded by the SPS/PPS a decoder needs to start from it.
    ///
    /// The Microsoft encoder normally emits the parameter sets inline ahead of every IDR, in which
    /// case this returns the frame untouched. But that behaviour is not contractual and has been
    /// observed to vary, and getting it wrong produces the worst kind of bug here: the teacher's
    /// monitor view stays black with no error anywhere, because the receiver silently discards
    /// slices it has no parameter set for. Prepending the encoder's own reported sequence header
    /// when -- and only when -- the frame lacks one is cheap insurance against that.
    /// </summary>
    private byte[] EnsureParameterSets(byte[] frame)
    {
        var (hasSequenceParameterSet, hasIdr) = ScanNalTypes(frame);
        if (!hasIdr || hasSequenceParameterSet)
        {
            return frame;
        }

        _sequenceHeader ??= TryReadSequenceHeader();
        if (_sequenceHeader is null or { Length: 0 })
        {
            return frame;
        }

        var combined = new byte[_sequenceHeader.Length + frame.Length];
        _sequenceHeader.CopyTo(combined, 0);
        frame.CopyTo(combined, _sequenceHeader.Length);
        return combined;
    }

    private byte[]? TryReadSequenceHeader()
    {
        try
        {
            using var outputType = _transform!.GetOutputCurrentType(_outputStreamId);
            return outputType.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader);
        }
        catch (SharpGenException)
        {
            // MF_MT_MPEG_SEQUENCE_HEADER is optional on the output type; its absence just means the
            // fallback above is unavailable and the encoder's inline parameter sets are all there is.
            return null;
        }
    }

    /// <summary>
    /// Walks the Annex B start codes and reports whether the buffer carries a sequence parameter
    /// set (NAL type 7) and whether it carries an IDR slice (NAL type 5).
    /// </summary>
    private static (bool HasSequenceParameterSet, bool HasIdr) ScanNalTypes(ReadOnlySpan<byte> annexB)
    {
        var hasSequenceParameterSet = false;
        var hasIdr = false;

        for (var i = 0; i + 3 < annexB.Length; i++)
        {
            if (annexB[i] != 0x00 || annexB[i + 1] != 0x00)
            {
                continue;
            }

            int headerIndex;
            if (annexB[i + 2] == 0x01)
            {
                headerIndex = i + 3;
            }
            else if (annexB[i + 2] == 0x00 && i + 4 < annexB.Length && annexB[i + 3] == 0x01)
            {
                headerIndex = i + 4;
            }
            else
            {
                continue;
            }

            switch (annexB[headerIndex] & 0x1F)
            {
                case 7:
                    hasSequenceParameterSet = true;
                    break;
                case 5:
                    hasIdr = true;
                    break;
            }

            if (hasSequenceParameterSet && hasIdr)
            {
                break;
            }

            i = headerIndex;
        }

        return (hasSequenceParameterSet, hasIdr);
    }

    private static byte[] Concat(List<byte[]> parts)
    {
        var total = 0;
        foreach (var part in parts)
        {
            total += part.Length;
        }

        var combined = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(combined, offset);
            offset += part.Length;
        }

        return combined;
    }

    private void CreateTransform(int width, int height)
    {
        _width = width;
        _height = height;
        _nv12Buffer = new byte[Nv12Converter.FrameSize(width, height)];
        _lastSampleTime = -1;
        _sequenceHeader = null;

        _transform = CreateEncoderTransform();

        try
        {
            ConfigureCodecProperties();

            // Order matters and is not interchangeable: an encoder MFT can only work out which
            // input types it accepts once it knows what it is being asked to produce, so the output
            // type must be set before the input type.
            ApplyOutputType();
            ApplyInputType();

            ResolveStreamIds();

            _outputBufferSize = _transform.GetOutputStreamInfo(_outputStreamId).Size;

            _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        }
        catch
        {
            DestroyTransform();
            throw;
        }
    }

    /// <summary>
    /// An MFT whose streams have fixed IDs is allowed to answer GetStreamIDs with E_NOTIMPL, which
    /// is the normal case for a single-input/single-output encoder and simply means both IDs are 0.
    /// </summary>
    private void ResolveStreamIds()
    {
        var inputIds = new int[1];
        var outputIds = new int[1];

        try
        {
            _transform!.GetStreamIDs(1, inputIds, 1, outputIds);
            _inputStreamId = inputIds[0];
            _outputStreamId = outputIds[0];
        }
        catch (SharpGenException)
        {
            _inputStreamId = 0;
            _outputStreamId = 0;
        }
    }

    private static IMFTransform CreateEncoderTransform()
    {
        var outputInfo = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264
        };

        MediaFactory.MFTEnumEx(
            VideoEncoderCategory,
            EncoderEnumFlags,
            null,
            outputInfo,
            out var activatesPtr,
            out var count);

        if (count == 0 || activatesPtr == IntPtr.Zero)
        {
            throw new NotSupportedException(
                "This machine has no Media Foundation H.264 encoder. On Windows N/KN editions the " +
                "Media Feature Pack must be installed.");
        }

        try
        {
            // MFT_ENUM_FLAG_SORTANDFILTER put the preferred encoder first; the rest are only
            // enumerated because MFTEnumEx has no "just one" mode, and must still be released.
            IMFTransform? transform = null;
            for (var i = 0; i < count; i++)
            {
                using var activate = new IMFActivate(Marshal.ReadIntPtr(activatesPtr, i * IntPtr.Size));
                if (transform is not null)
                {
                    continue;
                }

                try
                {
                    activate.ActivateObject<IMFTransform>(typeof(IMFTransform).GUID, out var candidate);
                    transform = candidate;
                }
                catch (SharpGenException)
                {
                    // A registered-but-unusable encoder (a stale registration, a codec pack whose
                    // binary is gone) should fall through to the next candidate rather than take
                    // the live stream down.
                }
            }

            return transform ?? throw new NotSupportedException(
                "Every registered Media Foundation H.264 encoder on this machine failed to activate.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(activatesPtr);
        }
    }

    private void ConfigureCodecProperties()
    {
        // Best-effort: these are all optional tuning knobs, and an encoder that rejects one still
        // encodes correctly with its own default. Failing the whole live stream over a rejected
        // quality hint would be the wrong trade.
        using var attributes = _transform!.Attributes;
        TrySet(attributes, LowLatencyMode, 1);
        TrySet(attributes, AVEncCommonRateControlMode, RateControlModeCbr);
        TrySet(attributes, AVEncCommonMeanBitRate, (uint)_bitrate);
        TrySet(attributes, AVEncMPVGOPSize, (uint)(_frameRate * GopSeconds));
        // 0 = favour speed over quality. This encoder shares a machine with the exam itself and the
        // real recording pipeline; spending CPU to make a monitor thumbnail prettier is not worth it.
        TrySet(attributes, AVEncCommonQualityVsSpeed, 0);
    }

    private static void TrySet(IMFAttributes attributes, Guid key, uint value)
    {
        try
        {
            attributes.Set(key, value);
        }
        catch (SharpGenException)
        {
        }
    }

    private void ApplyOutputType()
    {
        using var outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
        outputType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)_bitrate).CheckError();
        outputType.Set(MediaTypeAttributeKeys.Mpeg2Profile, H264ProfileBaseline).CheckError();
        outputType.SetEnumValue(
            MediaTypeAttributeKeys.InterlaceMode, VideoInterlaceMode.Progressive).CheckError();
        outputType.Set(MediaTypeAttributeKeys.FrameSize, Pack(_width, _height)).CheckError();
        outputType.Set(MediaTypeAttributeKeys.FrameRate, Pack(_frameRate, 1)).CheckError();
        outputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1)).CheckError();

        _transform!.SetOutputType(_outputStreamId, outputType, 0);
    }

    private void ApplyInputType()
    {
        using var inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12).CheckError();
        inputType.SetEnumValue(
            MediaTypeAttributeKeys.InterlaceMode, VideoInterlaceMode.Progressive).CheckError();
        inputType.Set(MediaTypeAttributeKeys.FrameSize, Pack(_width, _height)).CheckError();
        inputType.Set(MediaTypeAttributeKeys.FrameRate, Pack(_frameRate, 1)).CheckError();
        inputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1)).CheckError();

        _transform!.SetInputType(_inputStreamId, inputType, 0);
    }

    // MF packs paired 32-bit values (width/height, numerator/denominator) into one 64-bit attribute,
    // high word first -- same helper VideoSinkWriterFactory needs for the same reason.
    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    private void DestroyTransform()
    {
        if (_transform is null)
        {
            return;
        }

        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
        }
        catch (SharpGenException)
        {
            // Teardown courtesy only -- an encoder that objects to being told the stream ended
            // still has to be released below.
        }

        _transform.Dispose();
        _transform = null;
        _nv12Buffer = null;
        _sequenceHeader = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DestroyTransform();

        if (_mediaFoundationAcquired)
        {
            MediaFoundationRuntime.Release();
            _mediaFoundationAcquired = false;
        }
    }
}
