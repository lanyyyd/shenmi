// dllmain.cpp : 定义 DLL 应用程序的入口点。
#include "stdafx.h"
#include "JiYuTrainerHooks.h"

HINSTANCE hInst;

// 初始化必须放到独立线程：DllMain 运行在 loader lock 下，在里面 LoadBitmap / CreatePopupMenu /
// CreateThread / 装 40 个 inline hook，只要有一处等另一个持有 loader lock 的线程（User32 内部线程、
// 极域自己的初始化线程），就会互等，表现就是任务管理器里进程「已挂起」。
static DWORD WINAPI InitThreadProc(LPVOID)
{
	// 等宿主进程把 User32/GDI 初始化完再动手；此时 LoadLibrary 已返回，宿主不再被 loader lock 卡住。
	Sleep(200);
	VLoad();
	return 0;
}

BOOL APIENTRY DllMain( HMODULE hModule,
                       DWORD  ul_reason_for_call,
                       LPVOID lpReserved
                     )
{
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
		hInst = hModule;
		DisableThreadLibraryCalls(hModule);
		{
			HANDLE thread = CreateThread(nullptr, 0, InitThreadProc, nullptr, 0, nullptr);
			if (thread) CloseHandle(thread);
		}
		break;
    case DLL_THREAD_ATTACH:
    case DLL_THREAD_DETACH:
		break;
    case DLL_PROCESS_DETACH:
		VUnloadAll();
        break;
    }
    return TRUE;
}
