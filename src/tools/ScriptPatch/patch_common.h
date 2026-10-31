#pragma once
#include <windows.h>
#include <bcrypt.h>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>
#include <stdexcept>
#include <algorithm>

namespace rika {
namespace fs = std::filesystem;
inline fs::path ModulePath(HMODULE module = nullptr) {
    wchar_t path[32768]{}; auto size = GetModuleFileNameW(module, path, 32768);
    if (!size || size >= 32768) throw std::runtime_error("module");
    return fs::path(path);
}
inline bool PlainFile(const fs::path& file) {
    auto attributes = GetFileAttributesW(file.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && !(attributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT));
}
inline bool Hex(const std::string& text) {
    return text.size() == 64 && std::all_of(text.begin(), text.end(), [](char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'); });
}
class Hash {
    BCRYPT_ALG_HANDLE algorithm{}; BCRYPT_HASH_HANDLE hash{}; std::vector<unsigned char> memory;
public:
    Hash() {
        DWORD size{}, copied{};
        try {
            if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0 ||
                BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&size), sizeof(size), &copied, 0) < 0 || size > 65536)
                throw std::runtime_error("hash");
            memory.resize(size);
            if (BCryptCreateHash(algorithm, &hash, memory.data(), size, nullptr, 0, 0) < 0) throw std::runtime_error("hash");
        } catch(...) { if(algorithm)BCryptCloseAlgorithmProvider(algorithm,0);throw; }
    }
    ~Hash() { if (hash) BCryptDestroyHash(hash); if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0); }
    void Add(const void* bytes, ULONG size) { if (BCryptHashData(hash, (PUCHAR)bytes, size, 0) < 0) throw std::runtime_error("hash"); }
    std::string Finish() {
        unsigned char value[32]{};
        if (BCryptFinishHash(hash, value, sizeof(value), 0) < 0) throw std::runtime_error("hash");
        std::string result; static const char digits[] = "0123456789abcdef";
        for (auto c : value) { result += digits[c >> 4]; result += digits[c & 15]; }
        return result;
    }
};
inline std::string FileHash(const fs::path& file, unsigned long long maximum) {
    if (!PlainFile(file) || fs::file_size(file) > maximum) throw std::runtime_error("file");
    std::ifstream input(file, std::ios::binary); if (!input) throw std::runtime_error("file");
    Hash hash; char buffer[65536];
    while (input.read(buffer, sizeof(buffer)) || input.gcount()) hash.Add(buffer, (ULONG)input.gcount());
    if (!input.eof()) throw std::runtime_error("read");
    return hash.Finish();
}
inline std::wstring Setting(const fs::path& root, const wchar_t* name) {
    wchar_t value[512]{}; auto file = root / L"RikaPatch.ini";
    auto size = GetPrivateProfileStringW(L"Patch", name, L"", value, 512, file.c_str());
    if (size >= 511) throw std::runtime_error("config");
    return value;
}
inline std::string Ascii(const std::wstring& text) {
    std::string result;
    for (auto c : text) { if (c > 127) throw std::runtime_error("ascii"); result += (char)c; }
    return result;
}
inline fs::path CustomFont(const fs::path& root) {
    auto name=Setting(root,L"FontFile");if(name.empty())return {};
    if(name!=L"custom.ttf"&&name!=L"custom.otf")throw std::runtime_error("font-name");
    auto expected=Ascii(Setting(root,L"FontSha256"));auto file=root/L"font"/name;
    if(!Hex(expected)||FileHash(file,64ull*1024*1024)!=expected)throw std::runtime_error("font-changed");
    return file;
}
struct Entry { std::string source, payloadHash, file; };
inline std::vector<Entry> Map(const fs::path& root) {
    auto path = root / L"patch-map.txt";
    if (!PlainFile(path) || fs::file_size(path) > 65536) throw std::runtime_error("map");
    std::ifstream input(path); if (!input) throw std::runtime_error("map");
    std::vector<Entry> result; std::string line;
    while (std::getline(input, line)) {
        if (line.empty()) continue;
        std::istringstream row(line); Entry entry; std::string extra;
        if (!(row >> entry.source >> entry.payloadHash >> entry.file) || row >> extra || !Hex(entry.source) || !Hex(entry.payloadHash) ||
            entry.file.size() != 8 || entry.file.substr(4) != ".mdf" || !std::all_of(entry.file.begin(), entry.file.begin()+4, [](char c) { return c >= '0' && c <= '9'; }) ||
            std::any_of(result.begin(), result.end(), [&](const Entry& e) { return e.source == entry.source || e.file == entry.file; })) throw std::runtime_error("map");
        result.push_back(entry); if (result.size() > 256) throw std::runtime_error("map");
    }
    if (result.empty()) throw std::runtime_error("map");
    return result;
}
inline void Status(const fs::path& root, const std::string& text) {
    auto path = root / L"loader-status.txt";
    auto attributes = GetFileAttributesW(path.c_str());
    if (attributes != INVALID_FILE_ATTRIBUTES && !PlainFile(path)) return;
    std::ofstream file(path, std::ios::binary | std::ios::trunc); if (file) file << text;
}
}
