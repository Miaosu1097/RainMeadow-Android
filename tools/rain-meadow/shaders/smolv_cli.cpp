#include "smolv.h"
#include <fstream>
#include <iterator>
#include <string>
#include <vector>
#include <iostream>

int main(int argc, char** argv) {
    if (argc != 4) return 2;
    std::ifstream input(argv[2], std::ios::binary);
    if (!input) return 3;
    std::vector<unsigned char> data((std::istreambuf_iterator<char>(input)), {}), output;
    if (std::string(argv[1]) == "decode") {
        auto size = smolv::GetDecodedBufferSize(data.data(), data.size());
        if (!size || size > 64 * 1024 * 1024) return 4;
        output.resize(size);
#ifdef MODERN_SMOLV
        if (!smolv::Decode(data.data(), data.size(), output.data(), size, smolv::kDecodeFlagUse20160831AsZeroVersion)) return 5;
#else
        if (!smolv::Decode(data.data(), data.size(), output.data(), size)) return 5;
#endif
    } else if (std::string(argv[1]) == "encode") {
        if (!smolv::Encode(data.data(), data.size(), output, 0)) return 6;
    } else return 2;
    std::ofstream file(argv[3], std::ios::binary);
    file.write(reinterpret_cast<const char*>(output.data()), output.size());
    return file ? 0 : 7;
}
