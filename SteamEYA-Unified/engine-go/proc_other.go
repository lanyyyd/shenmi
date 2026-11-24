//go:build !windows

package main

import "syscall"

func processAlive(pid int) bool {
	if pid <= 0 {
		return false
	}
	err := syscall.Kill(pid, 0)
	return err == nil || err == syscall.EPERM
}

func sysProcAttr(detach bool) *syscall.SysProcAttr {
	return &syscall.SysProcAttr{Setsid: detach}
}

func sysProcAttrRetry(detach bool) *syscall.SysProcAttr {
	return &syscall.SysProcAttr{Setsid: detach}
}

// 非 Windows 平台没有云游戏浏览器接管需求，清理操作直接返回 0。
func killBrowserProfileProcesses(_ string) int { return 0 }

func killPIDTree(_ int) {}
