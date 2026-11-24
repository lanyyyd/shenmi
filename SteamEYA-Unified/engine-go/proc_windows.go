//go:build windows

package main

import (
	"os/exec"
	"strconv"
	"strings"
	"syscall"

	"golang.org/x/sys/windows"
)

// browserPIDsForProfile 返回命令行里引用了指定 profile 的浏览器进程。
// 只匹配我们自己的 --user-data-dir，不会误杀用户日常浏览器。
func browserPIDsForProfile(profile string) []int {
	if strings.TrimSpace(profile) == "" {
		return nil
	}
	needle := strings.ReplaceAll(profile, "'", "''")
	script := "$p = Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and " +
		"$_.CommandLine.Contains('--user-data-dir') -and $_.CommandLine.Contains('" + needle + "') } | " +
		"Select-Object -ExpandProperty ProcessId; $p -join \"`n\""
	cmd := exec.Command("powershell", "-NoProfile", "-NonInteractive", "-Command", script)
	out, err := cmd.Output()
	if err != nil {
		return nil
	}
	var pids []int
	for _, line := range strings.Split(strings.TrimSpace(string(out)), "\n") {
		if pid, err := strconv.Atoi(strings.TrimSpace(line)); err == nil && pid > 0 {
			pids = append(pids, pid)
		}
	}
	return pids
}

// killBrowserProfileProcesses 结束占用指定 profile 的浏览器进程树，返回结束数量。
func killBrowserProfileProcesses(profile string) int {
	killed := 0
	for _, pid := range browserPIDsForProfile(profile) {
		if pid == syscall.Getpid() {
			continue
		}
		cmd := exec.Command("taskkill", "/PID", strconv.Itoa(pid), "/T", "/F")
		if err := cmd.Run(); err == nil {
			killed++
		}
	}
	return killed
}

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
