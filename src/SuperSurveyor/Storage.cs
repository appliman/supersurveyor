using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SuperSurveyor;

[SupportedOSPlatform("linux")]
internal static class Storage
{
    internal static T? Read<T>(string path, JsonTypeInfo<T> type)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var result = JsonSerializer.Deserialize(stream, type)
                ?? throw new InvalidDataException("The data file is empty.");
            return result;
        }
        catch (FileNotFoundException)
        {
            return default;
        }
        catch (DirectoryNotFoundException)
        {
            return default;
        }
    }

    internal static void Write<T>(string path, T value, JsonTypeInfo<T> type)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            }))
            {
                JsonSerializer.Serialize(stream, value, type);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}

[JsonSerializable(typeof(FreeMobilePayload))]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(MonitorState))]
internal sealed partial class SurveyorJson : JsonSerializerContext
{
}
