using System.Text.Json;

namespace Prive.Launcher;

public static class Configurations {
    public static readonly string ConfigurationsLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Launcher", "configurations");

    public static List<string> GetConfigurations() => Directory.GetFiles(ConfigurationsLocation, "*.json").ToList();

    public static Configuration GetConfiguration(string fileName = "Default.json") {
        var path = Path.Combine(ConfigurationsLocation, fileName);
        if (!Directory.Exists(ConfigurationsLocation)) Directory.CreateDirectory(ConfigurationsLocation);
        if (!File.Exists(path)) {
            var config = new Configuration();
            SaveConfiguration(config);
            return config;
        }
        return JsonSerializer.Deserialize<Configuration>(File.ReadAllText(path)) ?? throw new NullReferenceException();
    }

    public static void SaveConfiguration(Configuration configuration) => SaveConfiguration("Default.json", configuration);

    public static void SaveConfiguration(string fileName, Configuration configuration) {
        var path = Path.Combine(ConfigurationsLocation, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(configuration));
    }
}

public class Configuration {
    public string GamePath { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
