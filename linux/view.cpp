#include <webview.h>

#include <filesystem>
#include <fstream>
#include <sstream>
#include <string>

namespace fs = std::filesystem;


std::string readFile(const std::string& path) {

    std::ifstream file(path);

    if (!file.is_open()) {
        return "";
    }

    std::stringstream buffer;
    buffer << file.rdbuf();

    return buffer.str();
}


bool writeFile(const std::string& path, const std::string& content) {

    std::ofstream file(path);

    if (!file.is_open()) {
        return false;
    }

    file << content;

    return true;
}


int main() {

    webview::webview w(true, nullptr);

    w.set_title("$title");
    w.set_size(1280, 720, WEBVIEW_HINT_NONE);


    // Read file
    w.bind("readFile",
        [](const std::string& path) -> std::string {

            return readFile(path);

        });


    // Write file
    w.bind("writeFile",
        [](const std::string& req) -> std::string {

            auto split = req.find('|');

            if (split == std::string::npos) {
                return R"({"success":false})";
            }


            std::string path =
                req.substr(0, split);

            std::string content =
                req.substr(split + 1);


            bool result =
                writeFile(path, content);


            return result
                ? R"({"success":true})"
                : R"({"success":false})";
        });



    // Platform
    w.bind("getPlatform",
        [](const std::string&) -> std::string {

#ifdef _WIN32
            return "windows";
#elif __linux__
            return "linux";
#elif __APPLE__
            return "macos";
#else
            return "unknown";
#endif

        });



    // Directory listing
    w.bind("listDir",
        [](const std::string& dir) -> std::string {

            std::string result = "[";

            bool first = true;


            try {

                for (auto const& entry :
                    fs::directory_iterator(dir)) {


                    if (!first) {
                        result += ",";
                    }


                    result += "\"";
                    result +=
                        entry.path()
                        .filename()
                        .string();
                    result += "\"";


                    first = false;
                }

            }
            catch (...) {

                return "[]";

            }


            result += "]";

            return result;

        });



    w.init(R"(

window.Vader = {

    fs: {

        readFile(path) {
            return readFile(path);
        },


        writeFile(path, content) {

            return JSON.parse(
                writeFile(
                    path + "|" + content
                )
            );

        },


        listDir(path) {

            return JSON.parse(
                listDir(path)
            );

        }

    },


    os: {

        platform() {

            return getPlatform();

        }

    }

};

)");



    std::string path =
        "file://" +
        fs::absolute("$filePath").string();


    w.navigate(path);


    w.run();


    return 0;
}