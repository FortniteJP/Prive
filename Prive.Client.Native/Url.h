#include <algorithm>
#include <string>
#include <sstream>
#include <string_view>

struct Url {
    private:
        typedef std::string_view::const_iterator iterator_t;

        static constexpr std::string_view MakeStringView(const std::string_view& base, iterator_t first, iterator_t last) {
            return base.substr(std::distance(base.begin(), first), std::distance(first, last));
        }

    public:
        std::string_view Protocol, Host, Port, Path, QueryString;

        static Url Parse(const std::string_view& url) {
            Url result;

            if (url.empty()) return result;

            iterator_t urlEnd = url.end();
            iterator_t queryStart = std::find(url.begin(), urlEnd, '?');
            iterator_t protocolStart = url.begin();
            iterator_t protocolEnd = std::find(protocolStart, urlEnd, ':');

            if (protocolEnd != urlEnd) {
                std::string_view prot = &*(protocolEnd);
                if ((prot.length() > 3) && (prot.substr(0, 3) == "://")) {
                    result.Protocol = std::string_view(&*protocolStart, &*(protocolEnd));
                    protocolEnd += 3;
                }
                else protocolEnd = url.begin();
            }
            else protocolEnd = url.begin();

            iterator_t hostStart = protocolEnd;
            iterator_t pathStart = std::find(hostStart, urlEnd, '/');
            iterator_t hostEnd = std::find(protocolEnd, (pathStart != urlEnd) ? pathStart : queryStart, ':');

            result.Host = MakeStringView(url, hostStart, hostEnd);

            if ((hostEnd != urlEnd) && ((&*(hostEnd))[0] == ':')) {
                hostEnd++;
                iterator_t portEnd = (pathStart != urlEnd) ? pathStart : queryStart;
                result.Port = MakeStringView(url, hostEnd, portEnd);
            }

            if (pathStart != urlEnd) result.Path = MakeStringView(url, pathStart, queryStart);
            if (queryStart != urlEnd) result.QueryString = MakeStringView(url, queryStart, url.end());

            return result;
        }

        static std::string CreateUrl(std::string_view protocol, std::string_view host, std::string_view port, std::string_view path, std::string_view queryString) {
            std::ostringstream str;
            if (!protocol.empty()) {
                str.write(protocol.data(), protocol.size());
                str.write("://", 3);
            }
            str.write(host.data(), host.size());
            if (!port.empty()) {
                str.write(":", 1);
                str.write(port.data(), port.size());
            }
            if (!path.empty()) {
                if (path[0] != '/') str.write("/", 1);
                str.write(path.data(), path.size());
            }
            if (!queryString.empty()) {
                if (queryString[0] != '?') str.write("?", 1);
                str.write(queryString.data(), queryString.size());
            }
            return str.str();
        }
};