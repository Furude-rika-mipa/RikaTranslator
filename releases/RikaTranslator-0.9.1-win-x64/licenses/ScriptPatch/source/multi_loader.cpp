#include "multi_common.h"
#include "Detours/detours.h"
int WINAPI wWinMain(HINSTANCE,HINSTANCE,PWSTR,int){
 bool verify=wcsstr(GetCommandLineW(),L"--verify-only")!=nullptr;auto root=rika::ModulePath().parent_path();
 try{
  if(rika::Setting(root,L"Format")!=L"2")throw std::runtime_error("config-version");
  auto name=rika::Setting(root,L"Game");if(name.empty()||name==L"."||name==L".."||name.find_first_of(L"/\\:\r\n\"<>|")!=std::wstring::npos)throw std::runtime_error("game-name");
  auto game=root.parent_path()/name;auto hash=rika::Ascii(rika::Setting(root,L"GameSha256"));
  if(!rika::Hex(hash)||rika::FileHash(game,128ull*1024*1024)!=hash)throw std::runtime_error("game-version");
  rika::VerifyResources(root);auto dll=root/L"RikaScriptPatch.dll";hash=rika::Ascii(rika::Setting(root,L"PatchDllSha256"));
  if(!rika::Hex(hash)||rika::FileHash(dll,8ull*1024*1024)!=hash)throw std::runtime_error("component-version");
  if(rika::Setting(root,L"ChineseFont")==L"1")rika::CustomFont(root);
  if(verify)return 0;
  wchar_t shortPath[32768]{};std::wstring wide=dll.wstring();BOOL substituted=FALSE;
  int size=WideCharToMultiByte(CP_ACP,WC_NO_BEST_FIT_CHARS,wide.c_str(),-1,nullptr,0,nullptr,&substituted);
  if(substituted){if(!GetShortPathNameW(dll.c_str(),shortPath,32768))throw std::runtime_error("unicode-path");wide=shortPath;}
  substituted=FALSE;size=WideCharToMultiByte(CP_ACP,WC_NO_BEST_FIT_CHARS,wide.c_str(),-1,nullptr,0,nullptr,&substituted);if(size<=0||substituted)throw std::runtime_error("unicode-path");
  std::string library(size,'\0');WideCharToMultiByte(CP_ACP,WC_NO_BEST_FIT_CHARS,wide.c_str(),-1,library.data(),size,nullptr,&substituted);
  STARTUPINFOW si{};si.cb=sizeof(si);PROCESS_INFORMATION pi{};std::wstring command=L"\""+game.wstring()+L"\"";
  rika::Status(root,"starting");
  if(!DetourCreateProcessWithDllW(game.c_str(),command.data(),nullptr,nullptr,FALSE,0,nullptr,game.parent_path().c_str(),&si,&pi,library.c_str(),nullptr))throw std::runtime_error("launch");
  CloseHandle(pi.hThread);CloseHandle(pi.hProcess);return 0;
 }catch(const std::exception& e){rika::Status(root,std::string("error:")+e.what());if(!verify)MessageBoxW(nullptr,L"补丁未能启动，请核对游戏版本和补丁文件。详情见 loader-status.txt。",L"Rika 汉化补丁",MB_OK|MB_ICONERROR);return 2;}
}
