package main

import (
	"context"
	"fmt"
	"log"
	"strings"
	"time"

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
	ctx, cancel := context.WithTimeout(s.ctx, 15*time.Second)
	defer cancel()

	raw, err := storage.GetCookies().Do(ctx)
	if err != nil {
		// Some Chromium builds only expose the cookie jar through the network
		// domain; fall back to the cookies visible to the current page.
		var location string
		if locErr := s.run(5*time.Second, chromedp.Location(&location)); locErr != nil || location == "" {
			return nil, err
		}
		raw, err = network.GetCookies().WithURLs([]string{location}).Do(ctx)
		if err != nil {
			return nil, err
		}
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

	if err := s.navigate(home); err != nil {
		return err
	}
	time.Sleep(1 * time.Second)

	if reset {
		resetBrowserState(s)
	}

	root := rootDomain(game, rawURL)
	injected, failed := 0, 0
	for _, cookie := range cookies {
		name := pyStr(cookie["name"])
		value := pyStr(cookie["value"])
		if name == "" || value == "" {
			continue
		}
		domain := cookieDomain(pyStr(cookie["domain"]), root)

		params := network.SetCookie(name, value).WithPath("/")
		if domain != "" {
			params = params.WithDomain(domain)
		} else {
			// CDP requires a URL when no domain is given (host-only cookie).
			var location string
			if locErr := s.run(5*time.Second, chromedp.Location(&location)); locErr == nil && location != "" {
				params = params.WithURL(location)
			}
		}
		if v, ok := cookie["secure"]; ok {
			params = params.WithSecure(pyTruthy(v))
		}
		if v, ok := cookie["httpOnly"]; ok {
			params = params.WithHTTPOnly(pyTruthy(v))
		}

		ctx, cancel := context.WithTimeout(s.ctx, 10*time.Second)
		err := params.Do(ctx)
		cancel()
		if err != nil {
			failed++
			log.Printf("跳过 Cookie %s（%s）: %v", name, domain, err)
		} else {
			injected++
		}
	}
	if failed > 0 {
		log.Printf("Cookie 注入完成：成功 %d 条，失败 %d 条", injected, failed)
	}
	return nil
}

// resetBrowserState is reset_browser_state: the persistent login profile must
// not keep the previous account's cookies / site storage.
func resetBrowserState(s *session) {
	ctx, cancel := context.WithTimeout(s.ctx, 10*time.Second)
	defer cancel()
	if err := storage.ClearCookies().Do(ctx); err != nil {
		_ = network.ClearBrowserCookies().Do(ctx)
	}
	_ = s.eval("try{localStorage.clear();sessionStorage.clear();}catch(e){}", nil)
}

func cookieCountMessage(game, rawURL, name string) string {
	return fmt.Sprintf("请先为「%s」打开浏览器", displayName(game, rawURL, name))
}
