//go:build windows

package main

import (
	"syscall"

	"golang.org/x/sys/windows"
)

// processAlive reports whether pid is still running (STILL_ACTIVE).
func processAlive(pid int) bool {
	if pid <= 0 {
		return false
	}
	h, err := windows.OpenProcess(windows.PROCESS_QUERY_LIMITED_INFORMATION, false, uint32(pid))
	if err != nil {
		return false
	}
	defer windows.CloseHandle(h)

	var code uint32
	if err := windows.GetExitCodeProcess(h, &code); err != nil {
		return false
	}
	return code == 259 // STILL_ACTIVE
}

// sysProcAttr detaches the login browser from the engine's console/job where
// possible, so that restarting or exiting the engine does not take the already
// opened browser down with it.
func sysProcAttr(detach bool) *syscall.SysProcAttr {
	flags := uint32(windows.CREATE_NEW_PROCESS_GROUP)
	if detach {
		flags |= windows.DETACHED_PROCESS | windows.CREATE_BREAKAWAY_FROM_JOB
	}
	return &syscall.SysProcAttr{CreationFlags: flags}
}

// sysProcAttrRetry keeps the detached semantics but without breakaway, used
// when the parent job forbids CREATE_BREAKAWAY_FROM_JOB.
func sysProcAttrRetry(detach bool) *syscall.SysProcAttr {
	flags := uint32(windows.CREATE_NEW_PROCESS_GROUP)
	if detach {
		flags |= windows.DETACHED_PROCESS
	}
	return &syscall.SysProcAttr{CreationFlags: flags}
}
