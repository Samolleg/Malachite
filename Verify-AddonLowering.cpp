// Private headless differential harness; not part of the application/package.
#include "MobIfElse.h"
#include <filesystem>
#include <fstream>
#include <iterator>
#include <iostream>
int main(int argc, char** argv) {
    if(argc != 2) return 2;
    for(const auto& item: std::filesystem::directory_iterator(argv[1])) {
        if(item.path().extension() != ".input") continue;
        std::ifstream in(item.path(),std::ios::binary);
        std::string text((std::istreambuf_iterator<char>(in)),{});
        auto result=MobIfElse::Lower(text);
        auto output=item.path(); output.replace_extension(".native");
        std::ofstream(output,std::ios::binary)<<result.text;
        auto meta=item.path(); meta.replace_extension(".native-meta");
        std::ofstream(meta)<<result.changed<<"\n"<<!result.error.empty()<<"\n"<<result.line<<"\n"<<result.error;
    }
    return 0;
}
