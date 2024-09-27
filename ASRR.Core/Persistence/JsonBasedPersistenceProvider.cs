using Newtonsoft.Json;
using NLog;
using System.Reflection;

namespace ASRR.Core.Persistence;

public class JsonBasedPersistenceProvider : IPersistentStorageProvider
{
    public const string ProgramDataPath = @"C:\ProgramData\ASRR\Storage";
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly string _path;

    public JsonBasedPersistenceProvider(string directory)
    {
        _path = Path.Combine(ProgramDataPath, directory);
        Directory.CreateDirectory(_path);
    }

    public T Fetch<T>() where T : class, new()
    {
        var filePath = FilePath<T>();
        if (!File.Exists(filePath))
        {
            File.WriteAllText(filePath, JsonConvert.SerializeObject(new T(), Formatting.Indented));
            Log.Info("Settings don't exist... creating new file");
        }



        var deserializedObject = JsonConvert.DeserializeObject<T>(File.ReadAllText(filePath));

        if (HasNullProperties(deserializedObject))
            Log.Warn($"File at path '{filePath}' contains null properties");

        return deserializedObject;
    }

    public bool Persist<T>(T toStore) where T : class
    {
        var filePath = FilePath<T>();
        Log.Info($"Persisting {typeof(T).Name} to file at path '{filePath}'");
        File.WriteAllText(filePath, JsonConvert.SerializeObject(toStore, Formatting.Indented));
        return true;
    }

    public void Open<T>() where T : class
    {
        // open filepath using default program
        var filePath = FilePath<T>();
        Log.Info($"Opening file at path '{filePath}'");
        System.Diagnostics.Process.Start(@filePath);
    }

    private string FilePath<T>() where T : class
    {
        return Path.Combine(_path, $"{FileName<T>()}.json");
    }

    private string FileName<T>() where T : class
    {
        return typeof(T).Name;
    }

    private bool HasNullProperties<T>(T obj)
    {
        var type = obj.GetType();
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        return properties.Select(x => x.GetValue(obj, null))
            .Any(y => y == null);
    }
}
