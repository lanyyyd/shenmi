//go:build !windows

package main

import "os"

// browserCandidates on non-Windows is only used for local smoke testing; the
// engine itself targets the Windows client. PATH/CHROME_PATH still work.
func browserCandidates(browserType string) []string {
	if p := os.Getenv("EYA_BROWSER_PATH"); p != "" {
		return []string{p, os.Getenv("CHROME_PATH")}
	}
	switch browserType {
	case browserEdge:
		return []string{"/usr/bin/microsoft-edge", "/opt/microsoft/msedge/msedge"}
	case browserChrome:
		return []string{
			"/usr/bin/google-chrome",
			"/usr/bin/google-chrome-stable",
			"/usr/bin/chromium",
			"/usr/bin/chromium-browser",
			"/snap/bin/chromium",
		}
	}
	return nil
}
