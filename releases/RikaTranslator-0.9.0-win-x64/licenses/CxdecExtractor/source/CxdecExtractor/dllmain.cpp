
#include "Application.h"
#include <string>
#include <cstdio>
#include <stdexcept>

static std::wstring JobDirectory() {
    wchar_t path[32768]{};
    auto size = GetEnvironmentVariableW(L"RIKA_CX_JOB", path, 32768);
    if (!size || size >= 32768) return L"";
    return path;
}
static void Status(const char* value) {
    auto job = JobDirectory(); if (job.empty()) return;
    FILE* file{};
    if (!_wfopen_s(&file, (job + L"\\status.tmp").c_str(), L"wb") && file) {
        fwrite(value, 1, strlen(value), file); fclose(file);
        MoveFileExW((job + L"\\status.tmp").c_str(), (job + L"\\status.txt").c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
    }
}
DWORD WINAPI RunJob(void*) {
    Status("waiting");
    auto app = Engine::Application::GetInstance();
    for (int i = 0; i < 900; ++i) {
        if (app && app->IsTVPEngineInitialize() && app->GetExtractor()->IsInitialized()) break;
        Sleep(100);
    }
    if (!app || !app->IsTVPEngineInitialize() || !app->GetExtractor()->IsInitialized()) {
        Status("error:engine-initialization"); return 1;
    }
    try {
        wchar_t package[260]{};
        if (!GetEnvironmentVariableW(L"RIKA_CX_PACKAGE", package, 260)) throw std::runtime_error("No archive");
        Status("extracting");
        app->GetExtractor()->ExtractPackage(package);
        Status("done");
        return 0;
    } catch (...) { Status("error:extraction"); return 2; }
}
BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(module);
        if (JobDirectory().empty()) return FALSE;
        try { Engine::Application::Initialize(module); }
        catch (...) { Status("error:initialization"); return FALSE; }
        HANDLE thread = CreateThread(nullptr, 0, RunJob, nullptr, 0, nullptr);
        if (thread) CloseHandle(thread);
    }
    return TRUE;
}
extern "C" __declspec(dllexport) void Dummy() {}
