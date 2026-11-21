package main

import (
	"net/url"
	"strings"
)

// gameConfig mirrors the GAMES table in browser_driver.py.
type gameConfig struct {
	Key           string
	Name          string
	URL           string
	Biz           string
	Vendor        string
	Home          string
	CookieDomains []string
}

const mihoyoHome = "https://ys.mihoyo.com/"

// gameOrder preserves the Python dict insertion order for /engine/health.
var gameOrder = []string{"genshin", "starrail", "yihuan", "bilibili"}

var games = map[string]*gameConfig{
	"genshin": {
		Key:           "genshin",
		Name:          "原神",
		URL:           "https://ys.mihoyo.com/cloud/#/",
		Biz:           "hk4e_cn",
		Vendor:        "mihoyo",
		Home:          "https://ys.mihoyo.com/",
		CookieDomains: []string{"mihoyo.com", "hoyoverse.com"},
	},
	"starrail": {
		Key:           "starrail",
		Name:          "星穹铁道",
		URL:           "https://sr.mihoyo.com/cloud/#/",
		Biz:           "hkrpg_cn",
		Vendor:        "mihoyo",
		Home:          "https://sr.mihoyo.com/",
		CookieDomains: []string{"mihoyo.com", "hoyoverse.com"},
	},
	"yihuan": {
		Key:           "yihuan",
		Name:          "异环",
		URL:           "https://yh.wanmei.com/cloud/index.html",
		Biz:           "yh",
		Vendor:        "wanmei",
		Home:          "https://id.wanmei.com/",
		CookieDomains: []string{"wanmei.com", "wmupd.com"},
	},
	"bilibili": {
		Key:           "bilibili",
		Name:          "哔哩哔哩",
		URL:           "https://www.bilibili.com/",
		Biz:           "",
		Vendor:        "bilibili",
		Home:          "https://www.bilibili.com/",
		CookieDomains: []string{"bilibili.com"},
	},
}

// cfgFor is _resolve_cfg: built-in table first, otherwise derive a config
// from the caller supplied URL (custom games).
func cfgFor(game, rawURL string) *gameConfig {
	if cfg, ok := games[game]; ok {
		return cfg
	}
	host := hostOf(rawURL)
	domains := []string{}
	if host != "" {
		domains = append(domains, host)
	}
	return &gameConfig{
		Key:           game,
		Name:          game,
		URL:           rawURL,
		Vendor:        "custom",
		Home:          rawURL,
		CookieDomains: domains,
	}
}

// displayName is display_name: explicit name → built-in table → URL host → key.
func displayName(game, rawURL, name string) string {
	if name != "" {
		return name
	}
	if cfg, ok := games[game]; ok {
		if cfg.Name != "" {
			return cfg.Name
		}
		return game
	}
	if host := hostOf(rawURL); host != "" {
		return host
	}
	return game
}

// hostOf is urllib.parse.urlparse(url).netloc.lower().
func hostOf(rawURL string) string {
	if rawURL == "" {
		return ""
	}
	u, err := url.Parse(rawURL)
	if err != nil {
		return ""
	}
	return strings.ToLower(u.Host)
}

// rootDomain is _root_domain.
func rootDomain(game, rawURL string) string {
	switch game {
	case "genshin", "starrail":
		return "mihoyo.com"
	case "yihuan":
		return "wanmei.com"
	case "bilibili":
		return "bilibili.com"
	}
	cfg := cfgFor(game, rawURL)
	host := ""
	for _, d := range cfg.CookieDomains {
		host = strings.ToLower(strings.Trim(strings.TrimSpace(d), "."))
		if host != "" {
			break
		}
	}
	if host == "" && rawURL != "" {
		host = hostOf(rawURL)
	}
	parts := []string{}
	for _, p := range strings.Split(host, ".") {
		if p != "" {
			parts = append(parts, p)
		}
	}
	if len(parts) >= 2 {
		return strings.Join(parts[len(parts)-2:], ".")
	}
	return host
}

// cookieDomain is _cookie_domain: hoyoverse credentials must not land on
// mihoyo.com (and vice versa).
func cookieDomain(domain, root string) string {
	d := strings.ToLower(strings.TrimLeft(domain, "."))
	if strings.Contains(d, "hoyoverse.com") {
		return ".hoyoverse.com"
	}
	if strings.Contains(d, "mihoyo.com") {
		return ".mihoyo.com"
	}
	if root != "" {
		return "." + root
	}
	return d
}

// navigateTarget is navigate_to_game's target selection.
func navigateTarget(game, rawURL string) string {
	cfg := cfgFor(game, rawURL)
	if rawURL != "" {
		return rawURL
	}
	if cfg.URL != "" {
		return cfg.URL
	}
	return games["genshin"].URL
}
