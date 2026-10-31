// Stream interception adapted from crskycode/KrkrPatch (GPL-3.0).
#include "patch_common.h"
#include "KrkrPlugin/tp_stub.h"
#include "Detours/detours.h"
#include <shlwapi.h>
#include <unordered_map>
#include <mutex>

// Public KiriKiri stream ABI, matching krkrz/krkrz tjs2/tjs.h.
class tTJSBinaryStream {
public:
    virtual tjs_uint64 TJS_INTF_METHOD Seek(tjs_int64, tjs_int) = 0;
    virtual tjs_uint TJS_INTF_METHOD Read(void*, tjs_uint) = 0;
    virtual tjs_uint TJS_INTF_METHOD Write(const void*, tjs_uint) = 0;
    virtual void TJS_INTF_METHOD SetEndOfStorage() = 0;
    virtual tjs_uint64 TJS_INTF_METHOD GetSize() = 0;
    virtual ~tTJSBinaryStream() {}
};
static rika::fs::path g_root;
static std::vector<rika::Entry> g_entries;
static std::unordered_map<std::wstring, int> g_cache;
static std::mutex g_mutex;
static bool g_debug;
static thread_local bool g_inside;
static auto g_getProc = GetProcAddress;
using Link = HRESULT(__stdcall*)(iTVPFunctionExporter*);
static Link g_link;
using Create = tTJSBinaryStream*(__fastcall*)(const ttstr&, tjs_uint);
static Create g_create;
static auto g_font = CreateFontIndirectW;
static bool g_fontFallback;
static std::wstring g_fontFamily=L"Microsoft YaHei";

template<class T> static bool Hook(T& original, T replacement) {
    if (DetourTransactionBegin() != NO_ERROR) return false;
    DetourUpdateThread(GetCurrentThread());
    if (DetourAttach(&(PVOID&)original, (PVOID)replacement) != NO_ERROR) { DetourTransactionAbort(); return false; }
    return DetourTransactionCommit() == NO_ERROR;
}
template<class T> static void Unhook(T& original, T replacement) {
    DetourTransactionBegin(); DetourUpdateThread(GetCurrentThread()); DetourDetach(&(PVOID&)original, (PVOID)replacement); DetourTransactionCommit();
}
static void Trace(const std::string& text) {
    if (!g_debug) return;
    std::lock_guard<std::mutex> lock(g_mutex);
    auto path = g_root / L"patch-debug.log";
    if (rika::fs::exists(path) && !rika::PlainFile(path)) return;
    if (rika::fs::exists(path) && rika::fs::file_size(path) > 1024 * 1024) return;
    std::ofstream file(path, std::ios::app); if (file) file << text << '\n';
}
static std::string Utf8Name(const std::wstring& value) {
    int count=WideCharToMultiByte(CP_UTF8,0,value.c_str(),(int)value.size(),nullptr,0,nullptr,nullptr);
    std::string result(count,'\0');if(count)WideCharToMultiByte(CP_UTF8,0,value.c_str(),(int)value.size(),result.data(),count,nullptr,nullptr);
    return result;
}
static HFONT WINAPI Font(const LOGFONTW* value) {
    if (!value || !g_fontFallback) return g_font(value);
    auto face = std::wstring(value->lfFaceName);
    Trace("font:"+Utf8Name(face));
    // Preserve custom digit/icon/symbol fonts; only replace common Japanese UI text families.
    const wchar_t* families[] = { L"MS Gothic",L"MS PGothic",L"MS UI Gothic",L"Yu Gothic",L"MS Mincho",L"MS PMincho",L"Yu Mincho",L"ＭＳ ゴシック",L"ＭＳ Ｐゴシック",L"ＭＳ 明朝",L"ＭＳ Ｐ明朝",L"メイリオ",
        L"源ノ明朝 JP",L"源ノ角ゴシック JP",L"Source Han Serif JP",L"Source Han Sans JP",L"Noto Serif CJK JP",L"Noto Sans CJK JP" };
    if (value->lfCharSet == SYMBOL_CHARSET || !std::any_of(std::begin(families), std::end(families), [&](const wchar_t* name) { return _wcsicmp(face.c_str(), name) == 0; })) return g_font(value);
    LOGFONTW copy = *value; wcscpy_s(copy.lfFaceName, g_fontFamily.c_str()); copy.lfCharSet = DEFAULT_CHARSET;
    return g_font(&copy);
}
static tTJSBinaryStream* __fastcall Stream(const ttstr& name, tjs_uint flags) {
    auto original = g_create(name, flags);
    if (!original || flags != TJS_BS_READ || g_inside) return original;
    struct Guard { Guard() { g_inside = true; } ~Guard() { g_inside = false; } } guard;
    tjs_uint64 position = 0; int selected = -1; bool known = false;
    auto key = std::wstring(name.c_str());
    try {
        { std::lock_guard<std::mutex> lock(g_mutex); auto found = g_cache.find(key); if (found != g_cache.end()) { selected = found->second; known = true; } }
        position = original->Seek(0, TJS_BS_SEEK_CUR);
        if (!known) {
            auto size = original->GetSize();
            if (size >= 8 && size <= 32ull * 1024 * 1024) {
                unsigned char prefix[4]{}; original->Seek(0,TJS_BS_SEEK_SET); auto count=original->Read(prefix,4);
                if (count == 4 && (!memcmp(prefix,"PSB\0",4) || !memcmp(prefix,"mdf\0",4))) {
                    rika::Hash hash; hash.Add(prefix,4); unsigned char buffer[65536]; tjs_uint64 remaining = size-4;
                    while (remaining) { auto n=original->Read(buffer,(tjs_uint)std::min<tjs_uint64>(remaining,sizeof(buffer))); if (!n || n > remaining) throw std::runtime_error("read"); hash.Add(buffer,n); remaining-=n; }
                    auto digest=hash.Finish(); Trace("seen:"+digest+" "+std::to_string(size)+" "+Utf8Name(key));
                    for (size_t i=0;i<g_entries.size();i++) if(g_entries[i].source==digest) { selected=(int)i;break; }
                }
            }
            { std::lock_guard<std::mutex> lock(g_mutex); if(g_cache.size()<8192 && key.size()<2048)g_cache.emplace(key,selected); }
        }
        original->Seek(position,TJS_BS_SEEK_SET);
        if (selected >= 0) {
            auto& entry=g_entries[selected]; auto path=g_root/L"scripts"/entry.file;
            if(rika::FileHash(path,32ull*1024*1024)!=entry.payloadHash) { rika::Status(g_root,"error:payload-changed");return original; }
            IStream* file{};
            if(FAILED(SHCreateStreamOnFileEx(path.c_str(),STGM_READ|STGM_SHARE_DENY_WRITE,FILE_ATTRIBUTE_NORMAL,FALSE,nullptr,&file))) return original;
            tTJSBinaryStream* patched{};
            try { patched=TVPCreateBinaryStreamAdapter(file); } catch(...) { file->Release();throw; }
            file->Release(); if(!patched)return original;
            patched->Seek(position,TJS_BS_SEEK_SET); delete original;
            Trace("patched:"+entry.source); return patched;
        }
    } catch(...) { try { original->Seek(position,TJS_BS_SEEK_SET); } catch(...) {} }
    return original;
}
static bool InstallStream() {
    // Exact signature from upstream KrkrPatch; fail closed if this engine differs.
    static const unsigned char pattern[]="\x55\x8B\xEC\x6A\xFF\x68\x2A\x2A\x2A\x2A\x64\xA1\x2A\x2A\x2A\x2A\x50\x83\xEC\x5C\x53\x56\x57\xA1\x2A\x2A\x2A\x2A\x33\xC5\x50\x8D\x45\xF4\x64\xA3\x2A\x2A\x2A\x2A\x89\x65\xF0\x89\x4D\xEC\xC7\x45\x2A\x2A\x2A\x2A\x2A\xE8\x2A\x2A\x2A\x2A\x8B\x4D\xF4\x64\x89\x0D\x2A\x2A\x2A\x2A\x59\x5F\x5E\x5B\x8B\xE5\x5D\xC3";
    auto module=(unsigned char*)GetModuleHandleW(nullptr); auto dos=(IMAGE_DOS_HEADER*)module;
    auto nt=(IMAGE_NT_HEADERS*)(module+dos->e_lfanew); auto sections=IMAGE_FIRST_SECTION(nt);
    Create found{};
    for(unsigned s=0;s<nt->FileHeader.NumberOfSections;s++) {
        if(!(sections[s].Characteristics&IMAGE_SCN_MEM_EXECUTE))continue;
        auto start=module+sections[s].VirtualAddress; auto size=sections[s].Misc.VirtualSize;
        for(size_t i=0;i+sizeof(pattern)-1<=size;i++) {
            bool match=true;for(size_t j=0;j<sizeof(pattern)-1;j++)if(pattern[j]!=0x2a&&start[i+j]!=pattern[j]){match=false;break;}
            if(match){if(found)return false;found=(Create)(start+i);}
        }
    }
    if(!found)return false; g_create=found;
    return Hook(g_create,Stream);
}
static HRESULT __stdcall LinkEngine(iTVPFunctionExporter* exporter) {
    auto result=g_link(exporter);Unhook(g_link,LinkEngine);
    if(!TVPInitImportStub(exporter)||!InstallStream()){rika::Status(g_root,"error:unsupported-engine");return result;}
    if(g_fontFallback) {
        try {
            auto family=rika::Setting(g_root,L"FontFamily");
            if(!family.empty()){if(family.size()>=LF_FACESIZE||family.find_first_of(L"\r\n;=[]\"")!=std::wstring::npos)throw std::runtime_error("font-family");g_fontFamily=family;}
            auto file=rika::CustomFont(g_root);
            if(!file.empty()&&!AddFontResourceExW(file.c_str(),FR_PRIVATE,nullptr))throw std::runtime_error("font-load");
            if(!Hook(g_font,Font))throw std::runtime_error("font-hook");
            Trace("font-selected:"+Utf8Name(g_fontFamily));
        } catch(const std::exception& error) {rika::Status(g_root,std::string("error:")+error.what());return result;}
    }
    rika::Status(g_root,"ready");Trace("ready");return result;
}
static FARPROC WINAPI GetProc(HMODULE module,LPCSTR name) {
    auto result=g_getProc(module,name);
    if(result && HIWORD(name) && !strcmp(name,"V2Link")) {
        g_link=(Link)result;Unhook(g_getProc,GetProc);Hook(g_link,LinkEngine);
    }
    return result;
}
BOOL APIENTRY DllMain(HMODULE module,DWORD reason,LPVOID) {
    if(reason==DLL_PROCESS_ATTACH) {
        if(DetourIsHelperProcess())return TRUE;
        DetourRestoreAfterWith();DisableThreadLibraryCalls(module);
        try { g_root=rika::ModulePath(module).parent_path(); g_entries=rika::Map(g_root);
            g_debug=rika::Setting(g_root,L"Diagnostics")==L"1"; g_fontFallback=rika::Setting(g_root,L"ChineseFont")==L"1";
            return Hook(g_getProc,GetProc)?TRUE:FALSE;
        } catch(...) { return FALSE; }
    }
    return TRUE;
}
extern "C" __declspec(dllexport) BOOL CreatePlugin() { return TRUE; }
