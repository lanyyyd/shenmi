package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
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
		// 浏览器可能由启动器进程派生，PID 先退出但 CDP 还活着；以调试端口为准。
		if cdpAlive(entry.Port) {
			if s, err := attachSession(entry); err == nil {
				e.login[game] = s
				return s, true, nil
			}
		}
		e.registry.remove(game)
	}

	// 注册表丢了但浏览器还开着（例如旧版本启动、或者注册表被清理）：
	// 扫描该游戏的 profile（含换新后的目录）直接接管，避免再开一个实例抢 profile。
	if s, err := attachAnyProfile(e.dataDir, game, browserType); err == nil {
		e.login[game] = s
		e.registry.put(browserEntry{
			Game:      game,
			Browser:   browserType,
			Exe:       s.exe,
			Profile:   s.profile,
			PID:       0,
			Port:      s.port,
			WSURL:     s.wsURL,
			StartedAt: time.Now().Unix(),
		})
		return s, true, nil
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
		if processAlive(entry.PID) || cdpAlive(entry.Port) {
			alive[game] = true
		} else {
			e.registry.remove(game)
		}
	}
	return alive
}

func launchSession(dataDir, game, kind, browserType string, detach bool, initialURL string) (*session, error) {
	// 登录浏览器每次都用全新的 profile：
	// 1) 避免和旧窗口发生 Edge 单实例交接（旧进程会接管命令但不带调试端口）；
	// 2) 每次上号都会重新注入 Cookie，不需要旧 profile 的登录态；
	// 3) 不会再出现「默认 profile 一个窗口 + 新 profile 一个窗口」。
	profile := ""
	if kind == kindLogin {
		profile = filepath.Join(dataDir,
			fmt.Sprintf("login_profile_%s_%d", game, time.Now().UnixNano()))
	} else {
		profile = filepath.Join(dataDir, "capture_profile_"+game)
		if port := readDevToolsPort(profile); port <= 0 || !cdpAlive(port) {
			if n := killBrowserProfileProcesses(profile); n > 0 {
				log.Printf("启动前清理 %d 个失效的浏览器进程 [%s/%s]", n, game, kind)
				time.Sleep(400 * time.Millisecond)
			}
		}
	}

	return launchWithProfile(profile, game, kind, browserType, detach, initialURL)
}

// launchWithProfile 用固定调试端口启动浏览器，并等待 CDP 真正可用。
// 使用显式端口而不是 --remote-debugging-port=0，避免 DevToolsActivePort 被
// 旧进程/单实例交接改写时读到过期端口。
func launchWithProfile(profile, game, kind, browserType string, detach bool, initialURL string) (*session, error) {
	if err := os.MkdirAll(profile, 0o755); err != nil {
		return nil, fmt.Errorf("创建浏览器 profile 失败：%w", err)
	}
	exe, err := findBrowserExec(browserType)
	if err != nil {
		return nil, err
	}
	port, err := freePort()
	if err != nil {
		return nil, fmt.Errorf("分配调试端口失败：%w", err)
	}
	_ = os.Remove(filepath.Join(profile, "DevToolsActivePort"))

	logPath := filepath.Join(profile, "browser.log")
	cmd, done, err := startBrowser(exe, browserArgs(profile, initialURL, port), detach, logPath)
	if err != nil {
		return nil, fmt.Errorf("启动浏览器失败：%w", err)
	}
	log.Printf("已启动浏览器 [%s/%s] exe=%s profile=%s pid=%d port=%d url=%s",
		game, kind, exe, profile, cmd.Process.Pid, port, initialURL)

	if err := waitCDP(port, done, 25*time.Second); err != nil {
		exitCode := -1
		if cmd.ProcessState != nil {
			exitCode = cmd.ProcessState.ExitCode()
		}
		killPIDTree(cmd.Process.Pid)
		return nil, fmt.Errorf("%w（exit=%d，日志：%s）", err, exitCode, logPath)
	}

	wsURL, err := browserWSURL(port)
	if err != nil {
		killPIDTree(cmd.Process.Pid)
		return nil, fmt.Errorf("读取浏览器调试地址失败：%w", err)
	}
	s, err := attachCDP(game, kind, browserType, exe, profile, cmd.Process.Pid, port, wsURL)
	if err != nil {
		for i := 0; i < 2; i++ {
			time.Sleep(300 * time.Millisecond)
			if s, err = attachCDP(game, kind, browserType, exe, profile, cmd.Process.Pid, port, wsURL); err == nil {
				break
			}
		}
	}
	if err != nil {
		// 端口开了但 CDP 一直连不上，这个窗口留着也没用，关掉避免残留。
		killPIDTree(cmd.Process.Pid)
		return nil, err
	}
	return s, nil
}

// freePort 先占一个空闲端口再交给浏览器，避免读端口文件带来的竞态。
func freePort() (int, error) {
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return 0, err
	}
	defer l.Close()
	return l.Addr().(*net.TCPAddr).Port, nil
}

// loginProfileDir 是登录浏览器默认使用的 profile 目录。
func loginProfileDir(dataDir, game string) string {
	return filepath.Join(dataDir, "login_profile_"+game)
}

// readDevToolsPort 读取 profile 里上一次运行留下的调试端口。
func readDevToolsPort(profile string) int {
	data, err := os.ReadFile(filepath.Join(profile, "DevToolsActivePort"))
	if err != nil {
		return 0
	}
	lines := strings.Split(strings.TrimSpace(string(data)), "\n")
	if len(lines) == 0 {
		return 0
	}
	port, err := strconv.Atoi(strings.TrimSpace(lines[0]))
	if err != nil || port <= 0 || port > 65535 {
		return 0
	}
	return port
}

// attachAnyProfile 没有注册表记录时，扫描该游戏的 profile（含换新后的
// login_profile_<game>_<时间戳>），接管还活着的那个浏览器。
func attachAnyProfile(dataDir, game, browserType string) (*session, error) {
	prefix := "login_profile_" + game
	entries, err := os.ReadDir(dataDir)
	if err != nil {
		return nil, err
	}
	type candidate struct {
		path string
		mod  time.Time
	}
	var candidates []candidate
	for _, de := range entries {
		if de.IsDir() && strings.HasPrefix(de.Name(), prefix) {
			info, err := de.Info()
			if err == nil {
				candidates = append(candidates, candidate{filepath.Join(dataDir, de.Name()), info.ModTime()})
			}
		}
	}
	sort.Slice(candidates, func(i, j int) bool { return candidates[i].mod.After(candidates[j].mod) })
	for _, c := range candidates {
		port := readDevToolsPort(c.path)
		if port <= 0 || !cdpAlive(port) {
			continue
		}
		wsURL, err := browserWSURL(port)
		if err != nil {
			continue
		}
		s, err := attachCDP(game, kindLogin, browserType, "", c.path, 0, port, wsURL)
		if err != nil {
			continue
		}
		log.Printf("已接管残留的登录浏览器 [%s] profile=%s port=%d", game, c.path, port)
		return s, nil
	}
	return nil, errors.New("没有可接管的调试端口")
}

// cleanupBrowsers 结束残留浏览器进程并清掉失效注册表；game 为空表示全部游戏。
func (e *engine) cleanupBrowsers(game string) int {
	e.mu.Lock()
	defer e.mu.Unlock()

	games := map[string]bool{}
	if game != "" {
		games[game] = true
	} else {
		for g := range e.login {
			games[g] = true
		}
		for g := range e.capture {
			games[g] = true
		}
		for g := range e.registry.snapshot() {
			games[g] = true
		}
		// 注册表丢了但 profile 目录还在的情况也要覆盖到。
		if entries, err := os.ReadDir(e.dataDir); err == nil {
			for _, de := range entries {
				name := de.Name()
				if strings.HasPrefix(name, "login_profile_") {
					games[strings.TrimPrefix(name, "login_profile_")] = true
				} else if strings.HasPrefix(name, "capture_profile_") {
					games[strings.TrimPrefix(name, "capture_profile_")] = true
				}
			}
		}
	}

	killed := 0
	for g := range games {
		loginLive := false
		for _, prefix := range []string{"login_profile_", "capture_profile_"} {
			profile := filepath.Join(e.dataDir, prefix+g)
			if _, err := os.Stat(profile); err != nil {
				continue
			}
			// 调试端口还活着说明浏览器可接管，绝对不能杀。
			if port := readDevToolsPort(profile); port > 0 && cdpAlive(port) {
				if prefix == "login_profile_" {
					loginLive = true
				}
				continue
			}
			killed += killBrowserProfileProcesses(profile)
			_ = os.Remove(filepath.Join(profile, "DevToolsActivePort"))
		}
		if s := e.capture[g]; s != nil {
			s.close()
			delete(e.capture, g)
		}
		if loginLive {
			continue
		}
		if s := e.login[g]; s != nil {
			s.close()
			delete(e.login, g)
		}
		e.registry.remove(g)
	}
	if killed > 0 {
		log.Printf("已清理 %d 个残留浏览器进程（game=%q）", killed, game)
	}
	return killed
}

// cleanupDeadOrphans 在引擎启动时做一次轻量清理：
// 只处理「CDP 已死、注册表也失效」的 profile，还活着的浏览器留给按需接管。
func (e *engine) cleanupDeadOrphans() {
	e.mu.Lock()
	defer e.mu.Unlock()

	for _, entry := range e.registry.snapshot() {
		if processAlive(entry.PID) && cdpAlive(entry.Port) {
			continue
		}
		e.registry.remove(entry.Game)
	}

	entries, err := os.ReadDir(e.dataDir)
	if err != nil {
		return
	}
	for _, de := range entries {
		if !de.IsDir() {
			continue
		}
		name := de.Name()
		if !strings.HasPrefix(name, "login_profile_") && !strings.HasPrefix(name, "capture_profile_") {
			continue
		}
		profile := filepath.Join(e.dataDir, name)
		if port := readDevToolsPort(profile); port > 0 && cdpAlive(port) {
			continue
		}
		_ = os.Remove(filepath.Join(profile, "DevToolsActivePort"))
		if n := killBrowserProfileProcesses(profile); n > 0 {
			log.Printf("启动清理残留浏览器 %d 个：%s", n, name)
		}
	}
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

func browserArgs(profile, initialURL string, port int) []string {
	if strings.TrimSpace(initialURL) == "" {
		initialURL = "about:blank"
	}
	return []string{
		"--user-data-dir=" + profile,
		"--remote-debugging-port=" + strconv.Itoa(port),
		"--remote-allow-origins=*",
		"--no-first-run",
		"--no-default-browser-check",
		"--disable-popup-blocking",
		"--start-maximized",
		"--disable-extensions",
		initialURL,
	}
}

func startBrowser(exe string, args []string, detach bool, logPath string) (*exec.Cmd, <-chan struct{}, error) {
	run := func(attr *syscall.SysProcAttr) (*exec.Cmd, <-chan struct{}, error) {
		cmd := exec.Command(exe, args...)
		cmd.SysProcAttr = attr
		var logFile *os.File
		if logPath != "" {
			// 防止日志无限增长；保留最近一次启动的输出用于排查。
			if info, err := os.Stat(logPath); err == nil && info.Size() > 2*1024*1024 {
				_ = os.Truncate(logPath, 0)
			}
			if f, err := os.OpenFile(logPath, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644); err == nil {
				logFile = f
				cmd.Stdout = f
				cmd.Stderr = f
			}
		}
		if err := cmd.Start(); err != nil {
			if logFile != nil {
				logFile.Close()
			}
			return nil, nil, err
		}
		done := make(chan struct{})
		go func() {
			_ = cmd.Wait()
			if logFile != nil {
				logFile.Close()
			}
			close(done)
		}()
		return cmd, done, nil
	}

	cmd, done, err := run(sysProcAttr(detach))
	if err != nil && detach {
		// CREATE_BREAKAWAY_FROM_JOB is rejected when the parent job does not
		// allow breakaway. Retry detached, just without the breakaway flag.
		log.Printf("浏览器 breakaway 启动失败：%v；退回 Job 内启动", err)
		cmd, done, err = run(sysProcAttrRetry(detach))
	}
	return cmd, done, err
}

// waitCDP 等待显式指定的调试端口可用；浏览器进程提前退出时立即返回。
func waitCDP(port int, done <-chan struct{}, timeout time.Duration) error {
	deadline := time.Now().Add(timeout)
	for {
		if cdpAlive(port) {
			return nil
		}
		select {
		case <-done:
			return errors.New("浏览器进程已退出（profile 可能仍被旧实例占用）")
		case <-time.After(100 * time.Millisecond):
		}
		if time.Now().After(deadline) {
			return errors.New("浏览器启动超时（调试端口未就绪）")
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
