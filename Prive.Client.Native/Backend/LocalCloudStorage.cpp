#include "LocalCloudStorage.h"
#include "../Core/Log.h"
#include "../Core/Paths.h"

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <bcrypt.h>

#include <algorithm>
#include <cctype>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <map>
#include <mutex>
#include <string_view>
#include <thread>
#include <vector>

#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "bcrypt.lib")

namespace fs = std::filesystem;

namespace {
    constexpr std::string_view PathPrefix = "/fortnite/api/cloudstorage/user/";
    constexpr size_t MaxHeaderBytes = 64 * 1024;
    constexpr size_t MaxBodyBytes = 16 * 1024 * 1024;
    constexpr DWORD SocketTimeoutMs = 10000;

    std::string PortValue;
    fs::path Root;
    std::mutex FileMutex;

    struct Request {
        std::string Method;
        std::string Path;
        std::map<std::string, std::string> Headers; // lower-case names
        std::string Body;

        std::string Header(const char* name) const {
            auto it = Headers.find(name);
            return it == Headers.end() ? std::string() : it->second;
        }
    };

    struct Response {
        int Status;
        const char* Reason;
        const char* ContentType;
        std::string Body;
    };

    // --- text helpers ------------------------------------------------------------------------

    std::string Lower(std::string text) {
        for (auto& c : text) c = (char)tolower((unsigned char)c);
        return text;
    }

    std::string Trim(std::string_view text) {
        size_t first = text.find_first_not_of(" \t");
        if (first == std::string_view::npos) return {};
        size_t last = text.find_last_not_of(" \t");
        return std::string(text.substr(first, last - first + 1));
    }

    std::string PercentDecode(std::string_view text) {
        std::string result;
        for (size_t i = 0; i < text.size(); i++) {
            if (text[i] == '%' && i + 2 < text.size() && isxdigit((unsigned char)text[i + 1]) && isxdigit((unsigned char)text[i + 2])) {
                result += (char)strtol(std::string(text.substr(i + 1, 2)).c_str(), nullptr, 16);
                i += 2;
            } else {
                result += text[i];
            }
        }
        return result;
    }

    std::string JsonString(std::string_view text) {
        std::string result = "\"";
        for (char c : text) {
            if (c == '"' || c == '\\') { result += '\\'; result += c; }
            else if ((unsigned char)c < 0x20) { char escape[8]; snprintf(escape, sizeof(escape), "\\u%04x", c); result += escape; }
            else result += c;
        }
        return result + "\"";
    }

    bool IsAccountId(std::string_view text) {
        return !text.empty() && text.size() <= 64 && std::all_of(text.begin(), text.end(), [](char c) { return isalnum((unsigned char)c); });
    }

    bool IsFilename(std::string_view text) {
        if (text.empty() || text.size() > 128 || text == "." || text == "..") return false;
        return std::all_of(text.begin(), text.end(), [](char c) { return isalnum((unsigned char)c) || c == '.' || c == '_' || c == '-'; });
    }

    // --- socket I/O ----------------------------------------------------------------------------

    bool Receive(SOCKET socket, std::string& buffer) {
        char chunk[8192];
        int read = recv(socket, chunk, sizeof(chunk), 0);
        if (read <= 0) return false;
        buffer.append(chunk, read);
        return true;
    }

    bool SendAll(SOCKET socket, std::string_view data) {
        while (!data.empty()) {
            int sent = send(socket, data.data(), (int)std::min<size_t>(data.size(), 1 << 20), 0);
            if (sent <= 0) return false;
            data.remove_prefix(sent);
        }
        return true;
    }

    bool ReadChunked(SOCKET socket, std::string& pending, std::string& body) {
        while (true) {
            size_t lineEnd;
            while ((lineEnd = pending.find("\r\n")) == std::string::npos) {
                if (pending.size() > MaxHeaderBytes || !Receive(socket, pending)) return false;
            }
            if (lineEnd == 0 || !isxdigit((unsigned char)pending[0])) return false;
            size_t size = strtoull(pending.substr(0, lineEnd).c_str(), nullptr, 16); // stops at ";ext"
            pending.erase(0, lineEnd + 2);
            if (size == 0) return true; // trailers, if any, are ignored
            if (size > MaxBodyBytes || body.size() + size > MaxBodyBytes) return false;
            while (pending.size() < size + 2) {
                if (!Receive(socket, pending)) return false;
            }
            body.append(pending, 0, size);
            pending.erase(0, size + 2);
        }
    }

    bool ReadRequest(SOCKET socket, Request& request) {
        std::string buffer;
        size_t headerEnd;
        while ((headerEnd = buffer.find("\r\n\r\n")) == std::string::npos) {
            if (buffer.size() > MaxHeaderBytes || !Receive(socket, buffer)) return false;
        }
        std::string_view head(buffer.data(), headerEnd);
        std::string pending = buffer.substr(headerEnd + 4);

        size_t lineEnd = head.find("\r\n");
        std::string_view requestLine = head.substr(0, lineEnd);
        size_t methodEnd = requestLine.find(' ');
        if (methodEnd == std::string_view::npos) return false;
        size_t targetEnd = requestLine.find(' ', methodEnd + 1); // raw space padding, if any, starts here
        request.Method = std::string(requestLine.substr(0, methodEnd));
        std::string target = PercentDecode(requestLine.substr(methodEnd + 1, targetEnd == std::string_view::npos ? std::string_view::npos : targetEnd - methodEnd - 1));

        for (size_t position = lineEnd == std::string_view::npos ? head.size() : lineEnd + 2; position < head.size();) {
            size_t end = head.find("\r\n", position);
            if (end == std::string_view::npos) end = head.size();
            std::string_view line = head.substr(position, end - position);
            size_t colon = line.find(':');
            if (colon != std::string_view::npos) request.Headers[Lower(Trim(line.substr(0, colon)))] = Trim(line.substr(colon + 1));
            position = end + 2;
        }

        bool chunked = Lower(request.Header("transfer-encoding")).find("chunked") != std::string::npos;
        size_t length = strtoull(request.Header("content-length").c_str(), nullptr, 10);
        if (length > MaxBodyBytes) return false;

        // libcurl asks before sending an upload body.
        bool bodyPending = chunked || pending.size() < length;
        if (bodyPending && Lower(request.Header("expect")) == "100-continue") SendAll(socket, "HTTP/1.1 100 Continue\r\n\r\n");

        if (chunked) {
            if (!ReadChunked(socket, pending, request.Body)) return false;
        } else {
            while (pending.size() < length) {
                if (!Receive(socket, pending)) return false;
            }
            pending.resize(length);
            request.Body = std::move(pending);
        }

        // The rewritten URL is space-padded to the original's length (CurlRedirect), and may carry
        // a query; neither is part of the path.
        size_t query = target.find('?');
        if (query != std::string::npos) target.resize(query);
        request.Path = Trim(target);
        return true;
    }

    // --- storage -------------------------------------------------------------------------------

    std::string HashHex(LPCWSTR algorithm, const std::string& data) {
        BCRYPT_ALG_HANDLE provider = nullptr;
        if (!BCRYPT_SUCCESS(BCryptOpenAlgorithmProvider(&provider, algorithm, nullptr, 0))) return {};
        DWORD length = 0, written = 0;
        BCryptGetProperty(provider, BCRYPT_HASH_LENGTH, (PUCHAR)&length, sizeof(length), &written, 0);
        std::vector<UCHAR> digest(length);
        NTSTATUS status = BCryptHash(provider, nullptr, 0, (PUCHAR)data.data(), (ULONG)data.size(), digest.data(), length);
        BCryptCloseAlgorithmProvider(provider, 0);
        if (!BCRYPT_SUCCESS(status)) return {};
        std::string hex;
        char byte[3];
        for (UCHAR b : digest) { snprintf(byte, sizeof(byte), "%02X", b); hex += byte; }
        return hex;
    }

    bool ReadWhole(const fs::path& path, std::string& data) {
        std::ifstream file(path, std::ios::binary);
        if (!file) return false;
        data.assign(std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>());
        return true;
    }

    std::string LastWriteUtc(const fs::path& path) {
        WIN32_FILE_ATTRIBUTE_DATA attributes;
        SYSTEMTIME time{ 1970, 1, 4, 1, 0, 0, 0, 0 };
        if (GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attributes)) FileTimeToSystemTime(&attributes.ftLastWriteTime, &time);
        char text[40];
        snprintf(text, sizeof(text), "%04d-%02d-%02dT%02d:%02d:%02d.%03dZ", time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute, time.wSecond, time.wMilliseconds);
        return text;
    }

    // Caller holds FileMutex. Seeds a new account from the game's own local copy (the launcher
    // swaps Saved and Saved.Prive, so look in both).
    fs::path AccountDirectory(const std::string& account) {
        std::error_code error;
        fs::path directory = Root / account;
        if (fs::exists(directory, error)) return directory;
        fs::create_directories(directory, error);
        for (const wchar_t* saved : { L"Saved", L"Saved.Prive" }) {
            fs::path cache = fs::path(Paths::LocalAppData()) / L"FortniteGame" / saved / L"Cloud" / account;
            if (!fs::is_directory(cache, error)) continue;
            for (const auto& entry : fs::directory_iterator(cache, error)) {
                if (entry.is_regular_file(error)) fs::copy_file(entry.path(), directory / entry.path().filename(), fs::copy_options::skip_existing, error);
            }
            Log::Info("LocalCloudStorage: seeded %s from %ls", account.c_str(), cache.c_str());
            break;
        }
        return directory;
    }

    // --- routes (the same answers as Prive.Server.Http's cloudstorage user routes) --------------

    Response Json(int status, const char* reason, std::string body) {
        return { status, reason, "application/json", std::move(body) };
    }

    Response Error(int status, const char* reason, const char* code, int numericCode, const std::string& message, const std::string& variable) {
        return Json(status, reason, "{\"errorCode\":" + JsonString(code) + ",\"errorMessage\":" + JsonString(message)
            + ",\"messageVars\":[" + JsonString(variable) + "],\"numericErrorCode\":" + std::to_string(numericCode)
            + ",\"originatingService\":\"fortnite\",\"intent\":\"prod-live\"}");
    }

    Response NotFound(const std::string& what) {
        return Error(404, "Not Found", "errors.com.epicgames.cloudstorage.file_not_found", 12004, "Sorry, we couldn't find a file for " + what, what);
    }

    Response MethodNotAllowed(const std::string& method) {
        return Error(405, "Method Not Allowed", "errors.com.epicgames.common.method_not_allowed", 1009,
            "Sorry the resource you were trying to access cannot be accessed with the HTTP method you used.", method);
    }

    Response ListFiles(const std::string& account) {
        std::lock_guard lock(FileMutex);
        std::error_code error;
        std::string body = "[";
        for (const auto& entry : fs::directory_iterator(AccountDirectory(account), error)) {
            if (!entry.is_regular_file(error) || entry.path().extension() == L".tmp") continue;
            std::string data;
            if (!ReadWhole(entry.path(), data)) continue;
            std::string name = entry.path().filename().string();
            if (body.size() > 1) body += ",";
            body += "{\"uniqueFilename\":" + JsonString(name) + ",\"filename\":" + JsonString(name)
                + ",\"hash\":\"" + HashHex(BCRYPT_SHA1_ALGORITHM, data) + "\",\"hash256\":\"" + HashHex(BCRYPT_SHA256_ALGORITHM, data)
                + "\",\"length\":" + std::to_string(data.size())
                + ",\"contentType\":\"application/octet-stream\",\"uploaded\":\"" + LastWriteUtc(entry.path())
                + "\",\"storageType\":\"S3\",\"doNotCache\":false}";
        }
        return Json(200, "OK", body + "]");
    }

    Response FileRequest(const Request& request, const std::string& account, const std::string& filename) {
        std::lock_guard lock(FileMutex);
        fs::path path = AccountDirectory(account) / filename;
        std::error_code error;
        if (request.Method == "GET") {
            std::string data;
            if (!ReadWhole(path, data)) return NotFound(filename);
            return { 200, "OK", "application/octet-stream", std::move(data) };
        }
        if (request.Method == "PUT") {
            // Written aside and moved over, so a crash mid-write never leaves half a file.
            fs::path temporary = path;
            temporary += L".tmp";
            {
                std::ofstream file(temporary, std::ios::binary | std::ios::trunc);
                if (!file.write(request.Body.data(), request.Body.size())) return Error(500, "Internal Server Error", "errors.com.epicgames.common.server_error", 1000, "Could not write " + filename, filename);
            }
            if (!MoveFileExW(temporary.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING)) {
                return Error(500, "Internal Server Error", "errors.com.epicgames.common.server_error", 1000, "Could not write " + filename, filename);
            }
            return { 204, "No Content", "application/octet-stream", {} };
        }
        if (request.Method == "DELETE") {
            fs::remove(path, error);
            return { 204, "No Content", "application/octet-stream", {} };
        }
        return MethodNotAllowed(request.Method);
    }

    Response Route(const Request& request) {
        if (request.Path.size() <= PathPrefix.size() || Lower(request.Path.substr(0, PathPrefix.size())) != PathPrefix) return NotFound(request.Path);
        std::string rest = request.Path.substr(PathPrefix.size());
        while (!rest.empty() && rest.back() == '/') rest.pop_back();
        size_t slash = rest.find('/');
        std::string account = rest.substr(0, slash);
        if (!IsAccountId(account)) return NotFound(request.Path);

        if (slash == std::string::npos) {
            return request.Method == "GET" ? ListFiles(account) : MethodNotAllowed(request.Method);
        }
        std::string filename = rest.substr(slash + 1);
        if (!IsFilename(filename)) return NotFound(filename);
        return FileRequest(request, account, filename);
    }

    // --- server --------------------------------------------------------------------------------

    // Runs inside the game: nothing may escape this function, or std::terminate takes the game down.
    void HandleClient(SOCKET client) {
        DWORD timeout = SocketTimeoutMs;
        setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, (const char*)&timeout, sizeof(timeout));
        setsockopt(client, SOL_SOCKET, SO_SNDTIMEO, (const char*)&timeout, sizeof(timeout));
        try {
            Request request;
            if (ReadRequest(client, request)) {
                Response response = Route(request);
                Log::Info("LocalCloudStorage: %s %s -> %d (%zu bytes)", request.Method.c_str(), request.Path.c_str(), response.Status, response.Body.size());
                char head[256];
                snprintf(head, sizeof(head), "HTTP/1.1 %d %s\r\nContent-Type: %s\r\nContent-Length: %zu\r\nConnection: close\r\n\r\n",
                    response.Status, response.Reason, response.ContentType, response.Body.size());
                if (SendAll(client, head)) SendAll(client, response.Body);
            }
        } catch (const std::exception& e) {
            Log::Error("LocalCloudStorage: request failed: %s", e.what());
        } catch (...) {
            Log::Error("LocalCloudStorage: request failed");
        }
        shutdown(client, SD_SEND);
        closesocket(client);
    }

    void Serve(SOCKET listener) {
        while (true) {
            SOCKET client = accept(listener, nullptr, nullptr);
            if (client == INVALID_SOCKET) {
                Sleep(50);
                continue;
            }
            try {
                std::thread(HandleClient, client).detach();
            } catch (...) {
                closesocket(client); // could not start a thread: drop the request, keep serving
            }
        }
    }
}

namespace LocalCloudStorage {
    bool Start() {
        if (Paths::LauncherDirectory().empty()) return false;
        Root = fs::path(Paths::LauncherDirectory()) / L"CloudStorage";
        std::error_code error;
        fs::create_directories(Root, error);

        WSADATA wsa;
        if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) {
            Log::Error("LocalCloudStorage: WSAStartup failed - settings go to the backend");
            return false;
        }
        SOCKET listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        address.sin_port = 0; // any free port
        int addressLength = sizeof(address);
        if (listener == INVALID_SOCKET
            || bind(listener, (sockaddr*)&address, sizeof(address)) == SOCKET_ERROR
            || listen(listener, SOMAXCONN) == SOCKET_ERROR
            || getsockname(listener, (sockaddr*)&address, &addressLength) == SOCKET_ERROR) {
            Log::Error("LocalCloudStorage: cannot listen (%d) - settings go to the backend", WSAGetLastError());
            if (listener != INVALID_SOCKET) closesocket(listener);
            return false;
        }
        try {
            std::thread(Serve, listener).detach();
        } catch (...) {
            closesocket(listener);
            Log::Error("LocalCloudStorage: cannot start its thread - settings go to the backend");
            return false;
        }
        PortValue = std::to_string(ntohs(address.sin_port));
        Log::Info("LocalCloudStorage: serving user cloud storage on 127.0.0.1:%s from %ls", PortValue.c_str(), Root.c_str());
        return true;
    }

    const std::string& Port() { return PortValue; }
}
