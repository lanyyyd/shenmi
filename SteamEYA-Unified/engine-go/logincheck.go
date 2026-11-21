package main

import (
	"fmt"
	"strconv"
	"strings"
	"time"
)

// checkLoginStatus is check_login_status (vendor branches included).
func checkLoginStatus(s *session, timeout int, game string) bool {
	cfg, ok := games[game]
	if !ok {
		cfg = games["genshin"]
	}
	if cfg.Vendor == "wanmei" {
		return checkWanmeiLoginStatus(s, timeout)
	}
	if cfg.Vendor == "bilibili" {
		return checkBilibiliLoginStatus(s, timeout)
	}

	if !waitForBody(s, timeout) {
		return false
	}
	time.Sleep(2 * time.Second)

	var pageText string
	if err := s.eval("document.body ? document.body.innerText : ''", &pageText); err != nil {
		return false
	}

	loginKeywords := []string{"扫码登录", "请登录", "登录后", "二维码登录", "短信登录", "密码登录"}
	hasLoginPrompt := false
	for _, kw := range loginKeywords {
		if strings.Contains(pageText, kw) {
			hasLoginPrompt = true
			break
		}
	}

	loggedInKeywords := []string{"开始游戏", "进入游戏", "我的钱包", "游戏时长", "退出登录", "uid"}
	hasLoggedIn := false
	for _, kw := range loggedInKeywords {
		if strings.Contains(pageText, kw) {
			hasLoggedIn = true
			break
		}
	}

	if hasLoginPrompt && !hasLoggedIn {
		return false
	}
	if hasLoggedIn {
		return true
	}

	var res jsonObj
	js := `fetch('/account/auth/info').then(r=>r.json()).catch(e=>({}))`
	if err := s.evalAwait(js, &res, time.Duration(timeout)*time.Second); err == nil {
		if numEq(res["retcode"], 0) {
			return true
		}
	}

	return !hasLoginPrompt
}

func waitForBody(s *session, timeout int) bool {
	deadline := time.Now().Add(time.Duration(timeout) * time.Second)
	for {
		var ok bool
		if err := s.eval("!!document.body", &ok); err == nil && ok {
			return true
		}
		if time.Now().After(deadline) {
			return false
		}
		time.Sleep(200 * time.Millisecond)
	}
}

// checkWanmeiLoginStatus is _check_wanmei_login: run the login API inside the
// page so the page's own cookies are used.
func checkWanmeiLoginStatus(s *session, timeout int) bool {
	js := fmt.Sprintf(
		`fetch(%s,{method:'POST',credentials:'include',headers:{'Content-Type':'application/x-www-form-urlencoded'},body:%s})`+
			`.then(function(r){return r.json();}).then(function(d){return d&&d.code;}).catch(function(e){return -1;})`,
		strconv.Quote(wanmeiLoginAPI), strconv.Quote(wanmeiBody()))

	var code any
	if err := s.evalAwait(js, &code, time.Duration(timeout)*time.Second); err != nil {
		return false
	}
	return numEq(code, 0)
}

// checkBilibiliLoginStatus is _check_bilibili_login.
func checkBilibiliLoginStatus(s *session, timeout int) bool {
	js := fmt.Sprintf(
		`fetch(%s,{credentials:'include'}).then(function(r){return r.json();})`+
			`.then(function(d){return !!(d&&d.code===0&&d.data&&d.data.isLogin);}).catch(function(e){return false;})`,
		strconv.Quote(biliNavAPI))

	var loggedIn bool
	if err := s.evalAwait(js, &loggedIn, time.Duration(timeout)*time.Second); err != nil {
		return false
	}
	return loggedIn
}
