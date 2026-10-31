#include "patch_common.h"
#include "Detours/detours.h"
#include <shellapi.h>

int WINAPI wWinMain(HINSTANCE,HINSTANCE,PWSTR,int) {
    bool verify=wcsstr(GetCommandLineW(),L"--verify-only")!=nullptr;
    try {
        auto root=rika::ModulePath().parent_path();
        if(rika::Setting(root,L"Format")!=L"1")throw std::runtime_error("config-version");
        auto name=rika::Setting(root,L"Game");auto expected=rika::Ascii(rika::Setting(root,L"GameSha256"));
        if(name.empty()||name.size()>200||name.find_first_of(L"/\\:\r\n\"<>|")!=std::wstring::npos||name==L"."||name==L".."||!rika::Hex(expected)) throw std::runtime_error("config");
        auto game=root.parent_path()/name;
        if(rika::FileHash(game,128ull*1024*1024)!=expected)throw std::runtime_error("game-version");
        auto entries=rika::Map(root);
        for(auto& entry:entries)if(rika::FileHash(root/L"scripts"/entry.file,32ull*1024*1024)!=entry.payloadHash)throw std::runtime_error("payload-changed");
        auto dll=root/L"RikaScriptPatch.dll";auto dllHash=rika::Ascii(rika::Setting(root,L"PatchDllSha256"));
        if(!rika::Hex(dllHash)||rika::FileHash(dll,8ull*1024*1024)!=dllHash)throw std::runtime_error("component-changed");
        if(rika::Setting(root,L"ChineseFont")==L"1")rika::CustomFont(root);
        if(verify)return 0;
        std::wstring dllPath=dll.wstring();
        BOOL substituted=FALSE;int count=WideCharToMultiByte(CP_ACP,WC_NO_BEST_FIT_CHARS,dllPath.c_str(),-1,nullptr,0,nullptr,&substituted);
        if(substituted) { wchar_t shortName[32768]{}; if(!GetShortPathNameW(dll.c_str(),shortName,32768))throw std::runtime_error("unicode-path");dllPath=shortName; }
        substituted=FALSE;count=WideCharToMultiByte(CP_ACP,WC_NO_BEST_FIT_CHARS,dllPath.c_str(),-1,nullptr,0,nullptr,&substituted);
        if(!count||substituted)throw std::runtime_error("unicode-path");
        std::string library(count,'\0');WideCharToMultiByte(CP_ACP,WC_NO_BEST_FIT_CHARS,dllPath.c_str(),-1,library.data(),count,nullptr,&substituted);
        STARTUPINFOW startup{};startup.cb=sizeof(startup);PROCESS_INFORMATION child{};
        std::wstring command=L"\""+game.wstring()+L"\"";
        rika::Status(root,"starting");
        if(!DetourCreateProcessWithDllW(game.c_str(),command.data(),nullptr,nullptr,FALSE,0,nullptr,game.parent_path().c_str(),&startup,&child,library.c_str(),nullptr))throw std::runtime_error("launch");
        CloseHandle(child.hThread);CloseHandle(child.hProcess);return 0;
    } catch(const std::exception& error) {
        try { rika::Status(rika::ModulePath().parent_path(),std::string("error:")+error.what()); }catch(...){}
        if(!verify)MessageBoxW(nullptr,L"汉化补丁未能启动。请把整个补丁文件夹放进对应游戏目录，确认游戏版本与补丁一致、文件完整，再运行本启动器。可查看补丁目录的 loader-status.txt。",L"Rika 汉化补丁",MB_OK|MB_ICONERROR);
        return 2;
    }
}
