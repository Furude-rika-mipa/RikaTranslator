#pragma once
#include "patch_common.h"
#include <map>
namespace rika {
struct Resource { std::wstring name; std::string source,hash; std::wstring payload; };
inline std::wstring Lower(std::wstring s){for(auto& c:s){if(c==L'/')c=L'\\';c=towlower(c);}return s;}
inline std::vector<Resource> Resources(const fs::path& root){
 auto file=root/L"resource-map.txt";if(!PlainFile(file)||fs::file_size(file)>1024*1024)throw std::runtime_error("resource-map");
 std::ifstream in(file,std::ios::binary);std::string bytes((std::istreambuf_iterator<char>(in)),{});
 if(bytes.size()<2||bytes.size()%2||bytes[0]!=(char)0xff||bytes[1]!=(char)0xfe)throw std::runtime_error("resource-map-encoding");
 std::wstring text((wchar_t*)(bytes.data()+2),(bytes.size()-2)/2);std::wistringstream lines(text);std::wstring line;std::vector<Resource> result;
 while(std::getline(lines,line)){
  if(!line.empty()&&line.back()==L'\r')line.pop_back();if(line.empty())continue;
  std::vector<std::wstring> parts;size_t start=0,pos;while((pos=line.find(L'|',start))!=std::wstring::npos){parts.push_back(line.substr(start,pos-start));start=pos+1;}parts.push_back(line.substr(start));
  if(parts.size()!=4)throw std::runtime_error("resource-map-fields");
  auto name=Lower(parts[0]);fs::path relative(name);
  if(name.empty()||name.size()>512||relative.is_absolute()||name.find_first_of(L":\r\n<>\"?*")!=std::wstring::npos)throw std::runtime_error("resource-name");
  for(auto& part:relative)if(part==L".."||part==L"."||part.empty())throw std::runtime_error("resource-name");
  auto payload=parts[3];if(payload.size()!=8||payload.substr(4)!=L".bin"||!std::all_of(payload.begin(),payload.begin()+4,[](auto c){return c>=L'0'&&c<=L'9';}))throw std::runtime_error("payload-name");
  auto source=Ascii(parts[1]),hash=Ascii(parts[2]);if(!Hex(source)||!Hex(hash)||std::any_of(result.begin(),result.end(),[&](auto& r){return r.name==name||r.payload==payload;}))throw std::runtime_error("resource-duplicate");
  result.push_back({name,source,hash,payload});if(result.size()>256)throw std::runtime_error("resource-count");
 }
 if(result.empty())throw std::runtime_error("resource-empty");return result;
}
inline std::map<WORD,wchar_t> Tunnels(const fs::path& root){
 auto file=root/L"character-map.bin";if(!PlainFile(file)||fs::file_size(file)>65536||fs::file_size(file)%4)throw std::runtime_error("character-map");
 std::ifstream in(file,std::ios::binary);std::map<WORD,wchar_t> out;WORD code,ch;
 while(in.read((char*)&code,2)&&in.read((char*)&ch,2)){
  auto hi=code>>8,lo=code&255;if(!((hi>=0x81&&hi<0xa0)||(hi>=0xe0&&hi<=0xfc))||lo==0||lo>=64||ch<32||(ch>=0xd800&&ch<=0xdfff)||!out.emplace(code,ch).second)throw std::runtime_error("character-code");
 }return out;
}
inline void VerifyResources(const fs::path& root){
 for(auto& r:Resources(root)){
  if(FileHash(root.parent_path()/r.name,128ull*1024*1024)!=r.source||FileHash(root/L"resources"/r.payload,128ull*1024*1024)!=r.hash)throw std::runtime_error("resource-version");
 }
 auto expected=Ascii(Setting(root,L"CharacterMapSha256"));if(!Hex(expected)||FileHash(root/L"character-map.bin",65536)!=expected)throw std::runtime_error("character-map-hash");Tunnels(root);
}
}
