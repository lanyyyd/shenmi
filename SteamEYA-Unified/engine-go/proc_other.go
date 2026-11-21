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
