using System;
using System.Net.Sockets;
using System.Text;

public class Program {
    public static void Main() {
        string serverIP = "127.0.0.1";
        int serverPort = 12345;

        try {
            TcpClient client = new TcpClient(serverIP, serverPort);
            Console.WriteLine("Connected to server.");

            string message = "Hello from C# client!";
            byte[] data = Encoding.ASCII.GetBytes(message);
            NetworkStream stream = client.GetStream();
            stream.Write(data, 0, data.Length);
            Console.WriteLine("Sent message: " + message);

            data = new byte[1024];
            int bytesRead = stream.Read(data, 0, data.Length);
            string response = Encoding.ASCII.GetString(data, 0, bytesRead);
            Console.WriteLine("Received response: " + response);

            stream.Close();
            client.Close();
        } catch (Exception e) {
            Console.WriteLine("Error: " + e.Message);
        }

        Console.WriteLine("Press Enter to exit...");
        Console.ReadLine();
    }
}