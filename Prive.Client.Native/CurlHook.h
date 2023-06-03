#include "Curl/curl.h"
#include "Url.h"

CURLcode (*OCurlSetOpt)(struct Curl_easy*, CURLoption, va_list) = nullptr;
CURLcode (*OCurlEasySetOpt)(struct Curl_easy*, CURLoption, ...) = nullptr;

CURLcode CurlSetOpt(struct Curl_easy *data, CURLoption option, ...) {
    va_list args;
    va_start(args, option);
    CURLcode result = OCurlSetOpt(data, option, args);
    va_end(args);
    return result;
}

CURLcode CurlEasySetOptDetour(struct Curl_easy* data, CURLoption tag, ...) {
    va_list args;
    va_start(args, tag);
    CURLcode result;

    if (!data) return CURLE_BAD_FUNCTION_ARGUMENT;

    if (tag == CURLOPT_SSL_VERIFYPEER) {
        result = CurlSetOpt(data, tag, 0);
    } else if (tag == CURLOPT_URL) {
        std::string url = va_arg(args, char*);
        // printf("URL: %s\n", url.c_str());
        size_t length = url.length();

        Url parsed = Url::Parse(url);
        if (parsed.Host.ends_with(".ol.epicgames.com")) {
            url = Url::CreateUrl("http", "localhost", "8000", parsed.Path, parsed.QueryString);
        }
        if (url.length() < length) {
            url.append(length - url.length(), ' ');
        }
        CurlSetOpt(data, CURLOPT_SSL_VERIFYPEER, 0);
        result = CurlSetOpt(data, tag, url.c_str());
    } else {
        result = OCurlSetOpt(data, tag, args);
    }

    va_end(args);
    return result;
}