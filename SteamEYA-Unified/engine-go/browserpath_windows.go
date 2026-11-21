//go:build windows

package main

import (
	"os"

	"golang.org/x/sys/windows/registry"
)

// browserCandidates mirrors the Windows lookup used by Selenium Manager /
// the Python 360 path list, plus the alternate 360 kernels (极速X / 安全浏览器).
func browserCandidates(browserType string) []string {
	programFiles := os.Getenv("ProgramFiles")
	programFilesX86 := os.Getenv("ProgramFiles(x86)")
	localAppData := os.Getenv("LocalAppData")
	programW6432 := os.Getenv("ProgramW6432")

	switch browserType {
	case browserEdge:
		candidates := []string{
			pathUnder(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
			pathUnder(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
			pathUnder(programW6432, "Microsoft", "Edge", "Application", "msedge.exe"),
			pathUnder(localAppData, "Microsoft", "Edge", "Application", "msedge.exe"),
			`C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe`,
			`C:\Program Files\Microsoft\Edge\Application\msedge.exe`,
		}
		return appendRegistryCandidates(candidates, "msedge.exe")

	case browserChrome:
		candidates := []string{
			pathUnder(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
			pathUnder(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
			pathUnder(programW6432, "Google", "Chrome", "Application", "chrome.exe"),
			pathUnder(localAppData, "Google", "Chrome", "Application", "chrome.exe"),
			`C:\Program Files\Google\Chrome\Application\chrome.exe`,
			`C:\Program Files (x86)\Google\Chrome\Application\chrome.exe`,
		}
		return appendRegistryCandidates(candidates, "chrome.exe")

	case browser360:
		candidates := []string{
			pathUnder(localAppData, "360Chrome", "Chrome", "Application", "360chrome.exe"),
			pathUnder(localAppData, "360ChromeX", "Chrome", "Application", "360chrome.exe"),
			pathUnder(programFilesX86, "360", "360Chrome", "Chrome", "Application", "360chrome.exe"),
			pathUnder(programFiles, "360", "360Chrome", "Chrome", "Application", "360chrome.exe"),
			pathUnder(programFilesX86, "360", "360ChromeX", "Chrome", "Application", "360chrome.exe"),
			pathUnder(programFiles, "360", "360ChromeX", "Chrome", "Application", "360chrome.exe"),
			pathUnder(localAppData, "360se6", "Application", "360se.exe"),
			`C:\Program Files (x86)\360\360Chrome\Chrome\Application\360chrome.exe`,
			`C:\Program Files\360\360Chrome\Chrome\Application\360chrome.exe`,
		}
		return appendRegistryCandidates(candidates, "360chrome.exe")
	}
	return nil
}

func appendRegistryCandidates(candidates []string, exeName string) []string {
	if p := appPathFromRegistry(exeName); p != "" {
		candidates = append(candidates, p)
	}
	return candidates
}

// appPathFromRegistry reads the App Paths default value (HKCU/HKLM/WOW6432).
func appPathFromRegistry(exeName string) string {
	subKeys := []string{
		`SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\` + exeName,
		`SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\` + exeName,
	}
	for _, hive := range []registry.Key{registry.CURRENT_USER, registry.LOCAL_MACHINE} {
		for _, sub := range subKeys {
			k, err := registry.OpenKey(hive, sub, registry.QUERY_VALUE)
			if err != nil {
				continue
			}
			value, _, err := k.GetStringValue("")
			_ = k.Close()
			if err == nil && value != "" {
				if st, statErr := os.Stat(value); statErr == nil && !st.IsDir() {
					return value
				}
			}
		}
	}
	return ""
}
