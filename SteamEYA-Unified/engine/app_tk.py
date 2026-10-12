"""
云游戏上号器 - 主程序 (tkinter GUI)  支持 云·原神 / 云·星穹铁道

功能：
1. 抓取 Cookie：打开浏览器 → 用户手动登录 → 自动抓取 mihoyo Cookie + combo_token → 加密保存 / 上传服务器
2. 一键上号：读取加密文件或从服务器获取 → 注入 Cookie → 跳转对应云游戏 → 自动登录
3. 服务器：账号登录后可在多台机器间同步 Cookie，无需 U 盘
"""
import logging
import os
import queue
import sys
import threading
import time
import tkinter as tk
from tkinter import filedialog, messagebox, ttk

import requests

import cookie_crypto
import browser_driver

# ── 日志配置 ──────────────────────────────────────────────
logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger("cloud_genshin")

APP_TITLE = "云游戏上号器（原神 / 星穹铁道 / 异环 / 哔哩哔哩）"


def _app_base_dir() -> str:
    """数据目录（浏览器 profile / cookie 文件 / 证书等）必须放在 exe 同级的真实目录。

    注意：PyInstaller 单文件模式（-F）下，__file__ 指向运行时临时解包目录
    （%TEMP%\\_MEIxxxxxx），该目录在程序退出时会被 PyInstaller 强制删除。
    若把浏览器 profile 放在里面，上号后浏览器仍开着（detach）会占用该目录，
    导致退出时清理失败并弹出 "Failed to remove temporary directory" 警告，
    同时 cookie 文件也会随临时目录一起被删掉。故这里改为取 exe 所在目录。
    """
    if getattr(sys, "frozen", False):
        return os.path.dirname(os.path.abspath(sys.executable))
    return os.path.dirname(os.path.abspath(__file__))


_BASE_DIR = _app_base_dir()
DEFAULT_SERVER_URL = "http://47.104.210.199:6966"
DEFAULT_COOKIE_FILE = os.path.join(_BASE_DIR, "cookies_genshin.enc")


# 持久化 profile 目录：Cookie 会写到磁盘，关掉浏览器再开还在
# 关键：按游戏分目录。同一个 profile 目录同时只能被一个浏览器进程占用，
# 共用一个目录会导致「上号原神后不能再上号崩铁」，分开后各游戏互不干扰、可同时开。
def capture_profile_dir(game: str) -> str:
    return os.path.join(_BASE_DIR, f"capture_profile_{game}")


def login_profile_dir(game: str) -> str:
    return os.path.join(_BASE_DIR, f"login_profile_{game}")


class CloudGenshinApp:
    def __init__(self, root: tk.Tk):
        self.root = root
        self.root.title(APP_TITLE)
        self.root.geometry("900x720")
        self.root.resizable(False, False)

        # 共享状态（按游戏分别保存浏览器，互不覆盖，可同时上号多个）
        self.capture_drivers = {}
        self.login_drivers = {}
        self.captured_cookies = []
        self.captured_combo_token = ""
        self.log_queue = queue.Queue()

        # 服务器状态
        self.server_token = ""
        self.server_username = ""

        # 从服务器获取的 Cookie（内存缓存，避免再存本地文件）
        self.remote_cookies = None
        self.remote_combo = ""

        self._build_ui()
        self._poll_log()

    # ── UI 构建 ───────────────────────────────────────────
    def _build_ui(self):
        # 顶部说明
        header = tk.Label(
            self.root,
            text="云游戏上号器  |  在家抓一次 Cookie，机房直接上号",
            font=("Microsoft YaHei", 16, "bold"),
            fg="#2c5282",
            pady=8,
        )
        header.pack()

        # 游戏 + 浏览器选择
        sel_frame = tk.Frame(self.root)
        sel_frame.pack(pady=4)

        tk.Label(sel_frame, text="游戏：", font=("Microsoft YaHei", 12)).pack(side="left")
        self.var_game = tk.StringVar(value="genshin")
        game_combo = ttk.Combobox(
            sel_frame,
            textvariable=self.var_game,
            values=["genshin", "starrail", "yihuan", "bilibili"],
            state="readonly",
            width=10,
            font=("Microsoft YaHei", 12),
        )
        game_combo.pack(side="left", padx=(2, 16))
        game_combo.bind("<<ComboboxSelected>>", lambda e: self._on_game_change())
        # 显示中文名
        self.var_game_name = tk.StringVar(value="原神")
        tk.Label(sel_frame, textvariable=self.var_game_name, fg="#2c5282",
                 font=("Microsoft YaHei", 12, "bold")).pack(side="left", padx=(0, 16))

        tk.Label(sel_frame, text="浏览器：", font=("Microsoft YaHei", 12)).pack(side="left")
        self.var_browser = tk.StringVar(value="edge")
        browser_combo = ttk.Combobox(
            sel_frame,
            textvariable=self.var_browser,
            values=["edge", "chrome", "360"],
            state="readonly",
            width=10,
            font=("Microsoft YaHei", 12),
        )
        browser_combo.pack(side="left", padx=6)
        tk.Label(
            sel_frame,
            text="（Edge 推荐；Chrome 需已安装；360极速浏览器自动检测路径）",
            fg="#888",
            font=("Microsoft YaHei", 10),
        ).pack(side="left")

        tk.Label(
            self.root,
            text="各游戏切换后请重新抓取 / 下载对应游戏的 Cookie；不同游戏的 Cookie 分开存放，可同时上号多个游戏",
            fg="#888", font=("Microsoft YaHei", 10),
        ).pack()

        # Notebook
        self.nb = ttk.Notebook(self.root)
        self.nb.pack(fill="both", expand=True, padx=10, pady=5)

        self._build_capture_tab()
        self._build_login_tab()
        self._build_server_tab()

        # 日志区
        log_frame = tk.LabelFrame(self.root, text="运行日志", font=("Microsoft YaHei", 11))
        log_frame.pack(fill="both", expand=True, padx=10, pady=5)

        self.log_text = tk.Text(log_frame, height=10, font=("Consolas", 11), state="disabled")
        scrollbar = tk.Scrollbar(log_frame, command=self.log_text.yview)
        self.log_text.config(yscrollcommand=scrollbar.set)
        scrollbar.pack(side="right", fill="y")
        self.log_text.pack(fill="both", expand=True, padx=5, pady=5)

    def _on_game_change(self):
        """游戏切换时更新显示名与默认文件名"""
        game = self.var_game.get()
        info = browser_driver.GAMES.get(game, browser_driver.GAMES["genshin"])
        self.var_game_name.set(info["name"])
        # 更新默认 cookie 文件名（区分游戏）
        self.var_file_path.set(os.path.join(_BASE_DIR, f"cookies_{game}.enc"))
        # 清除上一个游戏的服务器缓存，避免混用
        self.remote_cookies = None
        self.remote_combo = ""
        self.log(f"已切换到：{info['name']}（{info['url']}）")

    def _build_capture_tab(self):
        tab = ttk.Frame(self.nb)
        self.nb.add(tab, text=" ① 抓取 Cookie ")

        # 步骤说明
        steps = (
            "使用步骤：\n"
            "1. 在顶部选择游戏（原神 / 星穹铁道 / 异环 / 哔哩哔哩）和浏览器\n"
            "2. 点击「启动浏览器并打开游戏」\n"
            "3. 在弹出的窗口里手动扫码/密码登录\n"
            "4. 登录成功后回到本窗口，点击「抓取 Cookie」\n"
            "5. 设置加密密码，点击「保存（本地）」或「上传到服务器」"
        )
        tk.Label(tab, text=steps, justify="left", font=("Microsoft YaHei", 13), fg="#444").pack(
            anchor="w", padx=10, pady=8
        )

        # 按钮区
        btn_frame = tk.Frame(tab)
        btn_frame.pack(fill="x", padx=10, pady=5)

        self.btn_open = tk.Button(
            btn_frame, text="启动浏览器并打开游戏", width=22, command=self._on_open_browser
        )
        self.btn_open.pack(side="left", padx=4)

        self.btn_capture = tk.Button(
            btn_frame, text="抓取 Cookie", width=14, state="disabled", command=self._on_capture
        )
        self.btn_capture.pack(side="left", padx=4)

        self.btn_save = tk.Button(
            btn_frame, text="保存（本地）", width=14, state="disabled", command=self._on_save
        )
        self.btn_save.pack(side="left", padx=4)

        self.btn_upload = tk.Button(
            btn_frame, text="上传到服务器", width=14, command=self._upload_to_server
        )
        self.btn_upload.pack(side="left", padx=4)

        # 密码输入
        pwd_frame = tk.Frame(tab)
        pwd_frame.pack(fill="x", padx=10, pady=8)
        tk.Label(pwd_frame, text="加密密码：", font=("Microsoft YaHei", 13)).pack(side="left")
        self.var_capture_pwd = tk.StringVar()
        tk.Entry(pwd_frame, textvariable=self.var_capture_pwd, show="*", width=30).pack(side="left", padx=4)
        tk.Label(pwd_frame, text="（至少 6 位）", fg="#888", font=("Microsoft YaHei", 11)).pack(side="left")

        # combo_token 显示
        token_frame = tk.Frame(tab)
        token_frame.pack(fill="x", padx=10, pady=8)
        tk.Label(token_frame, text="抓取到的 combo_token：", font=("Microsoft YaHei", 13)).pack(anchor="w")
        self.var_combo_token = tk.StringVar(value="（抓取后显示，可用于米游签）")
        tk.Entry(token_frame, textvariable=self.var_combo_token, width=80).pack(
            fill="x", pady=2
        )
        tk.Label(
            token_frame,
            text="（自动抓取失败时，可在 Edge F12→Network→wallet/wallet/get 请求头里复制 X-Rpc-Combo_token 粘贴到此处）",
            fg="#888",
            font=("Microsoft YaHei", 10),
        ).pack(anchor="w")

        # Cookie 数量
        self.var_cookie_count = tk.StringVar(value="尚未抓取")
        tk.Label(tab, textvariable=self.var_cookie_count, fg="#2c5282", font=("Microsoft YaHei", 13)).pack(
            anchor="w", padx=10
        )

    def _build_login_tab(self):
        tab = ttk.Frame(self.nb)
        self.nb.add(tab, text=" ② 一键上号 ")

        tk.Label(
            tab,
            text="使用步骤：\n1. 选择之前保存的加密 Cookie 文件（或点「从服务器获取」）\n2. 输入加密密码\n3. 点击「一键上号」，浏览器会自动打开并登录所选游戏",
            justify="left",
            font=("Microsoft YaHei", 13),
            fg="#444",
        ).pack(anchor="w", padx=10, pady=8)

        # 文件选择
        file_frame = tk.Frame(tab)
        file_frame.pack(fill="x", padx=10, pady=5)
        tk.Label(file_frame, text="Cookie 文件：", font=("Microsoft YaHei", 13)).pack(side="left")
        self.var_file_path = tk.StringVar(value=DEFAULT_COOKIE_FILE)
        tk.Entry(file_frame, textvariable=self.var_file_path, width=42).pack(side="left", padx=4)
        tk.Button(file_frame, text="浏览...", command=self._on_browse).pack(side="left")
        tk.Button(file_frame, text="从服务器获取", command=self._on_fetch_from_server).pack(side="left", padx=4)

        # 密码
        pwd_frame = tk.Frame(tab)
        pwd_frame.pack(fill="x", padx=10, pady=8)
        tk.Label(pwd_frame, text="加密密码：", font=("Microsoft YaHei", 13)).pack(side="left")
        self.var_login_pwd = tk.StringVar()
        tk.Entry(pwd_frame, textvariable=self.var_login_pwd, show="*", width=26).pack(side="left", padx=4)
        tk.Button(pwd_frame, text="检测 Cookie 有效性", command=self._on_check_valid).pack(side="left", padx=8)
        tk.Button(pwd_frame, text="Cookie 自动续期", command=self._on_refresh).pack(side="left", padx=4)

        # 上号按钮
        self.btn_login = tk.Button(
            tab, text="一键上号", font=("Microsoft YaHei", 15, "bold"), width=20, command=self._on_login
        )
        self.btn_login.pack(pady=15)

        # 状态
        self.var_status = tk.StringVar(value="就绪")
        tk.Label(tab, textvariable=self.var_status, font=("Microsoft YaHei", 13), fg="#2c5282").pack()

    def _build_server_tab(self):
        tab = ttk.Frame(self.nb)
        self.nb.add(tab, text=" ③ 服务器 ")

        tk.Label(
            tab,
            text="服务器地址已内置（47.104.210.199:6966），直接填写用户名密码注册/登录即可。\n登录后可把 Cookie 上传到服务器，机房直接从服务器下载上号，无需 U 盘传文件。",
            justify="left", font=("Microsoft YaHei", 11), fg="#444",
        ).pack(anchor="w", padx=10, pady=8)
        tk.Label(
            tab,
            text="管理员后台：浏览器打开  服务器地址 + /admin  （默认账号 admin / admin123，请及时修改）",
            justify="left", font=("Microsoft YaHei", 10), fg="#888",
        ).pack(anchor="w", padx=10)

        # 服务器地址
        row1 = tk.Frame(tab)
        row1.pack(fill="x", padx=10, pady=4)
        tk.Label(row1, text="服务器地址：", font=("Microsoft YaHei", 12), width=12).pack(side="left")
        self.var_server_url = tk.StringVar(value=DEFAULT_SERVER_URL)
        tk.Entry(row1, textvariable=self.var_server_url, width=40, font=("Microsoft YaHei", 11)).pack(side="left")

        # 用户名
        row2 = tk.Frame(tab)
        row2.pack(fill="x", padx=10, pady=4)
        tk.Label(row2, text="用户名：", font=("Microsoft YaHei", 12), width=12).pack(side="left")
        self.var_server_user = tk.StringVar()
        tk.Entry(row2, textvariable=self.var_server_user, width=20, font=("Microsoft YaHei", 11)).pack(side="left")

        # 密码
        row3 = tk.Frame(tab)
        row3.pack(fill="x", padx=10, pady=4)
        tk.Label(row3, text="密码：", font=("Microsoft YaHei", 12), width=12).pack(side="left")
        self.var_server_pwd = tk.StringVar()
        tk.Entry(row3, textvariable=self.var_server_pwd, show="*", width=20, font=("Microsoft YaHei", 11)).pack(side="left")

        # 按钮
        btn_row = tk.Frame(tab)
        btn_row.pack(pady=10)
        tk.Button(btn_row, text="注册", width=10, font=("Microsoft YaHei", 11), command=self._on_register).pack(side="left", padx=5)
        tk.Button(btn_row, text="登录", width=10, font=("Microsoft YaHei", 11), command=self._on_server_login).pack(side="left", padx=5)

        # 登录状态
        self.var_server_status = tk.StringVar(value="未登录")
        tk.Label(tab, textvariable=self.var_server_status, font=("Microsoft YaHei", 12), fg="#e53e3e").pack()

    # ── 服务器接口 ─────────────────────────────────────────
    def _server_request(self, method: str, path: str, json_data=None, need_auth=True) -> dict:
        """统一的服务器请求"""
        url = self.var_server_url.get().rstrip("/") + path
        headers = {}
        if need_auth and self.server_token:
            headers["Authorization"] = f"Bearer {self.server_token}"
        try:
            resp = requests.request(method, url, json=json_data, headers=headers, timeout=15)
            return resp.json()
        except requests.exceptions.ConnectionError:
            return {"ok": False, "msg": "无法连接服务器，请检查地址"}
        except Exception as e:
            return {"ok": False, "msg": f"请求失败：{e}"}

    def _on_register(self):
        username = self.var_server_user.get().strip()
        password = self.var_server_pwd.get()
        if not username or not password:
            messagebox.showwarning("提示", "请输入用户名和密码")
            return
        result = self._server_request("POST", "/api/register", {"username": username, "password": password}, need_auth=False)
        if result.get("ok"):
            messagebox.showinfo("成功", "注册成功，请点击登录")
        else:
            messagebox.showerror("失败", result.get("msg", "注册失败"))

    def _on_server_login(self):
        username = self.var_server_user.get().strip()
        password = self.var_server_pwd.get()
        if not username or not password:
            messagebox.showwarning("提示", "请输入用户名和密码")
            return

        def task():
            self.log("正在登录服务器...")
            result = self._server_request("POST", "/api/login", {"username": username, "password": password}, need_auth=False)
            if result.get("ok"):
                self.server_token = result["token"]
                self.server_username = username
                self.var_server_status.set(f"已登录：{username}")
                self.log(f"服务器登录成功：{username}")
            else:
                self.var_server_status.set("登录失败")
                self.log(f"服务器登录失败：{result.get('msg')}")
                messagebox.showerror("失败", result.get("msg", "登录失败"))

        threading.Thread(target=task, daemon=True).start()

    def _upload_to_server(self):
        if not self.server_token:
            messagebox.showwarning("提示", "请先在「服务器」标签页登录")
            return
        if not self.captured_cookies:
            messagebox.showwarning("提示", "请先抓取 Cookie")
            return

        game = self.var_game.get()

        def task():
            try:
                self.log("正在上传 Cookie 到服务器...")
                combo_token = self.var_combo_token.get()
                if combo_token.startswith("（") or not combo_token:
                    combo_token = self.captured_combo_token

                result = self._server_request("POST", "/api/upload", {
                    "game": game,
                    "cookies": self.captured_cookies,
                    "combo_token": combo_token,
                })
                if result.get("ok"):
                    detail = result.get("detail", "")
                    name = result.get("nickname", "")
                    msg = f"Cookie 已上传到服务器（{browser_driver.GAMES[game]['name']}）"
                    if name:
                        msg += f"\n检测到角色：{name}"
                    msg += f"\n服务器检测结果：{detail}"
                    self.log("✓ " + msg.replace("\n", " | "))
                    messagebox.showinfo("成功", msg)
                else:
                    self.log(f"上传失败：{result.get('msg')}")
                    messagebox.showerror("失败", result.get("msg", "上传失败"))
            except Exception as e:
                self.log(f"上传失败：{e}")
                messagebox.showerror("错误", f"上传失败：\n{e}")

        threading.Thread(target=task, daemon=True).start()

    def _download_from_server(self) -> dict | None:
        """从服务器下载 Cookie（明文），返回 {"cookies": [...], "combo_token": ""} 或 None"""
        if not self.server_token:
            messagebox.showwarning("提示", "请先在「服务器」标签页登录")
            return None
        game = self.var_game.get()
        result = self._server_request("GET", f"/api/download?game={game}")
        if not result.get("ok"):
            messagebox.showerror("失败", result.get("msg", "下载失败"))
            return None
        return {"cookies": result.get("cookies", []), "combo_token": result.get("combo_token", "")}

    def _on_check_valid(self):
        """检测当前游戏的 Cookie 是否有效（优先本地文件，其次服务器）"""
        game = self.var_game.get()
        pwd = self.var_login_pwd.get()
        file_path = self.var_file_path.get()

        def task():
            try:
                cookies = None
                combo_token = ""
                # 1. 本地文件
                if file_path and os.path.exists(file_path) and pwd:
                    self.log(f"正在读取并解密本地文件：{file_path}")
                    data = cookie_crypto.load_encrypted(file_path, pwd)
                    cookies = data["cookies"]
                    combo_token = data.get("combo_token", "")
                # 2. 服务器
                elif self.server_token:
                    self.log("正在从服务器获取 Cookie 用于检测...")
                    data = self._download_from_server()
                    if data:
                        cookies = data["cookies"]
                        combo_token = data.get("combo_token", "")
                if not cookies:
                    messagebox.showwarning("提示", "请先选择可解密的本地文件，或登录服务器后获取")
                    return

                self.log(f"正在检测 {browser_driver.GAMES[game]['name']} Cookie 有效性...")
                result = browser_driver.check_cookie_valid(cookies, game, combo_token)
                if result.get("valid") is True:
                    info = result.get("nickname", "")
                    uid = result.get("game_uid", "")
                    lv = result.get("level", "")
                    msg = f"Cookie 有效 ✓\n角色：{info}  UID：{uid}  等级：{lv}"
                    if result.get("free_duration") is not None:
                        msg += f"\n剩余免费时长：{result['free_duration']} 分钟"
                    self.log(msg.replace("\n", " | "))
                    messagebox.showinfo("检测结果", msg)
                elif result.get("valid") is False:
                    msg = f"Cookie 已失效：{result.get('msg')}\n请重新抓取并上传"
                    self.log(msg.replace("\n", " "))
                    messagebox.showerror("检测结果", msg)
                else:
                    self.log(f"无法确定：{result.get('msg')}")
                    messagebox.showwarning("检测结果", f"无法确定：{result.get('msg')}")
            except ValueError as e:
                messagebox.showerror("错误", f"密码错误或文件损坏：\n{e}")
            except Exception as e:
                self.log(f"检测失败：{e}")
                messagebox.showerror("错误", f"检测失败：\n{e}")

        threading.Thread(target=task, daemon=True).start()

    def _on_refresh(self):
        """用 login_ticket 自动续期 Cookie，减少重新抓取频率"""
        game = self.var_game.get()
        file_path = self.var_file_path.get()
        pwd = self.var_login_pwd.get()

        def task():
            try:
                # 1. 取得当前 Cookie（优先内存缓存，其次本地文件）
                if self.remote_cookies is not None:
                    cookies = list(self.remote_cookies)
                    source = "服务器"
                elif file_path and os.path.exists(file_path) and pwd:
                    self.log(f"正在读取并解密本地文件：{file_path}")
                    cookies = cookie_crypto.load_encrypted(file_path, pwd)["cookies"]
                    source = "本地文件"
                else:
                    messagebox.showwarning("提示", "请先选择可解密的本地文件，或登录服务器后获取")
                    return

                gname = browser_driver.GAMES[game]["name"]
                self.log(f"正在为 {gname} 的 Cookie 续期（来源：{source}）...")
                result = browser_driver.refresh_cookies(cookies, game)
                if not result.get("ok"):
                    self.log(f"续期失败：{result['msg']}")
                    messagebox.showerror("续期失败", result["msg"])
                    return

                new_cookies = result["cookies"]
                updated = "、".join(result.get("updated", [])) or "无"
                self.log(f"续期成功，已更新字段：{updated}")

                # 2. 验证续期后的 Cookie
                combo = self.remote_combo or self.captured_combo_token
                vr = browser_driver.check_cookie_valid(new_cookies, game, combo)
                self.log(f"续期后检测结果：{vr.get('msg')}")

                # 3. 写回服务器 / 本地文件
                saved = []
                if self.server_token:
                    up = self._server_request("POST", "/api/upload", {
                        "game": game, "cookies": new_cookies,
                        "combo_token": self.remote_combo or "",
                    })
                    if up.get("ok"):
                        saved.append("服务器")
                    else:
                        self.log(f"上传服务器失败：{up.get('msg')}")
                if file_path and os.path.exists(file_path) and pwd:
                    cookie_crypto.save_encrypted(new_cookies, self.remote_combo or "", pwd, file_path)
                    saved.append("本地文件")
                if self.remote_cookies is not None:
                    self.remote_cookies = new_cookies

                msg = (f"续期成功 ✓\n"
                       f"更新字段：{updated}\n"
                       f"续期后状态：{vr.get('msg')}\n"
                       f"已保存到：{'、'.join(saved) or '（未保存）'}")
                self.log(msg.replace("\n", " | "))
                messagebox.showinfo("续期成功", msg)
            except ValueError as e:
                messagebox.showerror("错误", f"密码错误或文件损坏：\n{e}")
            except Exception as e:
                self.log(f"续期失败：{e}")
                messagebox.showerror("错误", f"续期失败：\n{e}")

        threading.Thread(target=task, daemon=True).start()

    # ── 日志 ──────────────────────────────────────────────
    def log(self, msg: str):
        """线程安全的日志输出"""
        self.log_queue.put(msg)

    def _poll_log(self):
        while not self.log_queue.empty():
            msg = self.log_queue.get_nowait()
            self.log_text.config(state="normal")
            self.log_text.insert("end", msg + "\n")
            self.log_text.see("end")
            self.log_text.config(state="disabled")
        self.root.after(100, self._poll_log)

    # ── Tab1: 抓取 Cookie ─────────────────────────────────
    def _on_open_browser(self):
        game = self.var_game.get()
        gname = browser_driver.GAMES[game]["name"]

        def task():
            try:
                browser_type = self.var_browser.get()
                driver, reused = self._get_capture_driver(game, browser_type)
                if reused:
                    self.log(f"{gname} 的浏览器已开着，直接复用")
                else:
                    self.log(f"已启动 {browser_type.upper()} 浏览器（{gname} 独立 profile）")
                self.log(f"正在打开 {gname}...")
                browser_driver.navigate_to_game(driver, game)
                self.log(f"请在浏览器中手动登录 {gname}，登录成功后回来点「抓取 Cookie」")
                self.btn_capture.config(state="normal")
            except Exception as e:
                self.log(f"启动浏览器失败：{e}")
                messagebox.showerror("错误", f"启动浏览器失败：\n{e}")

        threading.Thread(target=task, daemon=True).start()

    def _get_capture_driver(self, game: str, browser_type: str):
        """取得该游戏的抓取浏览器：已开着就复用，否则用该游戏独立 profile 新开"""
        d = self.capture_drivers.get(game)
        if d is not None:
            try:
                _ = d.current_url  # 探测浏览器是否还活着
                return d, True
            except Exception:
                self.capture_drivers.pop(game, None)
        d = browser_driver.launch_browser(
            browser_type=browser_type,
            user_data_dir=capture_profile_dir(game),
            capture_perf=True,
        )
        self.capture_drivers[game] = d
        return d, False

    def _on_capture(self):
        game = self.var_game.get()
        if self.capture_drivers.get(game) is None:
            messagebox.showwarning(
                "提示", f"请先为「{browser_driver.GAMES[game]['name']}」启动浏览器（游戏切换后需各自启动）"
            )
            return

        def task():
            try:
                game = self.var_game.get()
                cfg = browser_driver.GAMES.get(game, browser_driver.GAMES["genshin"])
                driver = self.capture_drivers.get(game)
                if driver is None:
                    self.log(f"{cfg['name']} 的浏览器未启动，请先点击「启动浏览器并打开游戏」")
                    return
                self.log(f"正在抓取 {cfg['name']} 的 Cookie...")
                cookies = browser_driver.capture_cookies(driver, game)
                self.captured_cookies = cookies
                self.var_cookie_count.set(f"已抓取 {len(cookies)} 个 Cookie")

                if cfg.get("vendor") == "mihoyo":
                    # 米哈游：尝试抓取 combo_token
                    self.log("正在尝试抓取 combo_token（需要页面发起云游戏请求）...")
                    self.log("如果抓取为空，可以在 Edge 里点一下「签到」或「钱包」再点抓取")
                    token = browser_driver.capture_combo_token(driver, timeout=30)
                    self.captured_combo_token = token
                    if token:
                        self.var_combo_token.set(token)
                        self.log(f"combo_token 抓取成功：{token[:20]}...")
                    else:
                        self.var_combo_token.set("（未抓取到，可在 Edge 点签到后重试）")
                        self.log("未抓取到 combo_token，可在 Edge 里点一下「签到」后重试")
                else:
                    # 完美世界 / 哔哩哔哩：会话 Cookie 鉴权，无需 combo_token
                    self.captured_combo_token = ""
                    self.var_combo_token.set("（该站点无需 combo_token）")
                    self.log(f"{cfg['name']} 使用会话 Cookie 鉴权，无需 combo_token")

                if cookies:
                    self.btn_save.config(state="normal")
                    self.log(f"Cookie 抓取完成，共 {len(cookies)} 个，可点击保存")
                else:
                    self.log(f"未抓取到任何 {cfg['name']} Cookie，请确认已在浏览器中登录")
            except Exception as e:
                self.log(f"抓取失败：{e}")
                messagebox.showerror("错误", f"抓取失败：\n{e}")

        threading.Thread(target=task, daemon=True).start()

    def _on_save(self):
        pwd = self.var_capture_pwd.get()
        if len(pwd) < 6:
            messagebox.showwarning("提示", "密码至少 6 位")
            return
        if not self.captured_cookies:
            messagebox.showwarning("提示", "没有可保存的 Cookie")
            return

        game = self.var_game.get()
        save_path = os.path.join(_BASE_DIR, f"cookies_{game}.enc")
        try:
            # 优先使用输入框里的值（用户可能手动粘贴）
            combo_token = self.var_combo_token.get()
            if combo_token.startswith("（") or not combo_token:
                combo_token = self.captured_combo_token
            cookie_crypto.save_encrypted(self.captured_cookies, combo_token, pwd, save_path)
            self.var_file_path.set(save_path)
            self.log(f"已加密保存到 {save_path}")
            messagebox.showinfo("成功", f"Cookie 已加密保存到：\n{save_path}")
        except Exception as e:
            self.log(f"保存失败：{e}")
            messagebox.showerror("错误", f"保存失败：\n{e}")

    # ── Tab2: 一键上号 ────────────────────────────────────
    def _on_browse(self):
        path = filedialog.askopenfilename(
            title="选择加密 Cookie 文件",
            filetypes=[("加密文件", "*.enc"), ("所有文件", "*.*")],
            initialdir=_BASE_DIR,
        )
        if path:
            self.var_file_path.set(path)
            self.remote_cookies = None  # 手动选文件后不再用服务器缓存

    def _on_fetch_from_server(self):
        """从服务器下载 Cookie 到内存"""
        data = self._download_from_server()
        if not data:
            return
        self.remote_cookies = data["cookies"]
        self.remote_combo = data.get("combo_token", "")
        game = self.var_game.get()
        self.log(f"已从服务器获取 {browser_driver.GAMES[game]['name']} 的 {len(self.remote_cookies)} 个 Cookie")
        self.var_status.set("已从服务器获取 Cookie，可直接点击「一键上号」")
        messagebox.showinfo("成功", "Cookie 已从服务器获取，可直接点击「一键上号」")

    def _get_login_driver(self, game: str, browser_type: str):
        """取得该游戏的上号浏览器：已开着就复用，否则用该游戏独立 profile 新开
        每个游戏一个独立 profile，互不冲突，因此可以同时上号多个游戏。
        """
        d = self.login_drivers.get(game)
        if d is not None:
            try:
                _ = d.current_url  # 探测浏览器是否还活着
                return d, True
            except Exception:
                self.login_drivers.pop(game, None)
        d = browser_driver.launch_browser(
            browser_type=browser_type,
            user_data_dir=login_profile_dir(game),
            detach=True,
        )
        self.login_drivers[game] = d
        return d, False

    def _on_login(self):
        file_path = self.var_file_path.get()
        pwd = self.var_login_pwd.get()
        game = self.var_game.get()
        gname = browser_driver.GAMES[game]["name"]

        use_remote = self.remote_cookies is not None
        if not use_remote:
            if not os.path.exists(file_path):
                messagebox.showwarning("提示", "Cookie 文件不存在，或先点「从服务器获取」")
                return
            if not pwd:
                messagebox.showwarning("提示", "请输入加密密码")
                return

        self.btn_login.config(state="disabled")
        self.var_status.set("正在登录...")

        def task():
            try:
                # 1. 取得 Cookie
                if use_remote:
                    cookies = self.remote_cookies
                    combo_token = self.remote_combo
                    self.log(f"使用服务器 Cookie，共 {len(cookies)} 个")
                else:
                    self.log("正在解密 Cookie...")
                    data = cookie_crypto.load_encrypted(file_path, pwd)
                    cookies = data["cookies"]
                    combo_token = data.get("combo_token", "")
                    self.log(f"解密成功，共 {len(cookies)} 个 Cookie")

                # 2. 启动浏览器
                # - user_data_dir=login_profile_dir(game)：每个游戏独立 profile，可同时上号多个游戏
                # - detach=True：工具关闭后浏览器不被杀
                browser_type = self.var_browser.get()
                driver, reused = self._get_login_driver(game, browser_type)
                if reused:
                    self.log(f"复用已打开的 {browser_type.upper()} 浏览器（{gname}）")
                else:
                    self.log(f"正在启动 {browser_type.upper()} 浏览器（{gname} 独立 profile）...")

                # 3. 注入 Cookie
                self.log("正在注入 Cookie...")
                browser_driver.inject_cookies(driver, cookies, game)

                # 4. 跳转对应游戏
                self.log(f"正在跳转到 {gname}...")
                browser_driver.navigate_to_game(driver, game)

                # 5. 检测登录状态
                self.log("正在检测登录状态...")
                time.sleep(3)
                ok = browser_driver.check_login_status(driver, timeout=15, game=game)
                if ok:
                    self.var_status.set("登录成功！可以开始游戏了")
                    self.log("✓ 登录成功，浏览器已交给你操作")
                    self.log("提示：关闭上号器不会关闭浏览器；下次用同一 profile 打开仍保持登录")
                    # 上报登录时间到服务器
                    if self.server_token:
                        try:
                            self._server_request("POST", "/api/report_login", {"game": game})
                            self.log("已上报登录记录到服务器")
                        except Exception:
                            pass
                else:
                    self.var_status.set("可能需要手动确认登录状态")
                    self.log(f"未能自动确认登录状态，请查看 {gname} 页面是否已登录")

                self.log(f"提示：{gname} 用的是独立 profile，可以直接切换游戏再点「一键上号」开另一个")
            except ValueError as e:
                self.log(f"解密失败：{e}")
                messagebox.showerror("错误", f"密码错误或文件损坏：\n{e}")
                self.var_status.set("登录失败")
            except Exception as e:
                self.log(f"登录失败：{e}")
                messagebox.showerror("错误", f"登录失败：\n{e}")
                self.var_status.set("登录失败")
            finally:
                self.btn_login.config(state="normal")

        threading.Thread(target=task, daemon=True).start()


def main():
    root = tk.Tk()
    # Windows 下高 DPI 适配：2 = per-monitor DPI aware，高分屏不糊
    try:
        from ctypes import windll

        windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        pass

    # ttk 全局字体放大
    style = ttk.Style()
    style.configure("TNotebook.Tab", font=("Microsoft YaHei", 12), padding=[12, 6])
    style.configure("TButton", font=("Microsoft YaHei", 11))
    style.configure("TLabel", font=("Microsoft YaHei", 11))

    app = CloudGenshinApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
