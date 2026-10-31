#include <windows.h>
#include <detours.h>
#include <string>
#include <filesystem>
#include <cstdio>

int wmain(int argc, wchar_t** argv) {
    if (argc != 4) { puts("Usage: worker GAME_EXE JOB_DIRECTORY ARCHIVE_NAME"); return 2; }
    auto exe = std::filesystem::absolute(argv[1]);
    auto job = std::filesystem::absolute(argv[2]);
    std::filesystem::create_directories(job);
    SetEnvironmentVariableW(L"RIKA_CX_JOB", job.c_str());
    SetEnvironmentVariableW(L"RIKA_CX_PACKAGE", argv[3]);
    wchar_t module[32768]{}; GetModuleFileNameW(nullptr, module, 32768);
    auto dll = (std::filesystem::path(module).parent_path() / L"RikaCxdecExtractor.dll").string();
    std::wstring command = L"\"" + exe.wstring() + L"\"";
    STARTUPINFOW si{}; si.cb = sizeof(si); si.dwFlags = STARTF_USESHOWWINDOW; si.wShowWindow = SW_HIDE;
    PROCESS_INFORMATION pi{};
    HANDLE group = CreateJobObjectW(nullptr, nullptr);
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION info{};
    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!group || !SetInformationJobObject(group, JobObjectExtendedLimitInformation, &info, sizeof(info))) return 3;
    if (!DetourCreateProcessWithDllW(exe.c_str(), command.data(), nullptr, nullptr, FALSE,
        CREATE_SUSPENDED, nullptr, exe.parent_path().c_str(), &si, &pi, dll.c_str(), nullptr)) {
        printf("{\"error\":\"launch\",\"code\":%lu}\n", GetLastError()); CloseHandle(group); return 4;
    }
    if (!AssignProcessToJobObject(group, pi.hProcess)) {
        TerminateProcess(pi.hProcess, 5); CloseHandle(pi.hThread); CloseHandle(pi.hProcess); CloseHandle(group); return 5;
    }
    ResumeThread(pi.hThread); CloseHandle(pi.hThread);
    printf("{\"phase\":\"started\",\"childPid\":%lu}\n", pi.dwProcessId); fflush(stdout);
    std::string previous;
    int result = 6;
    for (int attempt = 0; attempt < 2400; ++attempt) {
        FILE* file{}; std::string status;
        if (!_wfopen_s(&file, (job / L"status.txt").c_str(), L"rb") && file) {
            char text[256]{}; fread(text, 1, sizeof(text)-1, file); fclose(file); status = text;
        }
        if (!status.empty() && status != previous) {
            printf("{\"phase\":\"%s\"}\n", status.c_str()); fflush(stdout); previous = status;
        }
        if (status == "done") { result = 0; break; }
        if (status.starts_with("error:")) { result = 7; break; }
        if (WaitForSingleObject(pi.hProcess, 100) == WAIT_OBJECT_0) {
            DWORD code{}; GetExitCodeProcess(pi.hProcess, &code);
            printf("{\"error\":\"game-exited\",\"code\":%lu}\n", code); result = 8; break;
        }
    }
    TerminateJobObject(group, result);
    WaitForSingleObject(pi.hProcess, 5000);
    CloseHandle(pi.hProcess); CloseHandle(group);
    return result;
}
