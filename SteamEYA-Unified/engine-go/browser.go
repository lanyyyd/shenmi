package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/chromedp/cdproto/network"
	"github.com/chromedp/cdproto/page"
	"github.com/chromedp/cdproto/runtime"
	cdptarget "github.com/chromedp/cdproto/target"
	"github.com/chromedp/chromedp"
)

const (
	kindCapture = "capture"
	kindLogin   = "login"
)

// session is one running Chromium instance plus a live CDP target connection.
// Login sessions outlive the engine (detached browser + persisted registry).
type session struct {
	game        string
	kind        string
	browserType string
	profile     string
	exe         string
	pid         int
	port        int
	wsURL       string

	ctx         context.Context
	cancel      context.CancelFunc
	allocCancel context.CancelFunc

	comboMu    sync.Mutex
	comboToken string
}

func (s *session) close() {
	if s == nil {
		return
	}
	if s.cancel != nil {
		s.cancel()
	}
	if s.allocCancel != nil {
		s.allocCancel()
	}
}

// alive reports whether the browser behind this session is still usable.
//
// Login sessions are user-facing browser windows that outlive the engine. A
// single CDP probe can time out (busy page, profile lock, launcher hand-off)
// while the window is perfectly fine, so either signal keeps it alive.
// Treating a transient probe failure as "gone" is what made the window flash
// and disappear: the caller closes the session, and closing a session cancels
// its CDP target, which closes the tab — the only tab, i.e. the whole window.
func (s *session) alive() bool {
	if s == nil || s.port <= 0 {
		return false
	}
	if s.kind == kindLogin {
		return processAlive(s.pid) || cdpAlive(s.port)
	}
	return cdpAlive(s.port)
}

func (s *session) run(timeout time.Duration, actions ...chromedp.Action) error {
	ctx, cancel := context.WithTimeout(s.ctx, timeout)
	defer cancel()
	return chromedp.Run(ctx, actions...)
}

func (s *session) navigate(target string) error {
	log.Printf("导航 [%s/%s] -> %s", s.game, s.kind, target)
	err := s.run(30*time.Second, chromedp.Navigate(target))
	if err != nil {
		log.Printf("导航失败 [%s/%s]: %v", s.game, s.kind, err)
		return err
	}
	s.bringToFront()
	return nil
}

// ensurePage navigates away from the startup blank tab. Chrome may keep the
// visible tab at about:blank while the controlled target is a background one,
// which made one-click login look like it did nothing.
func (s *session) ensurePage(target string) error {
	var loc string
	if err := s.run(5*time.Second, chromedp.Location(&loc)); err != nil {
		loc = ""
	}
	switch {
	case loc == "", loc == "about:blank", strings.HasPrefix(loc, "chrome://newtab"):
		return s.navigate(target)
	}
	s.bringToFront()
	return nil
}

func (s *session) bringToFront() {
	_ = s.run(5*time.Second, chromedp.ActionFunc(func(ctx context.Context) error {
		return page.BringToFront().Do(ctx)
	}))
}

func (s *session) reload() error {
	return s.run(30*time.Second, chromedp.Reload())
}

func (s *session) eval(js string, out any) error {
	return s.run(10*time.Second, chromedp.Evaluate(js, out))
}

func (s *session) evalAwait(js string, out any, timeout time.Duration) error {
	ctx, cancel := context.WithTimeout(s.ctx, timeout)
	defer cancel()
	return chromedp.Run(ctx, chromedp.Evaluate(js, out,
		func(p *runtime.EvaluateParams) *runtime.EvaluateParams {
			return p.WithAwaitPromise(true)
		}))
}

func (s *session) setCombo(v string) {
	if v == "" {
		return
	}
	// Selenium's performance log is scanned from its first entry, so keep the
	// earliest token seen instead of the most recent one.
	s.comboMu.Lock()
	if s.comboToken == "" {
		s.comboToken = v
	}
	s.comboMu.Unlock()
}

func (s *session) takeCombo() string {
	s.comboMu.Lock()
	defer s.comboMu.Unlock()
	return s.comboToken
}

type engine struct {
	mu       sync.Mutex
	dataDir  string
	capture  map[string]*session
	login    map[string]*session
	registry *browserRegistry
}

func newEngine(dataDir string) *engine {
	return &engine{
		dataDir:  dataDir,
		capture:  map[string]*session{},
		login:    map[string]*session{},
		registry: newBrowserRegistry(dataDir),
	}
}

// getCapture is _get_capture_driver: reuse the same browser for a game.
func (e *engine) getCapture(game, browserType string) (*session, bool, error) {
	if s := e.capture[game]; s != nil {
		if s.alive() {
			return s, true, nil
		}
		s.close()
		delete(e.capture, game)
	}
	s, err := launchSession(e.dataDir, game, kindCapture, browserType, false, navigateTarget(game, ""))
	if err != nil {
		return nil, false, err
	}
	e.capture[game] = s
	return s, false, nil
}

// getLogin is _get_login_driver, additionally reattaching to a browser that
// the previous engine process left running.
func (e *engine) getLogin(game, browserType string) (*session, bool, error) {
	if s := e.login[game]; s != nil {
		if s.alive() {
			return s, true, nil
		}
		// alive() 对登录会话是「进程或 CDP 任一存活」；走到这里说明两者都没了，
		// 关闭 session 不会误伤还开着的窗口。
		s.close()
		delete(e.login, game)
	}

	if entry, ok := e.registry.get(game); ok {
		if processAlive(entry.PID) && cdpAlive(entry.Port) {
			if s, err := attachSession(entry); err == nil {
				e.login[game] = s
				return s, true, nil
			}
		}
		e.registry.remove(game)
	}

	s, err := launchSession(e.dataDir, game, kindLogin, browserType, true, cfgFor(game, "").Home)
	if err != nil {
		return nil, false, err
	}
	e.login[game] = s
	e.registry.put(browserEntry{
		Game:      game,
		Browser:   browserType,
		Exe:       s.exe,
		Profile:   s.profile,
		PID:       s.pid,
		Port:      s.port,
		WSURL:     s.wsURL,
		StartedAt: time.Now().Unix(),
	})
	return s, false, nil
}

// aliveGames implements /engine/status: only login browsers count.
func (e *engine) aliveGames() map[string]bool {
	alive := map[string]bool{}
	for game, s := range e.login {
		if s.alive() {
			alive[game] = true
			continue
		}
		// 不要在这里 s.close()：/engine/status 是上号后每 45 秒轮询一次的接口，
		// 关闭 session 会 cancel CDP 并把接管的标签页关掉；如果那是窗口里唯一
		// 的标签，整个浏览器窗口就会闪一下消失。这里只丢掉引用，浏览器本身
		// 已经不可控了（进程与 CDP 都探测不到）。
		delete(e.login, game)
	}
	for game, entry := range e.registry.snapshot() {
		if alive[game] {
			continue
		}
		if processAlive(entry.PID) && cdpAlive(entry.Port) {
			alive[game] = true
		} else {
			e.registry.remove(game)
		}
	}
	return alive
}

func launchSession(dataDir, game, kind, browserType string, detach bool, initialURL string) (*session, error) {
	prefix := "capture_profile_"
	if kind == kindLogin {
		prefix = "login_profile_"
	}
	profile := filepath.Join(dataDir, prefix+game)
	if err := os.MkdirAll(profile, 0o755); err != nil {
		return nil, fmt.Errorf("创建浏览器 profile 失败：%w", err)
	}

	exe, err := findBrowserExec(browserType)
	if err != nil {
		return nil, err
	}

	// A stale DevToolsActivePort from the previous run would make us attach to
	// a port that no longer belongs to this browser.
	_ = os.Remove(filepath.Join(profile, "DevToolsActivePort"))

	cmd, done, err := startBrowser(exe, browserArgs(profile, initialURL), detach)
	if err != nil {
		return nil, fmt.Errorf("启动浏览器失败：%w", err)
	}
	log.Printf("已启动浏览器 [%s/%s] exe=%s profile=%s pid=%d url=%s",
		game, kind, exe, profile, cmd.Process.Pid, initialURL)

	port, wsPath, err := waitDevToolsPort(profile, done, 25*time.Second)
	if err != nil {
		return nil, err
	}
	wsURL := fmt.Sprintf("ws://127.0.0.1:%d%s", port, wsPath)
	if wsPath == "" {
		wsURL, err = browserWSURL(port)
		if err != nil {
			return nil, fmt.Errorf("读取浏览器调试地址失败：%w", err)
		}
	}

	s, err := attachCDP(game, kind, browserType, exe, profile, cmd.Process.Pid, port, wsURL)
	if err != nil {
		// DevToolsActivePort can appear slightly before the websocket endpoint
		// accepts connections; give the browser a couple of quick retries.
		for i := 0; i < 2; i++ {
			time.Sleep(300 * time.Millisecond)
			if s, err = attachCDP(game, kind, browserType, exe, profile, cmd.Process.Pid, port, wsURL); err == nil {
				break
			}
		}
	}
	return s, err
}

func attachSession(entry browserEntry) (*session, error) {
	wsURL := entry.WSURL
	if wsURL == "" {
		var err error
		wsURL, err = browserWSURL(entry.Port)
		if err != nil {
			return nil, err
		}
	}
	return attachCDP(entry.Game, kindLogin, entry.Browser, entry.Exe, entry.Profile,
		entry.PID, entry.Port, wsURL)
}

func attachCDP(game, kind, browserType, exe, profile string, pid, port int, wsURL string) (*session, error) {
	allocCtx, allocCancel := chromedp.NewRemoteAllocator(context.Background(), wsURL)

	preferHost := hostOf(navigateTarget(game, ""))
	targetID := pickPageTarget(port, preferHost)
	// The DevTools endpoint can answer /json/list slightly before the first
	// page shows up. Wait instead of letting chromedp create a hidden extra tab.
	for i := 0; targetID == "" && i < 10; i++ {
		time.Sleep(200 * time.Millisecond)
		targetID = pickPageTarget(port, preferHost)
	}
	var ctx context.Context
	var cancel context.CancelFunc
	if targetID != "" {
		log.Printf("CDP 接管已有标签页 [%s/%s] target=%s", game, kind, targetID)
		ctx, cancel = chromedp.NewContext(allocCtx, chromedp.WithTargetID(cdptarget.ID(targetID)))
	} else {
		log.Printf("CDP 未发现页面标签，创建新标签 [%s/%s]", game, kind)
		ctx, cancel = chromedp.NewContext(allocCtx)
	}

	// The first Run must use a context that lives as long as the session:
	// RemoteAllocator watches the context passed to its Allocate call (i.e.
	// this one) and, when it is done, cancels the session, closes the tab and
	// drops the DevTools websocket. Using a short-lived context here (and
	// cancelling it right after Run) silently killed every later CDP command,
	// which looked like "the page opened but cookies were never injected".
	// Bound the attach with a watchdog instead of a cancelable context.
	initCtx, initCancel := context.WithCancel(ctx)
	watchdog := time.AfterFunc(25*time.Second, initCancel)
	err := chromedp.Run(initCtx)
	if !watchdog.Stop() {
		initCancel()
	}
	if err != nil {
		cancel()
		allocCancel()
		return nil, fmt.Errorf("连接浏览器 CDP 失败：%w", err)
	}

	s := &session{
		game:        game,
		kind:        kind,
		browserType: browserType,
		profile:     profile,
		exe:         exe,
		pid:         pid,
		port:        port,
		wsURL:       wsURL,
		ctx:         ctx,
		cancel:      cancel,
		allocCancel: allocCancel,
	}
	s.setupTarget()
	s.bringToFront()
	return s, nil
}

func (s *session) setupTarget() {
	// Remove the webdriver flag, like the Python launch_browser does.
	_ = s.run(5*time.Second, chromedp.ActionFunc(func(ctx context.Context) error {
		_, err := page.AddScriptToEvaluateOnNewDocument(
			"Object.defineProperty(navigator, 'webdriver', {get: () => undefined});").Do(ctx)
		return err
	}))

	// Keep the latest x-rpc-combo_token seen on the wire; this replaces
	// Selenium's performance log used by capture_combo_token.
	chromedp.ListenTarget(s.ctx, func(ev any) {
		switch e := ev.(type) {
		case *network.EventRequestWillBeSentExtraInfo:
			s.setCombo(headerValue(e.Headers, "x-rpc-combo_token"))
		case *network.EventRequestWillBeSent:
			if e.Request != nil {
				s.setCombo(headerValue(e.Request.Headers, "x-rpc-combo_token"))
			}
		}
	})
	_ = s.run(5*time.Second, network.Enable())
}

func headerValue(headers network.Headers, name string) string {
	for k, v := range headers {
		if strings.EqualFold(k, name) {
			return pyStr(v)
		}
	}
	return ""
}

func browserArgs(profile, initialURL string) []string {
	if strings.TrimSpace(initialURL) == "" {
		initialURL = "about:blank"
	}
	return []string{
		"--user-data-dir=" + profile,
		"--remote-debugging-port=0",
		"--remote-allow-origins=*",
		"--no-first-run",
		"--no-default-browser-check",
		"--disable-popup-blocking",
		"--start-maximized",
		"--disable-extensions",
		initialURL,
	}
}

func startBrowser(exe string, args []string, detach bool) (*exec.Cmd, <-chan struct{}, error) {
	run := func(attr *syscall.SysProcAttr) (*exec.Cmd, <-chan struct{}, error) {
		cmd := exec.Command(exe, args...)
		cmd.SysProcAttr = attr
		cmd.Stdout = nil
		cmd.Stderr = nil
		if err := cmd.Start(); err != nil {
			return nil, nil, err
		}
		done := make(chan struct{})
		go func() {
			_ = cmd.Wait()
			close(done)
		}()
		return cmd, done, nil
	}

	cmd, done, err := run(sysProcAttr(detach))
	if err != nil && detach {
		// CREATE_BREAKAWAY_FROM_JOB is rejected when the parent job does not
		// allow breakaway. Retry detached, just without the breakaway flag.
		cmd, done, err = run(sysProcAttrRetry(detach))
	}
	return cmd, done, err
}

func waitDevToolsPort(profile string, done <-chan struct{}, timeout time.Duration) (int, string, error) {
	deadline := time.Now().Add(timeout)
	portFile := filepath.Join(profile, "DevToolsActivePort")
	for {
		if data, err := os.ReadFile(portFile); err == nil {
			lines := bytes.Split(bytes.TrimSpace(data), []byte("\n"))
			if len(lines) > 0 {
				if port, err := strconv.Atoi(strings.TrimSpace(string(lines[0]))); err == nil && port > 0 {
					wsPath := ""
					if len(lines) > 1 {
						wsPath = strings.TrimSpace(string(lines[1]))
					}
					return port, wsPath, nil
				}
			}
		}

		select {
		case <-done:
			return 0, "", errors.New("浏览器进程已退出（可能是该 profile 已被另一个窗口占用）")
		case <-time.After(100 * time.Millisecond):
		}

		if time.Now().After(deadline) {
			return 0, "", errors.New("浏览器启动超时（未找到 DevToolsActivePort）")
		}
	}
}

var localClient = &http.Client{Timeout: 1500 * time.Millisecond}

func cdpAlive(port int) bool {
	if port <= 0 {
		return false
	}
	resp, err := localClient.Get(fmt.Sprintf("http://127.0.0.1:%d/json/version", port))
	if err != nil {
		return false
	}
	defer resp.Body.Close()
	_, _ = io.Copy(io.Discard, resp.Body)
	return resp.StatusCode == http.StatusOK
}

func browserWSURL(port int) (string, error) {
	resp, err := localClient.Get(fmt.Sprintf("http://127.0.0.1:%d/json/version", port))
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()

	var info struct {
		WebSocketDebuggerURL string `json:"webSocketDebuggerUrl"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&info); err != nil {
		return "", err
	}
	if info.WebSocketDebuggerURL == "" {
		return "", errors.New("DevTools 未返回 webSocketDebuggerUrl")
	}
	return info.WebSocketDebuggerURL, nil
}

type cdpTargetInfo struct {
	ID   string `json:"id"`
	Type string `json:"type"`
	URL  string `json:"url"`
}

func listCDPTargets(port int) []cdpTargetInfo {
	resp, err := localClient.Get(fmt.Sprintf("http://127.0.0.1:%d/json/list", port))
	if err != nil {
		return nil
	}
	defer resp.Body.Close()

	var list []cdpTargetInfo
	if err := json.NewDecoder(resp.Body).Decode(&list); err != nil {
		return nil
	}
	return list
}

func pickPageTarget(port int, preferHost string) string {
	targets := listCDPTargets(port)
	var fallback string
	for _, t := range targets {
		if t.Type != "page" {
			continue
		}
		if fallback == "" {
			fallback = t.ID
		}
		if preferHost != "" && strings.Contains(strings.ToLower(t.URL), strings.ToLower(preferHost)) {
			return t.ID
		}
	}
	return fallback
}
