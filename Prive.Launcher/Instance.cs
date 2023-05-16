namespace Prive.Launcher {
    public class Instance {
        public string GamePath { get; }

        public Instance(string gamePath) {
            GamePath = gamePath;
        }

        public void Launch() {
            throw new NotImplementedException();
        }
    }
}