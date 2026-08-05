using System.Globalization;
using System.Windows.Data;

namespace VoxOralExam.DesktopApp.Converters;

/// <summary>
/// Renders a server timestamp in the student machine's own timezone.
///
/// <para>Java gửi mọi mốc thời gian dưới dạng ISO-8601 UTC kết thúc bằng "Z" (Instant.toString()).
/// Bind thẳng bằng StringFormat sẽ in ra đúng giờ UTC đó -- ở Việt Nam là sớm hơn 7 tiếng. Vì vậy
/// phải gọi ToLocalTime() trước khi format.</para>
///
/// <para>Culture cố ý là InvariantCulture chứ không phải <paramref name="culture"/> của binding:
/// pattern đã là định dạng ngày kiểu Việt Nam, và máy cài lịch không phải Gregorian (Thai Buddhist,
/// Hijri, ...) sẽ in ra năm sai nếu dùng culture của máy.</para>
/// </summary>
public class LocalDateTimeConverter : IValueConverter
{
    private const string DefaultFormat = "dd/MM/yyyy HH:mm";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTimeOffset moment
            ? moment.ToLocalTime().ToString(
                parameter as string ?? DefaultFormat,
                CultureInfo.InvariantCulture)
            : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
