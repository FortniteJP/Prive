// From ConsoleGUI.MouseExample/MouseHandler.cs

#pragma warning disable CS0649

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ConsoleGUI;

namespace Prive.Launcher {
    public static class MouseHandler {
        private static IntPtr InputHandle = IntPtr.Zero;
        private static InputRecord[] InputBuffer = default!;

        [SupportedOSPlatform("Windows")]
        public static void Initialize() {
            InputHandle = GetStdHandle(unchecked((uint)-10));
            InputBuffer = new InputRecord[100];
        }

        public static void ReadMouseEvents() {
            if (InputHandle == IntPtr.Zero) throw new InvalidOperationException("MouseHandler is not initialized");
            if (!ReadConsoleInput(InputHandle, InputBuffer, (uint)InputBuffer.Length, out var eventsRead)) return;

            for (var i = 0; i < eventsRead; i++) {
                var inputEvent = InputBuffer[i];

                if ((inputEvent.EventType & 0x0002) != 0) ProcessMouseEvent(inputEvent.MouseEvent);
                else WriteConsoleInput(InputHandle, new[] { inputEvent }, 1, out var eventsWritten);
            }
        }

        private static void ProcessMouseEvent(in MouseRecord mouseEvent) {
            ConsoleManager.MousePosition = new(mouseEvent.MousePosition.X, mouseEvent.MousePosition.Y);
            ConsoleManager.MouseDown = (mouseEvent.ButtonState & 0x0001) != 0;
        }

        private struct COORD {
            public short X;
            public short Y;
        }

        private struct MouseRecord {
            public COORD MousePosition;
            public uint ButtonState;
            public uint ControlKeyState;
            public uint EventFlags;
        }

        private struct InputRecord {
            public ushort EventType;
            public MouseRecord MouseEvent;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(uint nStdHandle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool ReadConsoleInput(IntPtr hConsoleInput, [Out] InputRecord[] lpBuffer, uint nLength, out uint lpNumberOfEventsRead);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool WriteConsoleInput(IntPtr hConsoleInput, InputRecord[] lpBuffer, uint nLength, out uint lpNumberOfEventsWritten);
    }
}