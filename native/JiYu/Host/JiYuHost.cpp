// SteamEYA JiYu Host
//
// 32 位无界面宿主进程，职责：
//   1. 找到极域电子教室 StudentMain.exe，并向其注入 SteamEya.JiYuHook.dll；
//   2. 枚举极域窗口，把广播/黑屏窗口句柄转发给 Hook；
//   3. 注册全局“老板键”，按下后让 Hook 把广播窗口强制全屏并置顶；
//   4. 把运行状态写入状态文件，供 64 位 SteamEYA 客户端「小工具」页读取；
//   5. 接收客户端通过 WM_COPYDATA 下发的配置命令。
//
// 通信约定：
//   宿主窗口  class = "SteamEYA.JiYuHost"  title = "SteamEYA.JiYuHost.Window"
//   Hook 窗口 title = "SteamEYA.JiYuHook.Window"
//   客户端 -> 宿主：WM_COPYDATA("cfg:..." / "cmd:...")
//   Hook  -> 宿主：WM_COPYDATA("hkb:...")

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <shlwapi.h>
#include <tlhelp32.h>
#include <string>
#include <string.h>
#include <vector>
#include <cstdio>

#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "advapi32.lib")

static const wchar_t* kHostClassName = L"SteamEYA.JiYuHost";
static const wchar_t* kHostWindowTitle = L"SteamEYA.JiYuHost.Window";
static const wchar_t* kHookWindowTitle = L"SteamEYA.JiYuHook.Window";

static const UINT_PTR kTimerTick = 1;
#define WM_APP_ENUM_WINDOWS (WM_APP + 1)
static const int kHotkeyId = 1;

struct Config
{
    std::wstring statusPath;
    std::wstring iniPath;
    std::wstring dllPath;

    bool bypassEnabled = true;
    bool allowGbTop = false;

    bool hotkeyEnabled = true;
    UINT hotkeyMods = MOD_CONTROL | MOD_ALT;
    UINT hotkeyVk = 'B';
};

struct State
{
    DWORD studentPid = 0;
    std::wstring studentPath;

    bool injected = false;
    bool controlled = false;
    bool hookFound = false;
    bool hotkeyRegistered = false;

    bool gbTopmost = false;
    bool gbFullscreen = false;
    bool inputLocked = false;

    std::wstring lastMessage;
};

static Config g_cfg;
static State g_state;
static HWND g_hostWnd = nullptr;
static HANDLE g_instanceMutex = nullptr;
static DWORD g_lastInjectTick = 0;
static std::vector<DWORD> g_badPids;

// ---------------------------------------------------------------------------
// 小工具
// ---------------------------------------------------------------------------

static std::wstring ExeDirectory()
{
    WCHAR path[MAX_PATH * 2] = { 0 };
    GetModuleFileNameW(nullptr, path, ARRAYSIZE(path));
    PathRemoveFileSpecW(path);
    return path;
}

static std::wstring DefaultStatusPath()
{
    WCHAR base[MAX_PATH * 2] = { 0 };
    DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", base, ARRAYSIZE(base));
    if (length > 0 && length < ARRAYSIZE(base))
    {
        std::wstring dir = std::wstring(base) + L"\\SteamEYA";
        CreateDirectoryW(dir.c_str(), nullptr);
        return dir + L"\\jiyu-status.txt";
    }
    return ExeDirectory() + L"\\jiyu-status.txt";
}

static std::wstring Sanitize(std::wstring text)
{
    for (auto& ch : text)
    {
        if (ch == L'\r' || ch == L'\n' || ch == L'\t') ch = L' ';
    }
    return text;
}

static DWORD NowTick()
{
    return GetTickCount();
}

static bool IsPidAlive(DWORD pid)
{
    HANDLE h = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!h) return false;
    DWORD code = 0;
    bool alive = GetExitCodeProcess(h, &code) && code == STILL_ACTIVE;
    CloseHandle(h);
    return alive;
}

static bool IsBadPid(DWORD pid)
{
    for (auto it = g_badPids.begin(); it != g_badPids.end();)
    {
        if (!IsPidAlive(*it)) it = g_badPids.erase(it);
        else ++it;
    }
    for (DWORD bad : g_badPids)
    {
        if (bad == pid) return true;
    }
    return false;
}

static std::wstring KeyName(UINT vk)
{
    if ((vk >= 'A' && vk <= 'Z') || (vk >= '0' && vk <= '9'))
    {
        wchar_t c[2] = { (wchar_t)vk, 0 };
        return c;
    }
    if (vk >= VK_F1 && vk <= VK_F24)
    {
        wchar_t c[16];
        swprintf_s(c, L"F%u", vk - VK_F1 + 1);
        return c;
    }
    switch (vk)
    {
    case VK_SPACE: return L"Space";
    case VK_RETURN: return L"Enter";
    case VK_ESCAPE: return L"Esc";
    case VK_TAB: return L"Tab";
    case VK_BACK: return L"Backspace";
    case VK_DELETE: return L"Delete";
    case VK_INSERT: return L"Insert";
    case VK_HOME: return L"Home";
    case VK_END: return L"End";
    case VK_PRIOR: return L"PageUp";
    case VK_NEXT: return L"PageDown";
    case VK_LEFT: return L"Left";
    case VK_RIGHT: return L"Right";
    case VK_UP: return L"Up";
    case VK_DOWN: return L"Down";
    case VK_OEM_3: return L"`";
    case VK_OEM_MINUS: return L"-";
    case VK_OEM_PLUS: return L"=";
    case VK_OEM_4: return L"[";
    case VK_OEM_6: return L"]";
    case VK_OEM_5: return L"\\";
    case VK_OEM_1: return L";";
    case VK_OEM_7: return L"'";
    case VK_OEM_COMMA: return L",";
    case VK_OEM_PERIOD: return L".";
    case VK_OEM_2: return L"/";
    default: break;
    }
    wchar_t c[16];
    swprintf_s(c, L"0x%02X", vk);
    return c;
}

static std::wstring HotkeyText()
{
    if (!g_cfg.hotkeyEnabled || g_cfg.hotkeyVk == 0) return L"";
    std::wstring text;
    if (g_cfg.hotkeyMods & MOD_CONTROL) text += L"Ctrl+";
    if (g_cfg.hotkeyMods & MOD_ALT) text += L"Alt+";
    if (g_cfg.hotkeyMods & MOD_SHIFT) text += L"Shift+";
    if (g_cfg.hotkeyMods & MOD_WIN) text += L"Win+";
    text += KeyName(g_cfg.hotkeyVk);
    return text;
}

// ---------------------------------------------------------------------------
// 配置文件（供 Hook 读取的 INI 与宿主自身状态）
// ---------------------------------------------------------------------------

static void WriteHookIni()
{
    if (g_cfg.iniPath.empty()) return;
    WritePrivateProfileStringW(L"JTSettings", L"AllowGbTop",
        g_cfg.allowGbTop ? L"TRUE" : L"FALSE", g_cfg.iniPath.c_str());
    WritePrivateProfileStringW(L"JTSettings", L"AutoIncludeFullWindow", L"FALSE", g_cfg.iniPath.c_str());
    WritePrivateProfileStringW(L"JTSettings", L"DoNotShowVirusWindow", L"TRUE", g_cfg.iniPath.c_str());
    WritePrivateProfileStringW(L"JTSettings", L"AllowMonitor", L"TRUE", g_cfg.iniPath.c_str());
    WritePrivateProfileStringW(L"JTSettings", L"AllowControl", L"FALSE", g_cfg.iniPath.c_str());
    // 这两项默认关闭：否则极域被 Hook 卡住时任务管理器都结束不了，用户只能重启。
    WritePrivateProfileStringW(L"JTSettings", L"ProhibitKillProcess", L"FALSE", g_cfg.iniPath.c_str());
    WritePrivateProfileStringW(L"JTSettings", L"ProhibitCloseWindow", L"FALSE", g_cfg.iniPath.c_str());
}

// 统一转 UTF-8：不要再依赖 ccs=UTF-16LE + fwprintf 写中文，那条路径在非 Unicode locale 下
// 会把中文转成 MBCS 再落盘（出现乱码），且末尾容易带出缓冲残留。
static std::string ToUtf8(const std::wstring& text)
{
    if (text.empty()) return std::string();
    int len = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), (int)text.size(), nullptr, 0, nullptr, nullptr);
    if (len <= 0) return std::string();
    std::string out((size_t)len, '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.c_str(), (int)text.size(), &out[0], len, nullptr, nullptr);
    return out;
}

static void AppendLine(std::string& out, const char* key, const std::string& value)
{
    out += key;
    out += '=';
    out += value;
    out += '\n';
}

static void WriteStatus()
{
    if (g_cfg.statusPath.empty()) return;

    std::wstring temp = g_cfg.statusPath + L".tmp";

    std::string body;
    body.reserve(512);
    AppendLine(body, "student", g_state.studentPid ? "1" : "0");
    AppendLine(body, "pid", std::to_string(g_state.studentPid));
    AppendLine(body, "studentPath", ToUtf8(Sanitize(g_state.studentPath)));
    AppendLine(body, "injected", g_state.injected ? "1" : "0");
    AppendLine(body, "controlled", g_state.controlled ? "1" : "0");
    AppendLine(body, "hook", g_state.hookFound ? "1" : "0");
    AppendLine(body, "bypass", g_cfg.bypassEnabled ? "1" : "0");
    AppendLine(body, "allowTop", g_cfg.allowGbTop ? "1" : "0");
    AppendLine(body, "gbTop", g_state.gbTopmost ? "1" : "0");
    AppendLine(body, "gbFull", g_state.gbFullscreen ? "1" : "0");
    AppendLine(body, "locked", g_state.inputLocked ? "1" : "0");
    AppendLine(body, "hotkeyEnabled", g_cfg.hotkeyEnabled ? "1" : "0");
    AppendLine(body, "hotkeyRegistered", g_state.hotkeyRegistered ? "1" : "0");
    AppendLine(body, "hotkey", ToUtf8(HotkeyText()));
    AppendLine(body, "message", ToUtf8(Sanitize(g_state.lastMessage)));
    AppendLine(body, "updated", std::to_string(NowTick()));

    FILE* fp = nullptr;
    if (_wfopen_s(&fp, temp.c_str(), L"wb") != 0 || !fp) return;
    if (!body.empty()) fwrite(body.data(), 1, body.size(), fp);
    fclose(fp);

    MoveFileExW(temp.c_str(), g_cfg.statusPath.c_str(),
        MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
}

// ---------------------------------------------------------------------------
// 进程间通信
// ---------------------------------------------------------------------------

static void SendCopyData(HWND target, const std::wstring& text, HWND sender)
{
    if (!target || text.empty()) return;
    COPYDATASTRUCT cds = { 0 };
    cds.cbData = (DWORD)((text.size() + 1) * sizeof(wchar_t));
    cds.lpData = (PVOID)text.c_str();
    SendMessageTimeoutW(target, WM_COPYDATA, (WPARAM)sender, (LPARAM)&cds,
        SMTO_ABORTIFHUNG | SMTO_NORMAL, 1000, nullptr);
}

static HWND FindHookWindow()
{
    return FindWindowW(nullptr, kHookWindowTitle);
}

static void SendToHook(const std::wstring& command)
{
    SendCopyData(FindHookWindow(), command, g_hostWnd);
}

// Hook 按 ":" 拆分消息，盘符里的冒号会多拆一段，这里把路径拆成「盘符 + 剩余部分」两段发送。
static void SendIniPathToHook()
{
    if (g_cfg.iniPath.empty()) return;
    size_t colon = g_cfg.iniPath.find(L':');
    if (colon == std::wstring::npos)
    {
        SendToHook(L"hk:inipath:" + g_cfg.iniPath);
        return;
    }
    SendToHook(L"hk:inipath:" + g_cfg.iniPath.substr(0, colon) + L":" + g_cfg.iniPath.substr(colon + 1));
}

// ---------------------------------------------------------------------------
// 极域进程定位与注入
// ---------------------------------------------------------------------------

typedef LONG(NTAPI* fnNtOpenProcess)(PHANDLE, ACCESS_MASK, PVOID, PVOID);
struct NtClientId { HANDLE UniqueProcess; HANDLE UniqueThread; };

static HANDLE OpenTargetProcess(DWORD pid)
{
    static fnNtOpenProcess ntOpen =
        (fnNtOpenProcess)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtOpenProcess");
    if (ntOpen)
    {
        struct
        {
            ULONG Length;
            HANDLE RootDirectory;
            PVOID ObjectName;
            ULONG Attributes;
            PVOID SecurityDescriptor;
            PVOID SecurityQualityOfService;
        } oa = { sizeof(oa), nullptr, nullptr, 0, nullptr, nullptr };

        NtClientId cid = { (HANDLE)(ULONG_PTR)pid, nullptr };
        HANDLE process = nullptr;
        if (ntOpen(&process, PROCESS_ALL_ACCESS, &oa, &cid) >= 0 && process)
        {
            return process;
        }
    }
    return OpenProcess(PROCESS_ALL_ACCESS, FALSE, pid);
}

static void EnableDebugPrivilege()
{
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &token)) return;

    TOKEN_PRIVILEGES tp = { 0 };
    tp.PrivilegeCount = 1;
    tp.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
    if (LookupPrivilegeValueW(nullptr, SE_DEBUG_NAME, &tp.Privileges[0].Luid))
    {
        AdjustTokenPrivileges(token, FALSE, &tp, sizeof(tp), nullptr, nullptr);
    }
    CloseHandle(token);
}

static bool InjectDll(DWORD pid, const std::wstring& dllPath)
{
    if (dllPath.empty() || GetFileAttributesW(dllPath.c_str()) == INVALID_FILE_ATTRIBUTES)
    {
        g_state.lastMessage = L"注入失败：找不到 Hook DLL";
        return false;
    }

    HANDLE process = OpenTargetProcess(pid);
    if (!process)
    {
        g_state.lastMessage = L"注入失败：无法打开极域进程";
        return false;
    }

    const SIZE_T size = (dllPath.size() + 1) * sizeof(wchar_t);
    void* remote = VirtualAllocEx(process, nullptr, size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote)
    {
        CloseHandle(process);
        g_state.lastMessage = L"注入失败：远程内存分配失败";
        return false;
    }

    bool ok = WriteProcessMemory(process, remote, dllPath.c_str(), size, nullptr) != FALSE;
    if (ok)
    {
        auto loadLibrary = (LPTHREAD_START_ROUTINE)GetProcAddress(
            GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW");
        HANDLE thread = CreateRemoteThread(process, nullptr, 0, loadLibrary, remote, 0, nullptr);
        if (thread)
        {
            DWORD result = 0;
            WaitForSingleObject(thread, 5000);
            GetExitCodeThread(thread, &result);
            ok = result != 0;
            CloseHandle(thread);
        }
        else
        {
            ok = false;
        }
    }

    VirtualFreeEx(process, remote, 0, MEM_RELEASE);
    CloseHandle(process);

    g_state.lastMessage = ok ? L"已向极域注入 Hook" : L"注入失败：远程线程创建/加载失败";
    return ok;
}

static bool FindStudentMain(DWORD* outPid, std::wstring* outPath)
{
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return false;

    PROCESSENTRY32W entry = { 0 };
    entry.dwSize = sizeof(entry);
    bool found = false;

    for (BOOL ok = Process32FirstW(snapshot, &entry); ok; ok = Process32NextW(snapshot, &entry))
    {
        if (_wcsicmp(entry.szExeFile, L"StudentMain.exe") != 0) continue;

        DWORD pid = entry.th32ProcessID;
        if (pid <= 4 || IsBadPid(pid)) continue;

        HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
        if (!process) continue;

        WCHAR path[MAX_PATH * 2] = { 0 };
        DWORD length = ARRAYSIZE(path);
        bool hasPath = QueryFullProcessImageNameW(process, 0, path, &length) != FALSE;
        CloseHandle(process);

        if (!hasPath) continue;

        // 与上游一致：同目录存在 LibTDMaster.dll 才视为真正的极域学生端，避免误注入同名进程。
        std::wstring dir = path;
        size_t slash = dir.find_last_of(L'\\');
        if (slash != std::wstring::npos) dir.erase(slash);
        std::wstring marker = dir + L"\\LibTDMaster.dll";
        if (GetFileAttributesW(marker.c_str()) == INVALID_FILE_ATTRIBUTES)
        {
            g_badPids.push_back(pid);
            continue;
        }

        *outPid = pid;
        *outPath = path;
        found = true;
        break;
    }

    CloseHandle(snapshot);
    return found;
}

// ---------------------------------------------------------------------------
// 广播窗口枚举：只把句柄交给 Hook，窗口处理全部由 Hook 完成
// ---------------------------------------------------------------------------

static bool IsJiYuWindowText(const wchar_t* text)
{
    if (!text) return false;
    return wcsstr(text, L"广播") || wcsstr(text, L"演示") || wcsstr(text, L"共享")
        || wcscmp(text, L"屏幕演播室窗口") == 0 || wcscmp(text, L"BlackScreen Window") == 0;
}

static BOOL CALLBACK EnumJiYuWindowsProc(HWND hWnd, LPARAM lParam)
{
    DWORD studentPid = (DWORD)lParam;
    if (!IsWindowVisible(hWnd)) return TRUE;

    DWORD pid = 0;
    GetWindowThreadProcessId(hWnd, &pid);
    if (pid != studentPid) return TRUE;

    WCHAR text[128] = { 0 };
    GetWindowTextW(hWnd, text, ARRAYSIZE(text));
    if (!IsJiYuWindowText(text)) return TRUE;

    WCHAR command[64];
    swprintf_s(command, L"hw:%lu", (unsigned long)(ULONG_PTR)hWnd);
    SendToHook(command);
    return TRUE;
}

// ---------------------------------------------------------------------------
// 热键与命令处理
// ---------------------------------------------------------------------------

static void ApplyHotkey()
{
    UnregisterHotKey(g_hostWnd, kHotkeyId);
    g_state.hotkeyRegistered = false;

    if (!g_cfg.hotkeyEnabled || g_cfg.hotkeyVk == 0) return;

    if (RegisterHotKey(g_hostWnd, kHotkeyId, g_cfg.hotkeyMods | MOD_NOREPEAT, g_cfg.hotkeyVk))
    {
        g_state.hotkeyRegistered = true;
    }
    else
    {
        g_state.lastMessage = L"老板键注册失败，可能已被其它程序占用";
    }
}

static void TriggerBossKey()
{
    if (!g_state.controlled && !g_state.hookFound)
    {
        g_state.lastMessage = L"老板键：Hook 尚未连接";
        WriteStatus();
        return;
    }
    SendToHook(L"hk:boss");
    g_state.lastMessage = L"老板键已触发";
    WriteStatus();
}

static void SetBypassEnabled(bool enabled)
{
    g_cfg.bypassEnabled = enabled;
    if (enabled)
    {
        if (g_state.hookFound) SendToHook(L"hk:load");
        else if (g_state.studentPid) g_lastInjectTick = 0;
    }
    else
    {
        SendToHook(L"hk:pause");
        g_state.controlled = false;
        g_state.gbTopmost = false;
        g_state.gbFullscreen = false;
        g_state.inputLocked = false;
        g_state.lastMessage = L"已暂停极域绕过";
    }
}

static void ApplyHotkeyConfig(const std::wstring& command)
{
    // cfg:hotkey:<enabled>:<mods>:<vk>
    unsigned enabled = 0, mods = 0, vk = 0;
    if (swscanf_s(command.c_str(), L"cfg:hotkey:%u:%u:%u", &enabled, &mods, &vk) == 3)
    {
        g_cfg.hotkeyEnabled = enabled != 0;
        g_cfg.hotkeyMods = mods & (MOD_ALT | MOD_CONTROL | MOD_SHIFT | MOD_WIN);
        g_cfg.hotkeyVk = vk;
        ApplyHotkey();
        WriteStatus();
    }
}

static void HandleClientCommand(const std::wstring& command)
{
    if (command.rfind(L"cfg:bypass:", 0) == 0)
    {
        SetBypassEnabled(command.size() > 11 && command[11] == L'1');
        WriteStatus();
    }
    else if (command.rfind(L"cfg:allowtop:", 0) == 0)
    {
        g_cfg.allowGbTop = command.size() > 13 && command[13] == L'1';
        WriteHookIni();
        SendToHook(L"hk:reset");
        WriteStatus();
    }
    else if (command.rfind(L"cfg:hotkey:", 0) == 0)
    {
        ApplyHotkeyConfig(command);
    }
    else if (command == L"cmd:boss")
    {
        TriggerBossKey();
    }
    else if (command == L"cmd:status")
    {
        if (g_state.hookFound) SendToHook(L"hk:stat");
        WriteStatus();
    }
}

static void HandleHookMessage(const std::wstring& message)
{
    if (message == L"hkb:succ")
    {
        g_state.controlled = true;
        g_state.hookFound = true;
        g_state.lastMessage = L"Hook 已连接，极域已被控制";
    }
    else if (message == L"hkb:paused")
    {
        g_state.controlled = false;
        g_state.lastMessage = L"极域绕过已暂停";
    }
    else if (message == L"hkb:immck")
    {
        // 不要在 WM_COPYDATA 的栈上枚举窗口：EnumWindows 里的 GetWindowTextW 是跨进程同步消息，
        // 会把 Hook 那侧（极域 UI 线程）的 SendMessageTimeout 一起拖住。改成投递到自己的消息队列。
        PostMessageW(g_hostWnd, WM_APP_ENUM_WINDOWS, 0, 0);
    }
    else if (message.rfind(L"hkb:jyk:", 0) == 0)
    {
        g_state.inputLocked = message.size() > 8 && message[8] == L'1';
    }
    else if (message.rfind(L"hkb:stat:", 0) == 0)
    {
        // hkb:stat:<top>:<full>
        unsigned top = 0, full = 0;
        if (swscanf_s(message.c_str(), L"hkb:stat:%u:%u", &top, &full) == 2)
        {
            g_state.gbTopmost = top != 0;
            g_state.gbFullscreen = full != 0;
        }
    }
    else if (message == L"hkb:gbtop")
    {
        g_state.gbTopmost = true;
    }
    else if (message == L"hkb:gbuntop")
    {
        g_state.gbTopmost = false;
    }
    else if (message == L"hkb:gbmfull")
    {
        g_state.gbFullscreen = true;
        g_state.gbTopmost = true;
    }
    else if (message == L"hkb:gbmnofull")
    {
        g_state.gbFullscreen = false;
    }
    else if (message.rfind(L"hkb:wtf:", 0) == 0)
    {
        unsigned pid = 0;
        if (swscanf_s(message.c_str(), L"hkb:wtf:%u", &pid) == 1)
        {
            g_badPids.push_back((DWORD)pid);
        }
        g_state.studentPid = 0;
        g_state.injected = false;
        g_state.controlled = false;
        g_state.hookFound = false;
        g_state.lastMessage = L"注入到了非极域进程，已排除";
    }
    else if (message.rfind(L"vback:", 0) == 0)
    {
        g_state.lastMessage = message;
    }
    else if (message.rfind(L"wcd", 0) == 0)
    {
        // 心跳，无需处理
    }
}

// ---------------------------------------------------------------------------
// 周期任务
// ---------------------------------------------------------------------------

static void Tick()
{
    if (!g_cfg.bypassEnabled)
    {
        if (!g_state.studentPid || !IsPidAlive(g_state.studentPid))
        {
            DWORD pid = 0;
            std::wstring path;
            if (FindStudentMain(&pid, &path))
            {
                g_state.studentPid = pid;
                g_state.studentPath = path;
            }
            else
            {
                g_state.studentPid = 0;
                g_state.studentPath.clear();
            }
        }
        g_state.hookFound = FindHookWindow() != nullptr;
        WriteStatus();
        return;
    }

    DWORD pid = 0;
    std::wstring path;
    if (!FindStudentMain(&pid, &path))
    {
        if (g_state.studentPid)
        {
            g_state.studentPid = 0;
            g_state.studentPath.clear();
            g_state.injected = false;
            g_state.controlled = false;
            g_state.gbTopmost = false;
            g_state.gbFullscreen = false;
            g_state.inputLocked = false;
            g_state.lastMessage = L"极域电子教室未运行";
        }
        g_state.hookFound = FindHookWindow() != nullptr;
        WriteStatus();
        return;
    }

    if (pid != g_state.studentPid)
    {
        g_state.studentPid = pid;
        g_state.studentPath = path;
        g_state.injected = false;
        g_state.controlled = false;
        g_state.hookFound = false;
        g_lastInjectTick = 0;
    }

    HWND hookWindow = FindHookWindow();
    g_state.hookFound = hookWindow != nullptr;

    if (hookWindow)
    {
        // 客户端重启但已注入的 Hook 还留在极域进程里：不重复注入，重新建立配置与状态同步。
        if (!g_state.injected) g_state.injected = true;
        if (!g_state.controlled)
        {
            SendIniPathToHook();
            SendToHook(L"hk:load");
            g_state.controlled = true;
            g_state.lastMessage = L"Hook 已在极域中运行";
        }

        EnumWindows(EnumJiYuWindowsProc, (LPARAM)pid);
        SendToHook(L"hk:stat");
    }
    else if (!g_state.injected && (g_lastInjectTick == 0 || NowTick() - g_lastInjectTick > 5000))
    {
        g_lastInjectTick = NowTick();
        WriteHookIni();
        if (InjectDll(pid, g_cfg.dllPath))
        {
            g_state.injected = true;
            SendIniPathToHook();
        }
    }

    WriteStatus();
}

// ---------------------------------------------------------------------------
// 主窗口
// ---------------------------------------------------------------------------

static LRESULT CALLBACK HostWndProc(HWND hWnd, UINT message, WPARAM wParam, LPARAM lParam)
{
    switch (message)
    {
    case WM_COPYDATA:
    {
        auto cds = (PCOPYDATASTRUCT)lParam;
        if (!cds || !cds->lpData || cds->cbData < sizeof(wchar_t)) return 0;

        std::wstring text((const wchar_t*)cds->lpData,
            cds->cbData / sizeof(wchar_t));
        while (!text.empty() && text.back() == L'\0') text.pop_back();

        if (text.rfind(L"hkb:", 0) == 0 || text.rfind(L"vback:", 0) == 0 || text.rfind(L"wcd", 0) == 0)
        {
            HandleHookMessage(text);
        }
        else
        {
            HandleClientCommand(text);
        }
        return TRUE;
    }

    case WM_HOTKEY:
        if ((int)wParam == kHotkeyId)
        {
            TriggerBossKey();
        }
        return 0;

    case WM_APP_ENUM_WINDOWS:
        if (g_state.studentPid)
        {
            EnumWindows(EnumJiYuWindowsProc, (LPARAM)g_state.studentPid);
        }
        return 0;

    case WM_TIMER:
        if (wParam == kTimerTick)
        {
            Tick();
        }
        return 0;

    case WM_ENDSESSION:
    case WM_DESTROY:
        KillTimer(hWnd, kTimerTick);
        UnregisterHotKey(hWnd, kHotkeyId);
        PostQuitMessage(0);
        return 0;

    default:
        return DefWindowProcW(hWnd, message, wParam, lParam);
    }
}

static bool ParseArgs(int argc, wchar_t** argv)
{
    g_cfg.statusPath = DefaultStatusPath();
    g_cfg.iniPath = ExeDirectory() + L"\\jiyu.ini";
    g_cfg.dllPath = ExeDirectory() + L"\\SteamEya.JiYuHook.dll";

    for (int i = 1; i < argc; ++i)
    {
        std::wstring key = argv[i];
        if (i + 1 >= argc) break;
        std::wstring value = argv[++i];

        if (key == L"--status") g_cfg.statusPath = value;
        else if (key == L"--ini") g_cfg.iniPath = value;
        else if (key == L"--dll") g_cfg.dllPath = value;
    }
    return true;
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int)
{
    EnableDebugPrivilege();

    int argc = 0;
    wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (argv) ParseArgs(argc, argv);
    if (argv) LocalFree(argv);

    g_instanceMutex = CreateMutexW(nullptr, TRUE, L"SteamEYA.JiYuHost.SingleInstance");
    if (g_instanceMutex && GetLastError() == ERROR_ALREADY_EXISTS)
    {
        CloseHandle(g_instanceMutex);
        return 0;
    }

    WNDCLASSEXW wc = { 0 };
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = HostWndProc;
    wc.hInstance = instance;
    wc.lpszClassName = kHostClassName;
    RegisterClassExW(&wc);

    g_hostWnd = CreateWindowExW(WS_EX_TOOLWINDOW, kHostClassName, kHostWindowTitle,
        0, 0, 0, 0, 0, nullptr, nullptr, instance, nullptr);
    if (!g_hostWnd)
    {
        if (g_instanceMutex) CloseHandle(g_instanceMutex);
        return 1;
    }

    ApplyHotkey();
    WriteHookIni();
    WriteStatus();

    SetTimer(g_hostWnd, kTimerTick, 2000, nullptr);
    Tick();

    MSG msg = { 0 };
    while (GetMessageW(&msg, nullptr, 0, 0) > 0)
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    if (g_instanceMutex)
    {
        ReleaseMutex(g_instanceMutex);
        CloseHandle(g_instanceMutex);
    }
    return 0;
}
