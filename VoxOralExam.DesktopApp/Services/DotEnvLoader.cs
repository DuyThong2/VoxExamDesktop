using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace VoxOralExam.DesktopApp.Services;

/// <summary>
/// Minimal .env file loader (KEY=VALUE per line, '#' comments, blank lines ignored). Sets process
/// environment variables so all of AppSettings can live outside appsettings.json -- which is
/// committed to git -- the same way agents/.env keeps secrets out of the Python side's source
/// control. No NuGet dependency needed for a format this simple.
/// </summary>
public static class DotEnvLoader
{
    public static void Load(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim().Trim('"');
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    /// <summary>
    /// Overrides every writable property on <paramref name="settings"/> from an environment
    /// variable named after the property in UPPER_SNAKE_CASE (e.g. JavaBaseUrl -> JAVA_BASE_URL,
    /// S3BucketName -> S3_BUCKET_NAME). Only applies when the env var is actually set, so
    /// appsettings.json / AppSettings.cs's own defaults still win for anyone without a .env yet.
    /// Reflection-based on purpose: AppSettings.cs already has ~20 fields and grows over time --
    /// this stays correct without a matching hand-written mapping line per field.
    /// </summary>
    public static void ApplyOverrides(object settings)
    {
        foreach (var property in settings.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite)
            {
                continue;
            }

            var envName = ToUpperSnakeCase(property.Name);
            var raw = Environment.GetEnvironmentVariable(envName);
            if (raw is null)
            {
                continue;
            }

            try
            {
                var converted = Convert.ChangeType(raw, property.PropertyType, CultureInfo.InvariantCulture);
                property.SetValue(settings, converted);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                LocalFileLogger.Error("dotenv", "override_value_invalid", ex, new { property = property.Name, envName, raw });
            }
        }
    }

    private static string ToUpperSnakeCase(string pascalCase)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < pascalCase.Length; i++)
        {
            var c = pascalCase[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(pascalCase[i - 1]))
            {
                builder.Append('_');
            }
            builder.Append(char.ToUpperInvariant(c));
        }
        return builder.ToString();
    }
}
