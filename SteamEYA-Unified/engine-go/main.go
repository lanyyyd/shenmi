package main

import (
	"flag"
	"log"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"time"
)

func main() {
	host := flag.String("host", "127.0.0.1", "监听地址")
	port := flag.Int("port", 6971, "监听端口")
	flag.Parse()

	dataDir, err := resolveDataDir()
	if err != nil {
		log.Fatalf("数据目录不可用: %v", err)
	}

	eng := newEngine(dataDir)
	mux := http.NewServeMux()
	registerRoutes(mux, eng)

	addr := net.JoinHostPort(*host, strconv.Itoa(*port))
	srv := &http.Server{
		Addr:              addr,
		Handler:           withCORS(mux),
		ReadHeaderTimeout: 10 * time.Second,
	}
	log.Printf("SteamEYA 云游戏引擎（Go）监听 http://%s，数据目录 %s", addr, dataDir)
	if err := srv.ListenAndServe(); err != nil && err != http.ErrServerClosed {
		log.Fatalf("引擎服务启动失败: %v", err)
	}
}

// resolveDataDir mirrors local_service.py: EYA_ENGINE_DATA first, otherwise
// the directory of the executable.
func resolveDataDir() (string, error) {
	if env := os.Getenv("EYA_ENGINE_DATA"); env != "" {
		if err := os.MkdirAll(env, 0o755); err != nil {
			return "", err
		}
		return env, nil
	}
	exe, err := os.Executable()
	if err != nil {
		return "", err
	}
	return filepath.Dir(exe), nil
}
