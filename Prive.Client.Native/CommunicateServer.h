#include <iostream>
#include <winsock2.h>
#include <thread>
#include <regex>

#pragma comment(lib, "ws2_32.lib")

std::string ToLower(std::string str) {
    std::string result = "";
    std::transform(str.begin(), str.end(), std::back_inserter(result), ::tolower);
    return result;
}

#define Pattern(x) std::regex P##x(ToLower(#x) + ";" + ".?");
#define PatternA(x, y) std::regex P##x(ToLower(#x) + ";" + #y + ".?");

PatternA(Outfit, (.*?);(.*?))

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

bool start_with(const std::string& s, const std::string& prefix) {
    if (s.size() < prefix.size()) return false;
    return std::equal(std::begin(prefix), std::end(prefix), std::begin(s));
}

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
    serverAddress.sin_port = htons(12346);
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
    std::cout << "Listening on port " << 12346 << std::endl;

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
        std::string message;
        std::smatch matches;

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

            message.clear();
            message = std::string(buffer, bytesRead);
            std::cout << "Received " << bytesRead << " bytes." << std::endl;
            std::cout << "`" << message << "`" << std::endl;
            // MessageBoxA(nullptr, message.c_str(), "Message", MB_OK);

            if (std::regex_match(message, matches, POutfit)) {
                std::string id = matches[1].str();
                std::string cid = matches[2].str();
                MessageBoxA(nullptr, ("Outfit ID `" + id + "`, CID `" + cid + "`").c_str(), "Message", MB_OK);
            } else {
                MessageBoxA(nullptr, message.c_str(), "Message", MB_OK);
            }

        }
    }
}