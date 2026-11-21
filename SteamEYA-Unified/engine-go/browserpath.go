package main

import (
	"fmt"
	"os"
	"path/filepath"
)

const (
	browserEdge   = "edge"
	browserChrome = "chrome"
	browser360    = "360"
)

// findBrowserExec locates the Chromium based browser executable. No Selenium
// Manager, no webdriver: we drive the browser over CDP instead.
func findBrowserExec(browserType string) (string, error) {
	switch browserType {
	case browserEdge, browserChrome, browser360:
	default:
		return "", fmt.Errorf("不支持的浏览器类型: %s", browserType)
	}

	for _, p := range browserCandidates(browserType) {
		if p == "" {
			continue
		}
		if st, err := os.Stat(p); err == nil && !st.IsDir() {
			return p, nil
		}
	}
	if browserType == browser360 {
		return "", fmt.Errorf("未找到 360极速浏览器，请手动确认安装路径。\n" +
			"常见路径：C:\\Users\\你的用户名\\AppData\\Local\\360Chrome\\Chrome\\Application\\360chrome.exe")
	}
	name := "Edge"
	if browserType == browserChrome {
		name = "Chrome"
	}
	return "", fmt.Errorf("未找到 %s 浏览器，请先安装后再试", name)
}

func pathUnder(base string, parts ...string) string {
	if base == "" {
		return ""
	}
	return filepath.Join(append([]string{base}, parts...)...)
}
