package main

import (
	"encoding/json"
	"net/http"
	"strings"
	"time"
)

type enginePayload struct {
	Game       string           `json:"game"`
	Browser    string           `json:"browser"`
	URL        string           `json:"url"`
	Name       string           `json:"name"`
	Cookies    []map[string]any `json:"cookies"`
	ComboToken string           `json:"combo_token"`
	DriverPath string           `json:"driver_path"`
}

func decodePayload(r *http.Request) enginePayload {
	var p enginePayload
	if r.Body != nil {
		_ = json.NewDecoder(r.Body).Decode(&p)
	}
	if p.Game == "" {
		p.Game = "genshin"
	}
	if p.Browser == "" {
		p.Browser = "edge"
	}
	p.URL = strings.TrimSpace(p.URL)
	p.Name = strings.TrimSpace(p.Name)
	if p.Cookies == nil {
		p.Cookies = []map[string]any{}
	}
	return p
}

func writeJSON(w http.ResponseWriter, v any) {
	w.Header().Set("Content-Type", "application/json")
	enc := json.NewEncoder(w)
	enc.SetEscapeHTML(false)
	_ = enc.Encode(v)
}

func withCORS(h http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Access-Control-Allow-Origin", "*")
		w.Header().Set("Access-Control-Allow-Methods", "*")
		w.Header().Set("Access-Control-Allow-Headers", "*")
		if r.Method == http.MethodOptions {
			w.WriteHeader(http.StatusOK)
			return
		}
		h.ServeHTTP(w, r)
	})
}

func (e *engine) handleHealth(w http.ResponseWriter, _ *http.Request) {
	items := make([]jsonObj, 0, len(gameOrder))
	for _, key := range gameOrder {
		cfg := games[key]
		items = append(items, jsonObj{
			"key":    key,
			"name":   cfg.Name,
			"url":    cfg.URL,
			"vendor": cfg.Vendor,
		})
	}
	writeJSON(w, jsonObj{"ok": true, "selenium_ready": true, "games": items})
}

func (e *engine) handleStatus(w http.ResponseWriter, _ *http.Request) {
	e.mu.Lock()
	alive := e.aliveGames()
	e.mu.Unlock()
	writeJSON(w, jsonObj{"ok": true, "alive": alive})
}

func (e *engine) handleBrowserOpen(w http.ResponseWriter, r *http.Request) {
	p := decodePayload(r)

	e.mu.Lock()
	defer e.mu.Unlock()

	s, reused, err := e.getCapture(p.Game, p.Browser)
	if err != nil {
		writeJSON(w, jsonObj{"ok": false, "msg": err.Error()})
		return
	}
	if !reused {
		if err := s.navigate(navigateTarget(p.Game, p.URL)); err != nil {
			writeJSON(w, jsonObj{"ok": false, "msg": err.Error()})
			return
		}
	}
	writeJSON(w, jsonObj{
		"ok":     true,
		"reused": reused,
		"name":   displayName(p.Game, p.URL, p.Name),
	})
}

func (e *engine) handleCapture(w http.ResponseWriter, r *http.Request) {
	p := decodePayload(r)

	e.mu.Lock()
	defer e.mu.Unlock()

	s := e.capture[p.Game]
	if s != nil && !s.alive() {
		s.close()
		delete(e.capture, p.Game)
		s = nil
	}
	if s == nil {
		writeJSON(w, jsonObj{"ok": false, "msg": cookieCountMessage(p.Game, p.URL, p.Name)})
		return
	}

	cookies, err := captureCookies(s, p.Game, p.URL)
	if err != nil {
		writeJSON(w, jsonObj{"ok": false, "msg": err.Error()})
		return
	}

	combo := ""
	if cfg, ok := games[p.Game]; ok && cfg.Vendor == "mihoyo" {
		combo = captureComboToken(s, 30)
	}
	writeJSON(w, jsonObj{
		"ok":          true,
		"cookies":     cookies,
		"combo_token": combo,
		"count":       len(cookies),
	})
}

func (e *engine) handleLogin(w http.ResponseWriter, r *http.Request) {
	p := decodePayload(r)
	if len(p.Cookies) == 0 {
		writeJSON(w, jsonObj{"ok": false, "msg": "缺少 Cookie"})
		return
	}

	e.mu.Lock()
	defer e.mu.Unlock()

	s, reused, err := e.getLogin(p.Game, p.Browser)
	if err != nil {
		writeJSON(w, jsonObj{"ok": false, "msg": err.Error()})
		return
	}

	// Clear the profile's previous account before injecting the new cookies.
	if err := injectCookies(s, p.Cookies, p.Game, p.URL, true); err != nil {
		writeJSON(w, jsonObj{"ok": false, "msg": err.Error()})
		return
	}
	if err := s.navigate(navigateTarget(p.Game, p.URL)); err != nil {
		writeJSON(w, jsonObj{"ok": false, "msg": err.Error()})
		return
	}
	time.Sleep(3 * time.Second)

	loggedIn := checkLoginStatus(s, 15, p.Game)
	writeJSON(w, jsonObj{"ok": true, "logged_in": loggedIn, "reused": reused})
}

func (e *engine) handleCheck(w http.ResponseWriter, r *http.Request) {
	p := decodePayload(r)
	result := checkCookieValid(p.Cookies, p.Game, p.ComboToken)
	result["ok"] = true
	writeJSON(w, result)
}

func (e *engine) handleRefresh(w http.ResponseWriter, r *http.Request) {
	p := decodePayload(r)
	// Python: {"ok": True, **refresh_cookies(...)} — the function's own ok
	// (possibly false) wins over the initial true.
	writeJSON(w, refreshCookies(p.Cookies, p.Game))
}

func registerRoutes(mux *http.ServeMux, e *engine) {
	mux.HandleFunc("/engine/health", e.handleHealth)
	mux.HandleFunc("/engine/status", e.handleStatus)
	mux.HandleFunc("/engine/browser/open", e.handleBrowserOpen)
	mux.HandleFunc("/engine/capture", e.handleCapture)
	mux.HandleFunc("/engine/login", e.handleLogin)
	mux.HandleFunc("/engine/check", e.handleCheck)
	mux.HandleFunc("/engine/refresh", e.handleRefresh)
}
