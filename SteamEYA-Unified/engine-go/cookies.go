package main

import (
	"context"
	"fmt"
	"log"
	"strings"
	"time"

	"github.com/chromedp/cdproto/cdp"
	"github.com/chromedp/cdproto/network"
	"github.com/chromedp/cdproto/storage"
	"github.com/chromedp/chromedp"
)

// outCookie mirrors the dict Selenium's get_cookies() returns.
type outCookie struct {
	Name     string `json:"name"`
	Value    string `json:"value"`
	Domain   string `json:"domain"`
	Path     string `json:"path"`
	Expiry   int64  `json:"expiry,omitempty"`
	Secure   bool   `json:"secure"`
	HTTPOnly bool   `json:"httpOnly"`
	SameSite string `json:"sameSite,omitempty"`
}

// captureCookies is capture_cookies: keep the domains belonging to the game.
func captureCookies(s *session, game, rawURL string) ([]outCookie, error) {
	var location string
	_ = s.run(5*time.Second, chromedp.Location(&location))

	var raw []*network.Cookie
	var err error
	// Protocol commands must run inside a chromedp action: only the context
	// handed to chromedp.Run carries the target executor, so calling Do() with
	// s.ctx (a plain context) fails with "invalid context".
	err = s.run(15*time.Second, chromedp.ActionFunc(func(ctx context.Context) error {
		raw, err = storage.GetCookies().Do(ctx)
		if err != nil {
			// Some Chromium builds only expose the cookie jar through the network
			// domain; fall back to the cookies visible to the current page.
			if location == "" {
				return err
			}
			raw, err = network.GetCookies().WithURLs([]string{location}).Do(ctx)
		}
		return err
	}))
	if err != nil {
		return nil, err
	}

	result := make([]outCookie, 0, len(raw))
	for _, c := range raw {
		result = append(result, outCookie{
			Name:     c.Name,
			Value:    c.Value,
			Domain:   c.Domain,
			Path:     c.Path,
			Expiry:   expiryOf(c),
			Secure:   c.Secure,
			HTTPOnly: c.HTTPOnly,
			SameSite: string(c.SameSite),
		})
	}

	cfg := cfgFor(game, rawURL)
	if len(cfg.CookieDomains) == 0 {
		return result, nil
	}
	filtered := make([]outCookie, 0, len(result))
	for _, c := range result {
		for _, kw := range cfg.CookieDomains {
			if strings.Contains(c.Domain, kw) {
				filtered = append(filtered, c)
				break
			}
		}
	}
	return filtered, nil
}

func expiryOf(c *network.Cookie) int64 {
	if c.Session || c.Expires <= 0 {
		return 0
	}
	return int64(c.Expires)
}

// setCookie runs Network.setCookie and reports the protocol "success" flag.
// cdproto's generated Do() drops that flag, so a cookie rejected by Chromium
// (invalid domain, expired, blocked...) would otherwise look like a success.
func setCookie(s *session, params *network.SetCookieParams, timeout time.Duration) (bool, error) {
	var success bool
	err := s.run(timeout, chromedp.ActionFunc(func(ctx context.Context) error {
		var res struct {
			Success bool `json:"success"`
		}
		if err := cdp.Execute(ctx, network.CommandSetCookie, params, &res); err != nil {
			return err
		}
		success = res.Success
		return nil
	}))
	return success, err
}

// withFlags copies the Selenium style secure/httpOnly flags like Python does
// (only when the key is present, never inferring them).
func withFlags(params *network.SetCookieParams, cookie map[string]any) *network.SetCookieParams {
	if v, ok := cookie["secure"]; ok {
		params = params.WithSecure(pyTruthy(v))
	}
	if v, ok := cookie["httpOnly"]; ok {
		params = params.WithHTTPOnly(pyTruthy(v))
	}
	return params
}

func setCookieParams(name, value, domain, url string, cookie map[string]any) *network.SetCookieParams {
	params := network.SetCookie(name, value).WithPath("/")
	switch {
	case domain != "":
		// No expiry on purpose: session cookie, like the Python service.
		params = params.WithDomain(domain)
	case url != "":
		// CDP requires a URL for a host-only cookie.
		params = params.WithURL(url)
	}
	return withFlags(params, cookie)
}

// injectCookies is inject_cookies. reset=true clears the profile's previous
// cookies/localStorage (the login flow), matching the Python service.
func injectCookies(s *session, cookies []map[string]any, game, rawURL string, reset bool) error {
	cfg := cfgFor(game, rawURL)
	home := cfg.Home
	if home == "" {
		home = rawURL
	}
	if home == "" {
		home = mihoyoHome
	}

	log.Printf("Cookie 注入：game=%s 收到 %d 条，home=%s，reset=%v", game, len(cookies), home, reset)

	if err := s.navigate(home); err != nil {
		return err
	}
	time.Sleep(1 * time.Second)

	if reset {
		resetBrowserState(s)
	}

	var location string
	_ = s.run(5*time.Second, chromedp.Location(&location))

	root := rootDomain(game, rawURL)
	names := make(map[string]struct{}, len(cookies))
	injected, fallback, failed, skipped := 0, 0, 0, 0

	for _, cookie := range cookies {
		name := pyStr(cookie["name"])
		value := pyStr(cookie["value"])
		if name == "" || value == "" {
			skipped++
			log.Printf("跳过 Cookie %q：缺少 name 或 value", name)
			continue
		}
		names[name] = struct{}{}
		domain := cookieDomain(pyStr(cookie["domain"]), root)

		ok, err := setCookie(s, setCookieParams(name, value, domain, location, cookie), 10*time.Second)
		if err == nil && ok {
			injected++
			log.Printf("Cookie %s（domain=%s secure=%v httpOnly=%v）注入成功",
				name, domain, pyTruthy(cookie["secure"]), pyTruthy(cookie["httpOnly"]))
			continue
		}

		reason := "浏览器拒绝（success=false）"
		if err != nil {
			reason = err.Error()
		}
		// Safe fallback: retry as a host-only cookie bound to the page URL we
		// are actually on, instead of the rewritten root domain.
		if domain != "" && location != "" {
			ok2, err2 := setCookie(s, setCookieParams(name, value, "", location, cookie), 10*time.Second)
			if err2 == nil && ok2 {
				fallback++
				log.Printf("Cookie %s（domain=%s）失败：%s；已回退为 host-only（%s）并成功",
					name, domain, reason, hostOf(location))
				continue
			}
			if err2 != nil {
				reason += "；回退也失败：" + err2.Error()
			} else {
				reason += "；回退被拒绝"
			}
		}
		failed++
		log.Printf("Cookie %s（domain=%s）注入失败：%s", name, domain, reason)
	}

	matched, total := countInjectedCookies(s, names)
	log.Printf("Cookie 注入完成：成功 %d 条（含回退 %d），失败 %d 条，跳过 %d 条；浏览器读回匹配 %d/%d 条",
		injected, fallback, failed, skipped, matched, total)
	if matched < total {
		log.Printf("警告：有 %d 条 Cookie 未能在浏览器中读回，登录态可能不完整", total-matched)
	}
	return nil
}

// countInjectedCookies reads the browser cookie jar back and counts how many
// of the injected names are actually present. The jar was cleared before
// injection, so a name match means the SetCookie really landed.
func countInjectedCookies(s *session, names map[string]struct{}) (int, int) {
	if len(names) == 0 {
		return 0, 0
	}
	var raw []*network.Cookie
	err := s.run(15*time.Second, chromedp.ActionFunc(func(ctx context.Context) error {
		var err error
		raw, err = storage.GetCookies().Do(ctx)
		return err
	}))
	if err != nil {
		log.Printf("读取浏览器 Cookie 失败：%v", err)
		return 0, len(names)
	}

	seen := map[string]bool{}
	for _, c := range raw {
		if _, want := names[c.Name]; want {
			seen[c.Name] = true
		}
	}
	return len(seen), len(names)
}

// resetBrowserState is reset_browser_state: the persistent login profile must
// not keep the previous account's cookies / site storage.
func resetBrowserState(s *session) {
	err := s.run(10*time.Second, chromedp.ActionFunc(func(ctx context.Context) error {
		return storage.ClearCookies().Do(ctx)
	}))
	if err != nil {
		err = s.run(10*time.Second, chromedp.ActionFunc(func(ctx context.Context) error {
			return network.ClearBrowserCookies().Do(ctx)
		}))
	}
	if err != nil {
		log.Printf("清空浏览器 Cookie 失败：%v", err)
	}
	_ = s.eval("try{localStorage.clear();sessionStorage.clear();}catch(e){}", nil)
}

func cookieCountMessage(game, rawURL, name string) string {
	return fmt.Sprintf("请先为「%s」打开浏览器", displayName(game, rawURL, name))
}
