public class SettingsWindow : Window {
    public SettingsWindow() : base("Prive") {
        Console.Title = "Prive Settings";
        ColorScheme.Normal = new(Color.BrightMagenta, Color.Black);
        Add(
            new Label() {
                Text = "Settings!",
                X = 1
            }
        );
    }
}