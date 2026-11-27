// SteamEYA 灵动岛通知窗口（自绘，Win32 分层窗口 + Direct2D）。
//
// 只服务本软件的通知，不监听系统通知。协议是命名管道上的简单二进制帧，
// C# 侧见 Services/IslandService.cs。
#include <windows.h>
#include <windowsx.h>
#include <d2d1.h>
#include <d2d1helper.h>
#include <dwrite.h>
#include <wincodec.h>
#include <shellapi.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <wrl/client.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cwchar>
#include <deque>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "dwrite.lib")
#pragma comment(lib, "windowscodecs.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "shcore.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")

using Microsoft::WRL::ComPtr;

namespace {

constexpr wchar_t kWindowClass[] = L"SteamEYA.Island.Window";
constexpr UINT WM_APP_SHOW = WM_APP + 1;
constexpr UINT WM_APP_PIPE_LOST = WM_APP + 2;

constexpr float kCompactW = 340.0f, kCompactH = 62.0f;
constexpr float kExpandedW = 404.0f, kExpandedH = 208.0f;
constexpr float kTopMargin = 8.0f;
constexpr UINT_PTR kAnimTimer = 1, kHideTimer = 2, kHoverTimer = 3;
constexpr int kAnimIntervalMs = 15;
constexpr int kMaxQueue = 5;

struct Notice {
    std::wstring id;
    std::wstring heading;
    std::wstring title;
    std::wstring body;
    std::wstring avatarPath;
    std::wstring glyph;
    std::wstring coalesceKey;
    std::wstring action;
    int durationMs = 9000;
    bool actionSent = false;   // 同一条通知只允许发一次点击，避免重复/双击重复触发
};

HANDLE g_pipe = INVALID_HANDLE_VALUE;
std::mutex g_pipeWriteLock;
HWND g_hwnd = nullptr;
HWND g_mainWnd = nullptr;   // 主程序窗口（--hwnd 传入），点击时直接把它带到前台
UINT g_dpi = 96;

// 岛进程自己的小日志（%TEMP%\SteamEYA-island.log），用于确认点击是否真的发生。
void IslandLog(const wchar_t* message) {
    wchar_t temp[MAX_PATH]{};
    DWORD n = GetTempPathW(MAX_PATH, temp);
    if (n == 0 || n >= MAX_PATH) return;
    std::wstring path = std::wstring(temp) + L"SteamEYA-island.log";
    HANDLE file = CreateFileW(path.c_str(), FILE_APPEND_DATA,
                              FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                              OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    SYSTEMTIME st{};
    GetLocalTime(&st);
    wchar_t line[512]{};
    swprintf_s(line, _countof(line), L"%04d-%02d-%02d %02d:%02d:%02d.%03d %s\r\n",
               st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond,
               st.wMilliseconds, message);
    DWORD written = 0;
    WriteFile(file, line, static_cast<DWORD>(wcslen(line) * sizeof(wchar_t)), &written, nullptr);
    CloseHandle(file);
}

// 把主程序窗口恢复并置前；即使随后的管道写入有问题，用户也能看到窗口弹出来。
void ActivateMainWindow() {
    if (!g_mainWnd || !IsWindow(g_mainWnd)) return;
    ShowWindowAsync(g_mainWnd, IsIconic(g_mainWnd) ? SW_RESTORE : SW_SHOW);
    SetForegroundWindow(g_mainWnd);
}

std::wstring Utf8ToWide(const std::string& value) {
    if (value.empty()) return {};
    int len = MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (len <= 0) return {};
    std::wstring out(static_cast<size_t>(len), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), out.data(), len);
    return out;
}

std::string WideToUtf8(const std::wstring& value) {
    if (value.empty()) return {};
    int len = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()),
                                  nullptr, 0, nullptr, nullptr);
    if (len <= 0) return {};
    std::string out(static_cast<size_t>(len), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()),
                        out.data(), len, nullptr, nullptr);
    return out;
}

bool ReadExact(HANDLE pipe, void* data, DWORD size) {
    auto* ptr = static_cast<char*>(data);
    while (size > 0) {
        DWORD got = 0;
        if (!ReadFile(pipe, ptr, size, &got, nullptr) || got == 0) return false;
        ptr += got;
        size -= got;
    }
    return true;
}

bool ReadString(HANDLE pipe, std::wstring& out) {
    uint32_t size = 0;
    if (!ReadExact(pipe, &size, sizeof(size)) || size > 65536) return false;
    if (size == 0) {
        out.clear();
        return true;
    }
    std::string utf8(size, '\0');
    if (!ReadExact(pipe, utf8.data(), size)) return false;
    out = Utf8ToWide(utf8);
    return true;
}

// 调用方必须持有 g_pipeWriteLock。
bool WriteFrameLocked(const std::wstring& action, const std::wstring& id, uint64_t sentAt) {
    if (g_pipe == INVALID_HANDLE_VALUE) return false;
    std::string actionUtf8 = WideToUtf8(action);
    std::string idUtf8 = WideToUtf8(id);
    uint32_t payloadSize = 1 + 4 + static_cast<uint32_t>(actionUtf8.size()) +
                           4 + static_cast<uint32_t>(idUtf8.size()) + 8;
    std::vector<char> buffer(4 + payloadSize);
    char* p = buffer.data();
    memcpy(p, &payloadSize, 4);
    p += 4;
    *p++ = 2;  // action
    uint32_t n = static_cast<uint32_t>(actionUtf8.size());
    memcpy(p, &n, 4); p += 4;
    memcpy(p, actionUtf8.data(), n); p += n;
    n = static_cast<uint32_t>(idUtf8.size());
    memcpy(p, &n, 4); p += 4;
    memcpy(p, idUtf8.data(), n); p += n;
    memcpy(p, &sentAt, 8);   // 点击发生时刻（GetTickCount64），C# 据此丢弃过期帧
    DWORD written = 0;
    return WriteFile(g_pipe, buffer.data(), static_cast<DWORD>(buffer.size()), &written, nullptr) != FALSE;
}

// 点击动作绝不能在 UI 线程做同步 IPC：管道另一端一旦不读，WriteFile 会把岛的消息循环
// 卡住，表现为岛窗口「未响应」而主程序没事。这里丢给后台线程写，UI 立即返回。
void SendActionAsync(std::wstring action, std::wstring id, uint64_t sentAt) {
    std::thread([action = std::move(action), id = std::move(id), sentAt]() {
        bool ok = false;
        {
            std::lock_guard<std::mutex> lock(g_pipeWriteLock);
            ok = WriteFrameLocked(action, id, sentAt);
        }
        IslandLog(ok ? L"click frame written" : L"click frame write failed");
    }).detach();
}

// ---------------------------------------------------------------- 渲染

struct Renderer {
    ComPtr<ID2D1Factory> d2d;
    ComPtr<IDWriteFactory> dwrite;
    ComPtr<IWICImagingFactory> wic;
    ComPtr<ID2D1DCRenderTarget> target;
    ComPtr<ID2D1SolidColorBrush> fill;
    ComPtr<ID2D1SolidColorBrush> border;
    ComPtr<ID2D1SolidColorBrush> text;
    ComPtr<ID2D1SolidColorBrush> muted;
    ComPtr<ID2D1SolidColorBrush> accent;
    ComPtr<ID2D1Bitmap> avatarBitmap;
    std::wstring avatarPath;
    std::map<std::pair<std::wstring, int>, ComPtr<IDWriteTextFormat>> formats;
    HBITMAP dib = nullptr;
    HDC mem = nullptr;
    HGDIOBJ oldBitmap = nullptr;
    void* bits = nullptr;
    int width = 0, height = 0;
    UINT dpi = 96;

    bool Init(UINT dpiValue) {
        if (FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED,
                                     __uuidof(ID2D1Factory), nullptr,
                                     reinterpret_cast<void**>(d2d.GetAddressOf())))) {
            return false;
        }
        if (FAILED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
                                       reinterpret_cast<IUnknown**>(dwrite.GetAddressOf())))) {
            return false;
        }
        if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                    IID_PPV_ARGS(wic.GetAddressOf())))) {
            return false;
        }
        dpi = dpiValue;
        return CreateSurface();
    }

    bool CreateSurface() {
        ReleaseSurface();
        float scale = static_cast<float>(dpi) / 96.0f;
        width = static_cast<int>(std::ceil(kExpandedW * scale)) + 2;
        height = static_cast<int>(std::ceil(kExpandedH * scale)) + 2;

        BITMAPINFO bi{};
        bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bi.bmiHeader.biWidth = width;
        bi.bmiHeader.biHeight = -height;
        bi.bmiHeader.biPlanes = 1;
        bi.bmiHeader.biBitCount = 32;
        bi.bmiHeader.biCompression = BI_RGB;
        dib = CreateDIBSection(nullptr, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
        if (!dib) return false;
        mem = CreateCompatibleDC(nullptr);
        if (!mem) return false;
        oldBitmap = SelectObject(mem, dib);

        D2D1_RENDER_TARGET_PROPERTIES props = D2D1::RenderTargetProperties(
            D2D1_RENDER_TARGET_TYPE_DEFAULT,
            D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED),
            static_cast<float>(dpi), static_cast<float>(dpi));
        if (FAILED(d2d->CreateDCRenderTarget(&props, target.ReleaseAndGetAddressOf()))) {
            return false;
        }
        RECT rc{0, 0, width, height};
        if (FAILED(target->BindDC(mem, &rc))) return false;

        target->CreateSolidColorBrush(D2D1::ColorF(0x050608, 0.97f), &fill);
        target->CreateSolidColorBrush(D2D1::ColorF(0xFFFFFF, 0.08f), &border);
        target->CreateSolidColorBrush(D2D1::ColorF(0xF1F3FA, 1.0f), &text);
        target->CreateSolidColorBrush(D2D1::ColorF(0x8E98AB, 1.0f), &muted);
        target->CreateSolidColorBrush(D2D1::ColorF(0x6E92F7, 1.0f), &accent);
        return true;
    }

    void ReleaseSurface() {
        target.Reset();
        fill.Reset(); border.Reset(); text.Reset(); muted.Reset(); accent.Reset();
        avatarBitmap.Reset();
        avatarPath.clear();
        formats.clear();
        if (mem && oldBitmap) SelectObject(mem, oldBitmap);
        if (dib) DeleteObject(dib);
        if (mem) DeleteDC(mem);
        dib = nullptr; mem = nullptr; oldBitmap = nullptr; bits = nullptr;
        width = height = 0;
    }

    ~Renderer() { ReleaseSurface(); }

    ComPtr<IDWriteTextFormat> Format(float size, DWRITE_FONT_WEIGHT weight,
                                     const wchar_t* family = L"Segoe UI") {
        int keySize = static_cast<int>(std::lround(size * 100));
        auto key = std::make_pair(std::wstring(family), keySize * 10 + static_cast<int>(weight));
        auto found = formats.find(key);
        if (found != formats.end()) return found->second;

        ComPtr<IDWriteTextFormat> format;
        dwrite->CreateTextFormat(family, nullptr, weight, DWRITE_FONT_STYLE_NORMAL,
                                 DWRITE_FONT_STRETCH_NORMAL, size, L"zh-CN", &format);
        if (format) {
            format->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
            formats.emplace(key, format);
        }
        return format;
    }

    void TextLine(const std::wstring& value, float x, float y, float w, float h,
                  float size, DWRITE_FONT_WEIGHT weight, ID2D1Brush* brush,
                  const wchar_t* family = L"Segoe UI") {
        if (value.empty() || w <= 0) return;
        auto format = Format(size, weight, family);
        if (!format) return;
        ComPtr<IDWriteTextLayout> layout;
        if (FAILED(dwrite->CreateTextLayout(value.c_str(), static_cast<UINT32>(value.size()),
                                            format.Get(), w, h, &layout))) {
            return;
        }
        DWRITE_TRIMMING trimming{DWRITE_TRIMMING_GRANULARITY_CHARACTER, 0, 0};
        ComPtr<IDWriteInlineObject> sign;
        if (SUCCEEDED(dwrite->CreateEllipsisTrimmingSign(format.Get(), &sign))) {
            layout->SetTrimming(&trimming, sign.Get());
        }
        layout->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
        target->DrawTextLayout(D2D1::Point2F(x, y), layout.Get(), brush);
    }

    void TextWrapped(const std::wstring& value, float x, float y, float w, float h,
                     float size, DWRITE_FONT_WEIGHT weight, ID2D1Brush* brush) {
        if (value.empty() || w <= 0 || h <= 0) return;
        auto format = Format(size, weight);
        if (!format) return;
        format->SetWordWrapping(DWRITE_WORD_WRAPPING_WRAP);
        ComPtr<IDWriteTextLayout> layout;
        if (FAILED(dwrite->CreateTextLayout(value.c_str(), static_cast<UINT32>(value.size()),
                                            format.Get(), w, h, &layout))) {
            return;
        }
        target->DrawTextLayout(D2D1::Point2F(x, y), layout.Get(), brush);
    }

    void Avatar(const std::wstring& path, float x, float y, float size, const std::wstring& glyph) {
        if (path != avatarPath) {
            avatarPath = path;
            avatarBitmap.Reset();
            if (!path.empty()) {
                ComPtr<IWICBitmapDecoder> decoder;
                if (SUCCEEDED(wic->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ,
                                                             WICDecodeMetadataCacheOnDemand, &decoder))) {
                    ComPtr<IWICBitmapFrameDecode> frame;
                    if (SUCCEEDED(decoder->GetFrame(0, &frame))) {
                        ComPtr<IWICFormatConverter> converter;
                        if (SUCCEEDED(wic->CreateFormatConverter(&converter)) &&
                            SUCCEEDED(converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppPBGRA,
                                                           WICBitmapDitherTypeNone, nullptr, 0,
                                                           WICBitmapPaletteTypeCustom))) {
                            target->CreateBitmapFromWicBitmap(converter.Get(), nullptr, &avatarBitmap);
                        }
                    }
                }
            }
        }

        float cx = x + size / 2.0f, cy = y + size / 2.0f, r = size / 2.0f;
        if (avatarBitmap) {
            ComPtr<ID2D1Layer> layer;
            target->CreateLayer(nullptr, &layer);
            ComPtr<ID2D1EllipseGeometry> mask;
            d2d->CreateEllipseGeometry(D2D1::Ellipse(D2D1::Point2F(cx, cy), r, r), &mask);
            D2D1_LAYER_PARAMETERS params{};
            params.contentBounds = D2D1::InfiniteRect();
            params.geometricMask = mask.Get();
            params.maskAntialiasMode = D2D1_ANTIALIAS_MODE_PER_PRIMITIVE;
            // 这两个字段默认是 0：opacity=0 会让头像整层透明，零矩阵会把遮罩缩成一个点，
            // 表现就是头像永远显示不出来。必须显式给不透明和单位矩阵。
            params.maskTransform = D2D1::IdentityMatrix();
            params.opacity = 1.0f;
            params.layerOptions = D2D1_LAYER_OPTIONS_NONE;
            if (layer && mask) {
                target->PushLayer(params, layer.Get());
                target->DrawBitmap(avatarBitmap.Get(), D2D1::RectF(x, y, x + size, y + size), 1.0f,
                                   D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
                target->PopLayer();
            } else {
                target->DrawBitmap(avatarBitmap.Get(), D2D1::RectF(x, y, x + size, y + size), 1.0f,
                                   D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
            }
            return;
        }

        target->FillEllipse(D2D1::Ellipse(D2D1::Point2F(cx, cy), r, r), accent.Get());
        if (!glyph.empty()) {
            auto format = Format(size * 0.5f, DWRITE_FONT_WEIGHT_NORMAL, L"Segoe MDL2 Assets");
            if (format) {
                format->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER);
                format->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
                ComPtr<IDWriteTextLayout> layout;
                if (SUCCEEDED(dwrite->CreateTextLayout(glyph.c_str(), static_cast<UINT32>(glyph.size()),
                                                        format.Get(), size, size, &layout))) {
                    target->DrawTextLayout(D2D1::Point2F(x, y), layout.Get(), text.Get());
                }
            }
        }
    }

    void Begin() {
        target->BeginDraw();
        target->Clear(D2D1::ColorF(0.0f, 0.0f, 0.0f, 0.0f));
        target->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
    }

    void End() {
        target->EndDraw();
    }
};

Renderer g_renderer;

// ---------------------------------------------------------------- 状态

enum class Phase { Hidden, Showing, Visible, Hiding };

struct State {
    std::deque<Notice> queue;
    std::unique_ptr<Notice> current;
    Phase phase = Phase::Hidden;

    float w = kCompactW, h = kCompactH;
    float targetW = kCompactW, targetH = kCompactH;
    float vw = 0, vh = 0;
    float alpha = 0;
    bool hover = false;
    bool expanded = false;
    bool closeHover = false;
    bool mouseTracking = false;
    ULONGLONG lastTick = 0;

    void Enqueue(Notice notice) {
        if (!notice.coalesceKey.empty()) {
            if (current && current->coalesceKey == notice.coalesceKey) {
                current->id = notice.id;
                current->heading = notice.heading;
                current->title = notice.title;
                current->body = notice.body;
                current->avatarPath = notice.avatarPath;
                current->glyph = notice.glyph;
                current->action = notice.action;
                current->durationMs = notice.durationMs;
                current->actionSent = false;   // 新消息重置「已发送点击」标记
                if (phase == Phase::Hidden) {
                    Show();
                } else {
                    // 正在淡出时又来了同房间消息：取消淡出，重新显示并按新消息重新计 3 秒，
                    // 否则岛会按第一条消息的时间直接消失。
                    if (phase == Phase::Hiding) {
                        phase = Phase::Visible;
                        alpha = 1.0f;
                        expanded = false;
                        SetTargets(true);
                    } else if (phase == Phase::Visible) {
                        SetTargets(true);
                    }
                    // Showing 阶段保持入场动画，不强制跳尺寸。
                    ResetHideTimer();
                    EnsureAnimTimer();
                    Render();
                }
                return;
            }
            for (auto& item : queue) {
                if (item.coalesceKey == notice.coalesceKey) {
                    item = notice;
                    return;
                }
            }
        }
        if (current) {
            if (queue.size() < static_cast<size_t>(kMaxQueue)) queue.push_back(std::move(notice));
            return;
        }
        current = std::make_unique<Notice>(std::move(notice));
        Show();
    }

    void Show() {
        if (!current) return;
        phase = Phase::Showing;
        w = kCompactW - 30; h = kCompactH - 8;
        vw = vh = 0;
        alpha = 0;
        expanded = false;
        hover = false;
        targetW = kCompactW; targetH = kCompactH;
        ShowWindow(g_hwnd, SW_SHOWNOACTIVATE);
        lastTick = GetTickCount64();
        EnsureAnimTimer();
        ResetHideTimer();
    }

    // 定时器从显示一直保留到 FinishHide；静止时 Tick 不做重绘，开销极小。
    // 不能像之前那样静止就 KillTimer，否则到点收起时没有动画驱动，岛会卡在屏幕上。
    void EnsureAnimTimer() {
        if (lastTick == 0) lastTick = GetTickCount64();
        SetTimer(g_hwnd, kAnimTimer, kAnimIntervalMs, nullptr);
    }

    void SetTargets(bool instant = false) {
        if (expanded) {
            targetW = kExpandedW; targetH = kExpandedH;
        } else {
            targetW = kCompactW; targetH = kCompactH;
        }
        if (instant) {
            w = targetW; h = targetH; vw = vh = 0;
        } else {
            EnsureAnimTimer();
        }
    }

    void ResetHideTimer() {
        KillTimer(g_hwnd, kHideTimer);
        if (current) {
            UINT duration = static_cast<UINT>(std::clamp(current->durationMs, 1000, 60000));
            SetTimer(g_hwnd, kHideTimer, duration, nullptr);
        }
    }

    void Dismiss(bool all = false) {
        if (all) queue.clear();
        if (current) {
            phase = Phase::Hiding;
            targetW = kCompactW - 30;
            targetH = kCompactH - 8;
            KillTimer(g_hwnd, kHideTimer);
            // 淡出必须有动画定时器驱动；之前「静止后停掉定时器」会导致这里永远停在屏幕上。
            EnsureAnimTimer();
        }
    }

    void FinishHide() {
        KillTimer(g_hwnd, kAnimTimer);
        KillTimer(g_hwnd, kHideTimer);
        KillTimer(g_hwnd, kHoverTimer);
        ShowWindow(g_hwnd, SW_HIDE);
        phase = Phase::Hidden;
        current.reset();
        alpha = 0;
        lastTick = 0;
        if (!queue.empty()) {
            current = std::make_unique<Notice>(std::move(queue.front()));
            queue.pop_front();
            Show();
        }
    }

    void ResetToCompact() {
        expanded = false;
        closeHover = false;
        SetTargets();
        ResetHideTimer();
    }

    void Tick() {
        if (lastTick == 0) {
            lastTick = GetTickCount64();
        }
        ULONGLONG now = GetTickCount64();
        float dt = std::min(0.05f, static_cast<float>(now - lastTick) / 1000.0f);
        lastTick = now;
        if (dt <= 0) return;

        const bool wasVisible = phase == Phase::Visible;

        if (phase == Phase::Showing) {
            alpha = std::min(1.0f, alpha + dt * 5.0f);
            if (alpha >= 1.0f && std::fabs(w - targetW) < 1.0f && std::fabs(h - targetH) < 1.0f) {
                phase = Phase::Visible;
            }
        } else if (phase == Phase::Hiding) {
            alpha = std::max(0.0f, alpha - dt * 6.0f);
        }

        // 轻微回弹的弹簧，接近 Lume 的灵动岛手感。
        const float k = 420.0f, c = 30.0f;
        float aw = k * (targetW - w) - c * vw;
        float ah = k * (targetH - h) - c * vh;
        vw += aw * dt;
        vh += ah * dt;
        w += vw * dt;
        h += vh * dt;
        w = std::clamp(w, 260.0f, kExpandedW + 12.0f);
        h = std::clamp(h, 48.0f, kExpandedH + 12.0f);

        if (phase == Phase::Hiding && alpha <= 0.01f) {
            FinishHide();
            return;
        }

        // 只在动画期间重绘；静止仍保留定时器（很轻量），确保后续 Dismiss 一定能淡出。
        const bool settled = std::fabs(w - targetW) < 0.5f && std::fabs(h - targetH) < 0.5f &&
                             std::fabs(vw) < 1.0f && std::fabs(vh) < 1.0f;
        if (!wasVisible || phase != Phase::Visible || !settled) {
            Render();
        }
    }

    void Render() {
        if (!g_renderer.target || !current) return;
        float scale = static_cast<float>(g_dpi) / 96.0f;
        int physW = std::max(1, static_cast<int>(std::lround(w * scale)));
        int physH = std::max(1, static_cast<int>(std::lround(h * scale)));
        float radius = expanded || h > kCompactH + 20 ? 24.0f : h / 2.0f;

        g_renderer.Begin();
        ID2D1RenderTarget* rt = g_renderer.target.Get();

        D2D1_ROUNDED_RECT capsule{D2D1::RectF(0, 0, w, h), radius, radius};
        auto fill = g_renderer.fill;
        auto border = g_renderer.border;
        if (fill) {
            D2D1_COLOR_F color = fill->GetColor();
            color.a = 0.97f * alpha;
            fill->SetColor(color);
            rt->FillRoundedRectangle(capsule, fill.Get());
        }
        if (border) {
            D2D1_COLOR_F color = border->GetColor();
            color.a = 0.08f * alpha;
            border->SetColor(color);
            rt->DrawRoundedRectangle(capsule, border.Get(), 1.0f);
        }

        float expansion = std::clamp((h - kCompactH) / (kExpandedH - kCompactH), 0.0f, 1.0f);
        if (expansion < 0.55f) {
            // 收起态：头像 + 发送者 + 一条消息。
            float icon = 34.0f;
            float cy = h / 2.0f;
            g_renderer.Avatar(current->avatarPath, 17.0f, cy - icon / 2, icon, current->glyph);
            float left = 62.0f;
            float width = std::max(0.0f, w - left - 20.0f);
            if (auto muted = g_renderer.muted) {
                D2D1_COLOR_F color = muted->GetColor();
                color.a = 0.55f * alpha;
                muted->SetColor(color);
                g_renderer.TextLine(current->heading, left, cy - 21, width, 16, 11.5f,
                                    DWRITE_FONT_WEIGHT_NORMAL, muted.Get());
                color.a = 0.80f * alpha;
                muted->SetColor(color);
            }
            if (auto text = g_renderer.text) {
                D2D1_COLOR_F color = text->GetColor();
                color.a = alpha;
                text->SetColor(color);
                g_renderer.TextLine(current->title, left, cy - 4, width, 22, 14.5f,
                                    DWRITE_FONT_WEIGHT_SEMI_BOLD, text.Get());
            }
        } else {
            // 展开态：头像、标题、正文、关闭按钮。
            float pad = 20.0f;
            g_renderer.Avatar(current->avatarPath, pad, 18, 28, current->glyph);
            float headX = 58.0f;
            if (auto muted = g_renderer.muted) {
                D2D1_COLOR_F color = muted->GetColor();
                color.a = 0.55f * alpha;
                muted->SetColor(color);
                g_renderer.TextLine(current->heading, headX, 21, w - headX - 60, 22, 12.0f,
                                    DWRITE_FONT_WEIGHT_NORMAL, muted.Get());
            }
            if (auto text = g_renderer.text) {
                D2D1_COLOR_F color = text->GetColor();
                color.a = alpha;
                text->SetColor(color);
                g_renderer.TextWrapped(current->title, pad, 58, w - pad * 2, 30, 17.0f,
                                       DWRITE_FONT_WEIGHT_SEMI_BOLD, text.Get());
                color.a = 0.82f * alpha;
                text->SetColor(color);
                g_renderer.TextWrapped(current->body, pad, 94, w - pad * 2, h - 122, 14.0f,
                                       DWRITE_FONT_WEIGHT_NORMAL, text.Get());
            }
            // 关闭按钮
            float closeX = w - 42.0f;
            ComPtr<ID2D1SolidColorBrush> closeBrush;
            rt->CreateSolidColorBrush(D2D1::ColorF(0xFFFFFF, (closeHover ? 0.16f : 0.08f) * alpha),
                                      &closeBrush);
            if (closeBrush) {
                rt->FillRoundedRectangle(
                    D2D1::RoundedRect(D2D1::RectF(closeX, 18, closeX + 26, 44), 13, 13),
                    closeBrush.Get());
            }
            ComPtr<ID2D1SolidColorBrush> mark;
            rt->CreateSolidColorBrush(D2D1::ColorF(0xFFFFFF, 0.70f * alpha), &mark);
            if (mark) {
                rt->DrawLine(D2D1::Point2F(closeX + 9, 27), D2D1::Point2F(closeX + 17, 35), mark.Get(), 1.5f);
                rt->DrawLine(D2D1::Point2F(closeX + 17, 27), D2D1::Point2F(closeX + 9, 35), mark.Get(), 1.5f);
            }
        }

        g_renderer.End();

        MONITORINFO mi{};
        mi.cbSize = sizeof(mi);
        HMONITOR monitor = MonitorFromPoint(POINT{0, 0}, MONITOR_DEFAULTTOPRIMARY);
        GetMonitorInfoW(monitor, &mi);
        int x = mi.rcMonitor.left + (mi.rcMonitor.right - mi.rcMonitor.left - physW) / 2;
        int y = mi.rcMonitor.top + static_cast<int>(std::lround(kTopMargin * scale));
        if (phase == Phase::Hiding) y -= static_cast<int>(std::lround((1.0f - alpha) * 8 * scale));

        HDC screen = GetDC(nullptr);
        POINT dst{x, y};
        SIZE size{physW, physH};
        POINT src{0, 0};
        BLENDFUNCTION blend{AC_SRC_OVER, 0, 255, AC_SRC_ALPHA};
        UpdateLayeredWindow(g_hwnd, screen, &dst, &size, g_renderer.mem, &src, 0, &blend, ULW_ALPHA);
        ReleaseDC(nullptr, screen);
    }
};

State g_state;

// ---------------------------------------------------------------- 窗口过程

void OnMouseMove(int px, int py) {
    float scale = static_cast<float>(g_dpi) / 96.0f;
    float x = px / scale, y = py / scale;
    if (!g_state.mouseTracking) {
        TRACKMOUSEEVENT tme{sizeof(tme), TME_LEAVE, g_hwnd, 0};
        TrackMouseEvent(&tme);
        g_state.mouseTracking = true;
    }
    bool closeHover = g_state.expanded && x >= g_state.w - 52 && y <= 50;
    if (closeHover != g_state.closeHover) {
        g_state.closeHover = closeHover;
        g_state.Render();
    }
    if (!g_state.hover) {
        g_state.hover = true;
        KillTimer(g_hwnd, kHideTimer);
        SetTimer(g_hwnd, kHoverTimer, 180, nullptr);
    }
}

void OnMouseLeave() {
    g_state.hover = false;
    g_state.mouseTracking = false;
    g_state.closeHover = false;
    KillTimer(g_hwnd, kHoverTimer);
    if (g_state.expanded) {
        g_state.ResetToCompact();
    }
    g_state.ResetHideTimer();
    InvalidateRect(g_hwnd, nullptr, FALSE);
}

void OnClick(int px, int py) {
    if (g_state.phase == Phase::Hiding) {
        return;   // 淡出中，忽略点击，避免同一次点击重复发帧
    }
    float scale = static_cast<float>(g_dpi) / 96.0f;
    float x = px / scale, y = py / scale;
    if (g_state.expanded && x >= g_state.w - 52 && y <= 50) {
        g_state.Dismiss();
        return;
    }
    // 单击即跳转，不再要求「第一次展开、第二次才生效」；悬停仍然负责展开看正文。
    IslandLog(L"click activate");
    ActivateMainWindow();
    if (g_state.current && !g_state.current->action.empty() && !g_state.current->actionSent) {
        g_state.current->actionSent = true;
        SendActionAsync(g_state.current->action, g_state.current->id, GetTickCount64());
    }
    g_state.Dismiss();
}

LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
    case WM_CREATE:
        g_hwnd = hwnd;
        g_dpi = GetDpiForWindow(hwnd);
        if (!g_renderer.Init(g_dpi)) {
            return -1;
        }
        return 0;
    case WM_TIMER:
        if (wp == kAnimTimer) {
            g_state.Tick();
        } else if (wp == kHideTimer) {
            if (!g_state.hover) g_state.Dismiss();
        } else if (wp == kHoverTimer) {
            KillTimer(hwnd, kHoverTimer);
            if (g_state.hover && !g_state.expanded && g_state.current) {
                g_state.expanded = true;
                g_state.SetTargets();
                KillTimer(hwnd, kHideTimer);
            }
        }
        return 0;
    case WM_MOUSEMOVE:
        OnMouseMove(GET_X_LPARAM(lp), GET_Y_LPARAM(lp));
        return 0;
    case WM_MOUSELEAVE:
        OnMouseLeave();
        return 0;
    case WM_LBUTTONUP:
        OnClick(GET_X_LPARAM(lp), GET_Y_LPARAM(lp));
        return 0;
    case WM_RBUTTONUP:
        g_state.Dismiss();
        return 0;
    case WM_APP_SHOW: {
        auto* notice = reinterpret_cast<Notice*>(lp);
        if (notice) {
            QUERY_USER_NOTIFICATION_STATE state = QUNS_NOT_PRESENT;
            if (SUCCEEDED(SHQueryUserNotificationState(&state)) &&
                (state == QUNS_ACCEPTS_NOTIFICATIONS || state == QUNS_BUSY || state == QUNS_APP)) {
                g_state.Enqueue(std::move(*notice));
                g_state.SetTargets();
                g_state.Render();
            }
            delete notice;
        }
        return 0;
    }
    case WM_APP_PIPE_LOST:
        IslandLog(L"pipe lost, exiting");
        DestroyWindow(hwnd);
        return 0;
    case WM_DPICHANGED: {
        g_dpi = HIWORD(wp);
        g_renderer.Init(g_dpi);
        g_state.Render();
        return 0;
    }
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

// ---------------------------------------------------------------- IPC

void PipeReader(HWND hwnd) {
    for (;;) {
        uint32_t size = 0;
        if (!ReadExact(g_pipe, &size, sizeof(size)) || size == 0 || size > 8 * 1024 * 1024) {
            break;
        }
        std::vector<char> payload(size);
        if (!ReadExact(g_pipe, payload.data(), size)) break;
        const char* p = payload.data();
        const char* end = p + size;
        if (p >= end) break;
        unsigned char type = static_cast<unsigned char>(*p++);
        if (type != 1) continue;
        auto readString = [&](std::wstring& out) {
            if (p + 4 > end) return false;
            uint32_t len = 0;
            memcpy(&len, p, 4); p += 4;
            if (len > 65536 || p + len > end) return false;
            out = Utf8ToWide(std::string(p, len));
            p += len;
            return true;
        };
        auto* notice = new Notice();
        if (!readString(notice->id) || !readString(notice->heading) ||
            !readString(notice->title) || !readString(notice->body) ||
            !readString(notice->avatarPath) || !readString(notice->glyph) ||
            !readString(notice->coalesceKey)) {
            delete notice;
            continue;
        }
        if (p + 4 > end) { delete notice; continue; }
        memcpy(&notice->durationMs, p, 4); p += 4;
        if (!readString(notice->action)) { delete notice; continue; }
        PostMessageW(hwnd, WM_APP_SHOW, 0, reinterpret_cast<LPARAM>(notice));
    }
    if (hwnd) PostMessageW(hwnd, WM_APP_PIPE_LOST, 0, 0);
}

bool ConnectPipe(const std::wstring& pipeName, DWORD timeoutMs) {
    ULONGLONG deadline = GetTickCount64() + timeoutMs;
    while (GetTickCount64() < deadline) {
        HANDLE pipe = CreateFileW(pipeName.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                                  OPEN_EXISTING, 0, nullptr);
        if (pipe != INVALID_HANDLE_VALUE) {
            g_pipe = pipe;
            return true;
        }
        Sleep(60);
    }
    return false;
}

void WatchParent(DWORD parentPid, HWND hwnd) {
    HANDLE parent = OpenProcess(SYNCHRONIZE, FALSE, parentPid);
    if (!parent) return;
    WaitForSingleObject(parent, INFINITE);
    CloseHandle(parent);
    PostMessageW(hwnd, WM_CLOSE, 0, 0);
}

}  // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR commandLine, int) {
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

    std::wstring pipeName;
    DWORD parentPid = 0;
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    for (int i = 1; i + 1 < argc; ++i) {
        if (!wcscmp(argv[i], L"--pipe")) pipeName = argv[i + 1];
        else if (!wcscmp(argv[i], L"--parent")) parentPid = wcstoul(argv[i + 1], nullptr, 10);
        else if (!wcscmp(argv[i], L"--hwnd")) g_mainWnd = reinterpret_cast<HWND>(_wcstoui64(argv[i + 1], nullptr, 16));
    }
    if (argv) LocalFree(argv);
    if (pipeName.empty() || !ConnectPipe(pipeName, 3000)) {
        CoUninitialize();
        return 1;
    }

    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = WndProc;
    wc.hInstance = instance;
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    wc.lpszClassName = kWindowClass;
    RegisterClassExW(&wc);

    HWND hwnd = CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
        kWindowClass, L"", WS_POPUP,
        0, 0, 1, 1, nullptr, nullptr, instance, nullptr);
    if (!hwnd) {
        CoUninitialize();
        return 1;
    }

    std::thread(PipeReader, hwnd).detach();
    if (parentPid) std::thread(WatchParent, parentPid, hwnd).detach();
    IslandLog(L"island started");

    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0)) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    if (g_pipe != INVALID_HANDLE_VALUE) CloseHandle(g_pipe);
    CoUninitialize();
    return 0;
}
