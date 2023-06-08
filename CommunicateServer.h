#include <iostream>
#include <winsock2.h>
#include <thread>

#pragma comment(lib, "ws2_32.lib")

class CommunicateServer {
    public:
        CommunicateServer();
        ~CommunicateServer();
        bool Start();
        bool Stop();
    private:
        void HandleConnection();
        SOCKET ServerSocket;
        bool IsRunning;
};

CommunicateServer::CommunicateServer() : ServerSocket(INVALID_SOCKET), IsRunning(false) {}

CommunicateServer::~CommunicateServer() { Stop(); }

bool CommunicateServer::Start() {
    ServerSocket = socket(AF_INET, SOCK_STREAM, IPPROTO_HOPOPTS);
    if (ServerSocket == INVALID_SOCKET) {
        std::cout << "Failed to create socket" << std::endl;
        return false;
    }

    u_long mode = 1;
    if (ioctlsocket(ServerSocket, FIONBIO, &mode) == SOCKET_ERROR) {
        std::cout << "Failed to set socket to non-blocking" << std::endl;
        return false;
    }

    sockaddr_in serverAddress;
    serverAddress.sin_family = AF_INET;
    serverAddress.sin_addr.s_addr = inet_addr("127.0.0.1"); // INADDR_ANY
    serverAddress.sin_port = htons(12345);
    if (bind(ServerSocket, (sockaddr*)&serverAddress, sizeof(serverAddress)) == SOCKET_ERROR) {
        std::cout << "Failed to bind socket" << std::endl;
        return false;
    }

    if (listen(ServerSocket, SOMAXCONN) == SOCKET_ERROR) {
        std::cout << "Failed to listen on socket" << std::endl;
        closesocket(ServerSocket);
        return false;
    }

    IsRunning = true;
    std::cout << "Listening on port " << 12345 << std::endl;

    std::thread t(&CommunicateServer::HandleConnection, this);
    t.detach();

    return true;
}

bool CommunicateServer::Stop() {
    IsRunning = false;
    if (ServerSocket != INVALID_SOCKET) {
        closesocket(ServerSocket);
        ServerSocket = INVALID_SOCKET;
    }
    return true;
}

void CommunicateServer::HandleConnection() {
    while (IsRunning) {
        SOCKET clientSocket = accept(ServerSocket, nullptr, nullptr);
        if (clientSocket == INVALID_SOCKET) {
            // this does spam
            // std::cout << "Accept failed " << WSAGetLastError() << std::endl;
            continue;
        }

        u_long mode = 1;
        if (ioctlsocket(clientSocket, FIONBIO, &mode) == SOCKET_ERROR) {
            std::cout << "Failed to set socket to non-blocking" << std::endl;
            continue;
        }

        const int bufferSize = 1024;
        char buffer[bufferSize];

        while (true) {
            int bytesRead = recv(clientSocket, buffer, bufferSize, 0);
            if (bytesRead == SOCKET_ERROR) {
                int error = WSAGetLastError();
                if (error == WSAEWOULDBLOCK) continue;
                std::cout << "Recv failed " << error << std::endl;
                break;
            } else if (bytesRead == 0) {
                std::cout << "Client disconnected." << std::endl;
                break;
            }

            std::cout << "Received " << bytesRead << " bytes." << std::endl;
            std::cout << buffer << std::endl;
            MessageBoxA(nullptr, buffer, "Message", MB_OK);

        }
    }
}