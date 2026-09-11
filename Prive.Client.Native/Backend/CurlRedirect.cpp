#include "CurlRedirect.h"
#include "HostConfig.h"
#include "LocalCloudStorage.h"
#include "Url.h"
#include "Curl/curl.h"
#include "../Core/GuardHook.h"
#include "../Core/Log.h"
#include "../Unreal/Addresses.h"

#include <cstdarg>
#include <string>

struct Curl_easy; // opaque - curl.h only names it when building libcurl itself

namespace {
    using VSetOptFn = CURLcode(__fastcall*)(Curl_easy* data, CURLoption option, va_list args);
    VSetOptFn VSetOpt = nullptr;

    CURLcode SetOpt(Curl_easy* data, CURLoption option, ...) {
        va_list args;
        va_start(args, option);
        CURLcode result = VSetOpt(data, option, args);
        va_end(args);
        return result;
    }

    // Replaces curl_easy_setopt outright - never calls through, it goes to Curl_vsetopt itself.
    CURLcode EasySetOptDetour(Curl_easy* data, CURLoption option, ...) {
        if (!data) return CURLE_BAD_FUNCTION_ARGUMENT;

        va_list args;
        va_start(args, option);
        CURLcode result;

        if (option == CURLOPT_SSL_VERIFYPEER) {
            result = SetOpt(data, option, 0L);
        } else if (option == CURLOPT_URL) {
            std::string url = va_arg(args, char*);
            size_t length = url.length();

            Url parsed = Url::Parse(url);
            if (parsed.Host.ends_with(".epicgames.com")) {
                // The game's per-user files (its settings) stay on this machine - LocalCloudStorage,
                // in this DLL; everything else goes to the backend.
                bool userCloudStorage = parsed.Path.starts_with("/fortnite/api/cloudstorage/user/");
                if (userCloudStorage && !LocalCloudStorage::Port().empty()) {
                    url = Url::CreateUrl("http", "127.0.0.1", LocalCloudStorage::Port(), parsed.Path, parsed.QueryString);
                } else {
                    url = Url::CreateUrl(HostConfig::Scheme(), HostConfig::Host(), HostConfig::Port(), parsed.Path, parsed.QueryString);
                }
            } else {
                Log::Info("URL: %s", url.c_str());
            }
            // Carried over unchanged from the original hook: a rewritten URL is space-padded to at
            // least the length of the one the game passed.
            if (url.length() < length) url.append(length - url.length(), ' ');

            SetOpt(data, CURLOPT_SSL_VERIFYPEER, 0L);
            result = SetOpt(data, option, url.c_str());
        } else {
            result = VSetOpt(data, option, args);
        }

        va_end(args);
        return result;
    }
}

namespace CurlRedirect {
    bool Install() {
        VSetOpt = (VSetOptFn)Memory::Resolve(Addresses::CurlVSetOpt);
        auto easySetOpt = Memory::Resolve(Addresses::CurlEasySetOpt);
        if (!VSetOpt || !easySetOpt) {
            Log::Error("CurlRedirect: not installed - the game will talk to Epic directly");
            return false;
        }
        return GuardHook::Install("curl_easy_setopt", (void*)easySetOpt, (void*)&EasySetOptDetour);
    }
}
