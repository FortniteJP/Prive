namespace Prive.Launcher;

public class ServerWindow : Window {
    public ServerWindow() : base("Prive Server") {
        Console.Title = "Prive Server";
        ColorScheme = Utils.DefaultColorScheme;
        Add(new Label() {
            Text = "Server is not implemented yet.",
            X = 1, Y = 0
        });
    }
}