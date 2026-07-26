namespace VoxOralExam.DesktopApp.Infra.WebRtc.VideoEncoding;

/// <summary>
/// Packs the capture sources' interleaved BGR frames into the planar NV12 layout the H.264 encoder
/// MFT takes as input (Y plane of width*height bytes, then a half-height plane of interleaved
/// U,V pairs subsampled 2x2).
///
/// This exists because the two capture sources hand MonitorStreamClient BGR-family buffers
/// (LiveMonitorStreamService pushes Bgr from the camera's OpenCV Mat and Bgra from the screen
/// capture's D3D texture) but no Microsoft H.264 encoder MFT accepts an RGB input type -- NV12 is
/// the one input format every H.264 encoder MFT on Windows is required to support, so it is what
/// the encoder converts to rather than negotiating per machine.
///
/// The alternative was instantiating the Video Processor MFT to do the conversion, which means a
/// second MFT to configure, drive and keep in sync with the encoder for a transform that is one
/// pass over the pixels. VideoSegmentWriter.WriteBgr24 already does its own pixel-format packing by
/// hand for the same reason.
/// </summary>
internal static class Nv12Converter
{
    /// <summary>
    /// NV12 subsamples chroma 2x2, so both dimensions must be even for the chroma plane to have a
    /// whole number of samples. Capture sizes are not guaranteed even (a window-sized screen capture
    /// or an odd camera mode both produce odd dimensions), so the encoder is configured for the
    /// rounded-down even size and this drops the leftover last row/column.
    /// </summary>
    public static int RoundDownToEven(int value) => value & ~1;

    /// <summary>Byte length of an NV12 frame: a full-size Y plane plus a quarter-size UV plane.</summary>
    public static int FrameSize(int width, int height) => width * height * 3 / 2;

    /// <summary>
    /// Converts <paramref name="source"/> into <paramref name="destination"/>, which must be at
    /// least <see cref="FrameSize"/> bytes. <paramref name="width"/>/<paramref name="height"/> are
    /// the (even) output dimensions and may be smaller than the source frame, in which case the
    /// frame is cropped from the top-left rather than scaled -- the encoder is created from the
    /// first frame's dimensions, so this only ever trims the odd last row/column.
    /// </summary>
    public static void Convert(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int bytesPerPixel,
        Span<byte> destination,
        int width,
        int height)
    {
        if (bytesPerPixel is not (3 or 4))
        {
            throw new ArgumentOutOfRangeException(
                nameof(bytesPerPixel), bytesPerPixel, "Only BGR24 and BGRA32 input is supported.");
        }

        if (sourceWidth < width || sourceHeight < height)
        {
            throw new ArgumentException(
                $"Source frame {sourceWidth}x{sourceHeight} is smaller than the {width}x{height} output.",
                nameof(source));
        }

        var sourceStride = sourceWidth * bytesPerPixel;
        if (source.Length < sourceStride * sourceHeight)
        {
            throw new ArgumentException("Source buffer is shorter than its own stride*height.", nameof(source));
        }

        if (destination.Length < FrameSize(width, height))
        {
            throw new ArgumentException("Destination buffer is too small for an NV12 frame.", nameof(destination));
        }

        var uvPlaneOffset = width * height;

        // BT.601 studio-swing coefficients in Q8 fixed point, matching the MF encoder's default
        // MF_MT_YUV_MATRIX for standard-definition frame sizes. Integer math here is not just a
        // speed choice: this runs per pixel on every streamed frame on the single video worker
        // thread, and a float path measurably eats into the encode budget at 30fps.
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * sourceStride;
            var yRowStart = y * width;

            // Chroma is written once per 2x2 block, sampled from that block's top-left pixel.
            // Averaging all four would be marginally cleaner but doubles the per-pixel work for a
            // difference no one can see in a monitor thumbnail.
            var isChromaRow = (y & 1) == 0;
            var uvRowStart = uvPlaneOffset + y / 2 * width;

            for (var x = 0; x < width; x++)
            {
                var pixel = rowStart + x * bytesPerPixel;
                int b = source[pixel];
                int g = source[pixel + 1];
                int r = source[pixel + 2];

                destination[yRowStart + x] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);

                if (isChromaRow && (x & 1) == 0)
                {
                    var uv = uvRowStart + x;
                    destination[uv] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                    destination[uv + 1] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                }
            }
        }
    }
}
