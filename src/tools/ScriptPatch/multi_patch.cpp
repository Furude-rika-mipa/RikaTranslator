#include "multi_common.h"
#include "Detours/detours.h"
#include <mutex>
static HMODULE module;static rika::fs::path root;static std::map<std::wstring,std::wstring> resources;
static std::map<WORD,wchar_t> characters;static std::wstring family;static bool chineseFont=false;
static thread_local bool inside=false;
static decltype(&CreateFileW) RealCreateFileW=CreateFileW;
static decltype(&CreateFileA) RealCreateFileA=CreateFileA;
static decltype(&MultiByteToWideChar) RealMultiByteToWideChar=MultiByteToWideChar;
static decltype(&CreateFontIndirectW) RealCreateFontIndirectW=CreateFontIndirectW;
static decltype(&CreateFontIndirectA) RealCreateFontIndirectA=CreateFontIndirectA;
static decltype(&CreateFontW) RealCreateFontW=CreateFontW;
static decltype(&CreateFontA) RealCreateFontA=CreateFontA;
static decltype(&GetGlyphOutlineA) RealGetGlyphOutlineA=GetGlyphOutlineA;
static decltype(&GetGlyphOutlineW) RealGetGlyphOutlineW=GetGlyphOutlineW;
static decltype(&TextOutA) RealTextOutA=TextOutA;
static decltype(&ExtTextOutA) RealExtTextOutA=ExtTextOutA;
static decltype(&GetTextExtentPoint32A) RealExtentA=GetTextExtentPoint32A;
static std::wstring Wide(const char* p){if(!p)return {};int n=RealMultiByteToWideChar(CP_ACP,0,p,-1,nullptr,0);if(n<=0||n>32768)return {};std::wstring s(n,L'\0');RealMultiByteToWideChar(CP_ACP,0,p,-1,s.data(),n);s.pop_back();return s;}
static const wchar_t* Redirect(const wchar_t* p,DWORD access,DWORD creation){
 if(!p||creation!=OPEN_EXISTING||(access&(GENERIC_WRITE|DELETE|FILE_WRITE_DATA|FILE_APPEND_DATA)))return p;
 wchar_t path[32768];auto n=GetFullPathNameW(p,32768,path,nullptr);if(!n||n>=32768)return p;
 auto it=resources.find(rika::Lower(path));return it==resources.end()?p:it->second.c_str();
}
static HANDLE WINAPI FileW(LPCWSTR p,DWORD a,DWORD s,LPSECURITY_ATTRIBUTES sa,DWORD c,DWORD f,HANDLE h){if(inside)return RealCreateFileW(p,a,s,sa,c,f,h);inside=true;auto target=Redirect(p,a,c);auto result=RealCreateFileW(target,a,s,sa,c,f,h);inside=false;return result;}
static HANDLE WINAPI FileA(LPCSTR p,DWORD a,DWORD s,LPSECURITY_ATTRIBUTES sa,DWORD c,DWORD f,HANDLE h){if(inside)return RealCreateFileA(p,a,s,sa,c,f,h);inside=true;auto wide=Wide(p);auto target=Redirect(wide.c_str(),a,c);HANDLE result=target!=wide.c_str()?RealCreateFileW(target,a,s,sa,c,f,h):RealCreateFileA(p,a,s,sa,c,f,h);inside=false;return result;}
static int WINAPI Decode(UINT cp,DWORD flags,LPCCH bytes,int count,LPWSTR out,int capacity){
 if(!bytes||(cp!=932&&cp!=CP_ACP&&cp!=CP_THREAD_ACP)||characters.empty()||count==0||count< -1)return RealMultiByteToWideChar(cp,flags,bytes,count,out,capacity);
 size_t length=count<0?strlen(bytes)+1:(size_t)count;if(length>1048576)return RealMultiByteToWideChar(cp,flags,bytes,count,out,capacity);
 bool found=false;std::wstring decoded;
 for(size_t i=0;i<length;){BYTE first=(BYTE)bytes[i];int n=((first>=0x81&&first<0xa0)||(first>=0xe0&&first<=0xfc))&&i+1<length?2:1;
  auto it=n==2?characters.find((WORD)((first<<8)|(BYTE)bytes[i+1])):characters.end();
  if(it!=characters.end()){decoded+=it->second;found=true;}else{wchar_t ch[2]{};auto size=RealMultiByteToWideChar(932,flags,bytes+i,n,ch,2);if(size<=0)return 0;decoded.append(ch,size);}i+=n;
 }
 if(!found)return RealMultiByteToWideChar(cp,flags,bytes,count,out,capacity);if(!capacity)return (int)decoded.size();if(!out||capacity<(int)decoded.size()){SetLastError(ERROR_INSUFFICIENT_BUFFER);return 0;}memcpy(out,decoded.data(),decoded.size()*2);return (int)decoded.size();
}
static HFONT WINAPI FontW(const LOGFONTW* font){if(!font||!chineseFont)return RealCreateFontIndirectW(font);auto copy=*font;wcsncpy_s(copy.lfFaceName,family.c_str(),_TRUNCATE);copy.lfCharSet=DEFAULT_CHARSET;return RealCreateFontIndirectW(&copy);}
static HFONT WINAPI FontA(const LOGFONTA* font){if(!font||!chineseFont)return RealCreateFontIndirectA(font);LOGFONTW copy{};memcpy(&copy,font,offsetof(LOGFONTA,lfFaceName));wcsncpy_s(copy.lfFaceName,family.c_str(),_TRUNCATE);copy.lfCharSet=DEFAULT_CHARSET;return RealCreateFontIndirectW(&copy);}
static HFONT WINAPI FontDirectW(int h,int w,int e,int o,int weight,DWORD italic,DWORD underline,DWORD strike,DWORD charset,DWORD precision,DWORD clip,DWORD quality,DWORD pitch,LPCWSTR face){
 if(!chineseFont)return RealCreateFontW(h,w,e,o,weight,italic,underline,strike,charset,precision,clip,quality,pitch,face);
 return RealCreateFontW(h,w,e,o,weight,italic,underline,strike,DEFAULT_CHARSET,precision,clip,quality,pitch,family.c_str());
}
static HFONT WINAPI FontDirectA(int h,int w,int e,int o,int weight,DWORD italic,DWORD underline,DWORD strike,DWORD charset,DWORD precision,DWORD clip,DWORD quality,DWORD pitch,LPCSTR face){
 if(!chineseFont)return RealCreateFontA(h,w,e,o,weight,italic,underline,strike,charset,precision,clip,quality,pitch,face);
 return RealCreateFontW(h,w,e,o,weight,italic,underline,strike,DEFAULT_CHARSET,precision,clip,quality,pitch,family.c_str());
}
static DWORD WINAPI GlyphA(HDC dc,UINT ch,UINT format,LPGLYPHMETRICS gm,DWORD size,LPVOID bytes,const MAT2* mat){
 if(!(format&GGO_GLYPH_INDEX)){
  auto it=characters.find((WORD)ch);if(it!=characters.end())return RealGetGlyphOutlineW(dc,it->second,format,gm,size,bytes,mat);
  if(chineseFont&&ch>255&&ch<=65535){char data[]={(char)(ch>>8),(char)ch};wchar_t wide{};if(RealMultiByteToWideChar(932,0,data,2,&wide,1)==1)return RealGetGlyphOutlineW(dc,wide,format,gm,size,bytes,mat);}
 }return RealGetGlyphOutlineA(dc,ch,format,gm,size,bytes,mat);
}
static std::wstring RenderText(LPCSTR text,int count){if(!text||count<0||count>1048576)return {};int n=Decode(932,0,text,count,nullptr,0);if(n<1)return {};std::wstring wide(n,L'\0');if(Decode(932,0,text,count,wide.data(),n)!=n)return {};return wide;}
static BOOL WINAPI TextA(HDC dc,int x,int y,LPCSTR text,int count){if(!chineseFont&&characters.empty())return RealTextOutA(dc,x,y,text,count);auto wide=RenderText(text,count);return TextOutW(dc,x,y,wide.data(),(int)wide.size());}
static BOOL WINAPI ExtentA(HDC dc,LPCSTR text,int count,LPSIZE size){if(!chineseFont&&characters.empty())return RealExtentA(dc,text,count,size);auto wide=RenderText(text,count);return GetTextExtentPoint32W(dc,wide.data(),(int)wide.size(),size);}
static BOOL WINAPI ExtTextA(HDC dc,int x,int y,UINT flags,const RECT* rect,LPCSTR text,UINT count,const INT* spacing){
 if((!chineseFont&&characters.empty())||(flags&ETO_GLYPH_INDEX)||count>1048576)return RealExtTextOutA(dc,x,y,flags,rect,text,count,spacing);
 auto wide=RenderText(text,(int)count);std::vector<INT> advances;
 if(spacing){size_t step=(flags&ETO_PDY)?2:1;for(size_t i=0;i<count;){BYTE hi=(BYTE)text[i];size_t n=((hi>=0x81&&hi<0xa0)||(hi>=0xe0&&hi<=0xfc))&&i+1<count?2:1;for(size_t d=0;d<step;d++){INT advance=spacing[i*step+d];if(n==2)advance+=spacing[(i+1)*step+d];advances.push_back(advance);}i+=n;}if(advances.size()!=wide.size()*step)return RealExtTextOutA(dc,x,y,flags,rect,text,count,spacing);}
 return ExtTextOutW(dc,x,y,flags,rect,wide.data(),(UINT)wide.size(),spacing?advances.data():nullptr);
}
static void LookupTables(){
 if(characters.empty())return;auto base=(BYTE*)GetModuleHandleW(nullptr);auto dos=(IMAGE_DOS_HEADER*)base;auto nt=(IMAGE_NT_HEADERS*)(base+dos->e_lfanew);size_t size=nt->OptionalHeader.SizeOfImage;
 const WORD pattern[]={0x3000,0x3001,0x3002,0xff0c,0xff0e,0x30fb,0xff1a,0xff1b};
 // Some engines use an internal CP932 lookup table instead of the Win32 API.
 // Check three distant standard mappings before touching any process memory.
 for(size_t pos=0x8140*2;pos+sizeof(pattern)<size;pos+=2){
  MEMORY_BASIC_INFORMATION region{};if(!VirtualQuery(base+pos,&region,sizeof(region)))break;
  auto end=(BYTE*)region.BaseAddress+region.RegionSize;
  if(region.State!=MEM_COMMIT||(region.Protect&(PAGE_NOACCESS|PAGE_GUARD))){pos=(size_t)(end-base)-2;continue;}
  if(base+pos+sizeof(pattern)>end)continue;
  if(memcmp(base+pos,pattern,sizeof(pattern)))continue;size_t start=pos-0x8140*2;if(start+0x10000*2>size)continue;
  MEMORY_BASIC_INFORMATION tableRegion{};if(!VirtualQuery(base+start,&tableRegion,sizeof(tableRegion))||tableRegion.State!=MEM_COMMIT||(tableRegion.Protect&(PAGE_NOACCESS|PAGE_GUARD))||(BYTE*)tableRegion.BaseAddress+tableRegion.RegionSize<base+start+0x10000*2)continue;
  auto table=(WORD*)(base+start);if(table[0x8341]!=0x30a2||table[0x889f]!=0x4e9c)continue;
  for(auto& [code,ch]:characters){DWORD old;if(!VirtualProtect(table+code,2,PAGE_READWRITE,&old))throw std::runtime_error("font-lookup");table[code]=ch;DWORD ignored;VirtualProtect(table+code,2,old,&ignored);}
 }
}
extern "C" __declspec(dllexport) void WINAPI RikaPatchMarker(){}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID){
 if(DetourIsHelperProcess())return TRUE;
 if(reason==DLL_PROCESS_ATTACH){
  module=instance;DisableThreadLibraryCalls(instance);DetourRestoreAfterWith();
  try{
   root=rika::ModulePath(instance).parent_path();if(rika::Setting(root,L"Format")!=L"2")throw std::runtime_error("config");
   rika::VerifyResources(root);characters=rika::Tunnels(root);
   for(auto& r:rika::Resources(root))resources.emplace(rika::Lower((root.parent_path()/r.name).wstring()),(root/L"resources"/r.payload).wstring());
   chineseFont=rika::Setting(root,L"ChineseFont")==L"1";family=rika::Setting(root,L"FontFamily");
   if(chineseFont){if(family.empty()||family.size()>31)throw std::runtime_error("font-family");auto file=rika::CustomFont(root);if(!file.empty()&&!AddFontResourceExW(file.c_str(),FR_PRIVATE,nullptr))throw std::runtime_error("font-load");}
   LookupTables();if(DetourTransactionBegin()!=NO_ERROR)throw std::runtime_error("hooks");DetourUpdateThread(GetCurrentThread());
   LONG error=NO_ERROR;
   auto attach=[&](auto* target,auto hook){auto e=DetourAttach((PVOID*)target,(PVOID)hook);if(e!=NO_ERROR)error=e;};
   attach(&RealCreateFileW,FileW);attach(&RealCreateFileA,FileA);attach(&RealMultiByteToWideChar,Decode);attach(&RealCreateFontIndirectW,FontW);attach(&RealCreateFontIndirectA,FontA);attach(&RealCreateFontW,FontDirectW);attach(&RealCreateFontA,FontDirectA);attach(&RealGetGlyphOutlineA,GlyphA);
   attach(&RealTextOutA,TextA);attach(&RealExtTextOutA,ExtTextA);attach(&RealExtentA,ExtentA);
   if(error!=NO_ERROR){DetourTransactionAbort();throw std::runtime_error("hook-attach");}if(DetourTransactionCommit()!=NO_ERROR)throw std::runtime_error("hook-commit");
   rika::Status(root,"ready");
  }catch(const std::exception& e){rika::Status(root,std::string("error:")+e.what());return FALSE;}
 }
 return TRUE;
}
