package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"sync"
)

// browserEntry is persisted for detachable login browsers so that
// /engine/status can still answer after the engine process is restarted.
type browserEntry struct {
	Game      string `json:"game"`
	Browser   string `json:"browser"`
	Exe       string `json:"exe"`
	Profile   string `json:"profile"`
	PID       int    `json:"pid"`
	Port      int    `json:"port"`
	WSURL     string `json:"ws_url"`
	StartedAt int64  `json:"started_at"`
}

type browserRegistry struct {
	mu      sync.Mutex
	path    string
	entries map[string]browserEntry
}

func newBrowserRegistry(dataDir string) *browserRegistry {
	r := &browserRegistry{
		path:    filepath.Join(dataDir, "engine_go_browsers.json"),
		entries: map[string]browserEntry{},
	}
	r.load()
	return r
}

func (r *browserRegistry) load() {
	data, err := os.ReadFile(r.path)
	if err != nil {
		return
	}
	var list map[string]browserEntry
	if err := json.Unmarshal(data, &list); err != nil {
		return
	}
	for game, entry := range list {
		if entry.Port > 0 {
			r.entries[game] = entry
		}
	}
}

func (r *browserRegistry) get(game string) (browserEntry, bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	e, ok := r.entries[game]
	return e, ok
}

func (r *browserRegistry) put(entry browserEntry) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.entries[entry.Game] = entry
	r.saveLocked()
}

func (r *browserRegistry) remove(game string) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if _, ok := r.entries[game]; !ok {
		return
	}
	delete(r.entries, game)
	r.saveLocked()
}

func (r *browserRegistry) snapshot() map[string]browserEntry {
	r.mu.Lock()
	defer r.mu.Unlock()
	out := make(map[string]browserEntry, len(r.entries))
	for k, v := range r.entries {
		out[k] = v
	}
	return out
}

func (r *browserRegistry) saveLocked() {
	data, err := json.MarshalIndent(r.entries, "", "  ")
	if err != nil {
		return
	}
	tmp := r.path + ".tmp"
	if err := os.WriteFile(tmp, data, 0o644); err != nil {
		return
	}
	_ = os.Rename(tmp, r.path)
}
