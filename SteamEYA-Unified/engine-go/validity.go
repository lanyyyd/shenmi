package main

import (
	"fmt"
	"net/url"
	"strconv"
	"strings"
	"time"
)

const (
	ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
		"(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"

	biliNavAPI = "https://api.bilibili.com/x/web-interface/nav"

	wanmeiLoginAPI      = "https://id.wanmei.com/cloud/game/login"
	wanmeiInitConfigAPI = "https://id.wanmei.com/cloud/game/initConfig"
	wanmeiUserInfoAPI   = "https://id.wanmei.com/cloud/game/getUserInfo"
)

// wanmeiParamPairs mirrors _wanmei_params (ordered, also used for the in-page
// form body where Python joins with "&" without URL encoding).
func wanmeiParamPairs() [][2]string {
	return [][2]string{
		{"appId", "10607"},
		{"deviceId", "yhweb_5f3a9c21"},
		{"bid", "com.pwrd.cloud.yh.laohuweb"},
		{"provider", "0"},
		{"channelId", "6800"},
		{"version", "1.1.5"},
		{"sdkVersion", "5.15.3.260622023945-wlweb-release"},
		{"env", "0"},
		{"deviceModel", "Chrome/120.0.0.0 Safari/537.36"},
		{"deviceSys", "6"},
		{"deviceType", "Windows"},
		{"deviceName", "Chrome/120.0.0.0"},
		{"oneAppId", "1289"},
		{"onePlatformId", "22"},
		{"t", strconv.FormatInt(time.Now().UnixMilli(), 10)},
	}
}

func wanmeiParams() url.Values {
	v := url.Values{}
	for _, p := range wanmeiParamPairs() {
		v.Set(p[0], p[1])
	}
	return v
}

func wanmeiBody() string {
	parts := make([]string, 0, 15)
	for _, p := range wanmeiParamPairs() {
		parts = append(parts, p[0]+"="+p[1])
	}
	return strings.Join(parts, "&")
}

type mihoyoWallet struct {
	URL     string
	Host    string
	Referer string
	Origin  string
}

var mihoyoCloudWallet = map[string]mihoyoWallet{
	"genshin": {
		URL:     "https://api-cloudgame.mihoyo.com/hk4e_cg_cn/wallet/wallet/get",
		Host:    "api-cloudgame.mihoyo.com",
		Referer: "https://app.mihoyo.com/",
		Origin:  "https://ys.mihoyo.com",
	},
	"starrail": {
		URL:     "https://cg-hkrpg-api.mihoyo.com/hkrpg_cn/cg/wallet/wallet/get?cost_method=0",
		Host:    "cg-hkrpg-api.mihoyo.com",
		Referer: "https://sr.mihoyo.com/",
		Origin:  "https://sr.mihoyo.com",
	},
}

// mihoyoFreeDuration is _get_mihoyo_free_duration.
func mihoyoFreeDuration(comboToken, game string) jsonObj {
	cfg, ok := mihoyoCloudWallet[game]
	if !ok {
		return jsonObj{"ok": false, "msg": fmt.Sprintf("%s 暂不支持云游戏钱包查询", displayName(game, "", ""))}
	}
	if comboToken == "" {
		return jsonObj{"ok": false, "msg": "缺少 combo_token，无法查询免费时长"}
	}

	headers := map[string]string{
		"Host":              cfg.Host,
		"Accept":            "*/*",
		"Referer":           cfg.Referer,
		"Origin":            cfg.Origin,
		"x-rpc-combo_token": comboToken,
		"Accept-Encoding":   "gzip, deflate",
		"User-Agent":        ua,
	}
	if game == "starrail" {
		headers["x-rpc-client_type"] = "3"
		headers["x-rpc-cg_game_biz"] = "hkrpg_cn"
		headers["x-rpc-cg_game_id"] = "9000096"
		headers["x-rpc-vendor_id"] = "2"
		headers["x-rpc-op_biz"] = "clgm_hkrpg-cn"
		headers["x-rpc-language"] = "zh-cn"
		headers["Connection"] = "Keep-Alive"
	}

	data, err := getJSON(cfg.URL, headers, nil)
	if err != nil {
		return jsonObj{"ok": false, "msg": "请求失败：" + err.Error()}
	}

	retcode := data["retcode"]
	if numEq(retcode, -100) {
		return jsonObj{"ok": false, "expired": true, "msg": "combo_token 已失效，请重新抓取"}
	}
	if !numEq(retcode, 0) {
		return jsonObj{"ok": false, "msg": fmt.Sprintf("%s（retcode=%s）",
			pyStrDefault(data["message"], "查询失败"), pyStr(retcode))}
	}

	d := asMap(data["data"])
	ft := asMap(d["free_time"])
	var freeDuration any
	if v, ok := pyInt(ft["free_time"]); ok {
		freeDuration = v
	}
	return jsonObj{
		"ok":             true,
		"free_duration":  freeDuration,
		"total_duration": nil,
		"play_card":      pyStr(asMap(d["play_card"])["short_msg"]),
		"coin":           normalizeNumber(asMap(d["coin"])["coin_num"]),
		"msg":            "查询成功",
	}
}

// checkCookieValid is check_cookie_valid.
func checkCookieValid(cookies []map[string]any, game, comboToken string) jsonObj {
	cfg, ok := games[game]
	if !ok {
		cfg = games["genshin"]
	}
	order, cmap := orderedCookieValues(cookies)
	if len(cmap) == 0 {
		return jsonObj{"valid": nil, "msg": "Cookie 为空"}
	}

	if cfg.Vendor == "wanmei" {
		return checkWanmeiValid(order, cmap)
	}
	if cfg.Vendor == "bilibili" {
		return checkBilibiliValid(order, cmap)
	}

	cookieStr := cookieMapString(order, cmap)
	target := "https://api-takumi.mihoyo.com/binding/api/getUserGameRolesByCookie?game_biz=" + cfg.Biz
	headers := map[string]string{
		"Cookie":     cookieStr,
		"User-Agent": ua,
		"Referer":    "https://webstatic.mihoyo.com/",
		"Accept":     "application/json, text/plain, */*",
	}
	data, err := getJSON(target, headers, nil)
	if err != nil {
		return jsonObj{"valid": nil, "msg": "请求失败：" + err.Error()}
	}

	retcode := data["retcode"]
	if numEq(retcode, 0) {
		roles := asList(asMap(data["data"])["list"])
		result := jsonObj{
			"valid":    true,
			"msg":      "Cookie 有效",
			"nickname": "",
			"game_uid": "",
			"level":    nil,
			"region":   "",
		}
		if len(roles) > 0 {
			role := asMap(roles[0])
			result["nickname"] = pyStrDefault(role["nickname"], "")
			result["game_uid"] = pyStr(role["game_uid"])
			result["level"] = normalizeNumber(role["level"])
			result["region"] = pyStrDefault(role["region_name"], "")
		} else {
			result["msg"] = "Cookie 有效（未查询到角色）"
		}

		if comboToken != "" {
			wr := mihoyoFreeDuration(comboToken, game)
			if pyTruthy(wr["ok"]) {
				result["free_duration"] = wr["free_duration"]
				result["total_duration"] = wr["total_duration"]
				if fd, ok := wr["free_duration"].(int64); ok {
					result["msg"] = fmt.Sprintf("Cookie 有效，剩余免费时长 %d 分钟", fd)
				}
			}
		}
		return result
	}

	if numEq(retcode, -100) || numEq(retcode, -101) || numEq(retcode, 10001) || numEq(retcode, -110) {
		return jsonObj{"valid": false, "msg": pyStrDefault(data["message"], "Cookie 已失效")}
	}
	return jsonObj{"valid": false, "msg": fmt.Sprintf("%s（retcode=%s）",
		pyStrDefault(data["message"], "验证失败"), pyStr(retcode))}
}

// checkWanmeiValid is _check_wanmei_valid.
func checkWanmeiValid(order []string, cmap map[string]string) jsonObj {
	cookieStr := cookieMapString(order, cmap)

	headers := map[string]string{
		"Cookie":           cookieStr,
		"User-Agent":       ua,
		"Referer":          "https://yh.wanmei.com/",
		"Origin":           "https://yh.wanmei.com",
		"Accept":           "application/json, text/javascript, */*; q=0.01",
		"X-Requested-With": "XMLHttpRequest",
	}

	valid := false
	uid := int64(0)
	failMsg := ""

	data, err := requestJSON("POST", wanmeiLoginAPI, headers, nil, wanmeiParams())
	if err != nil {
		failMsg = "请求失败：" + err.Error()
	} else {
		code := data["code"]
		switch {
		case numEq(code, 0):
			valid = true
			uid = findUserID(data["result"], 0)
		case numEq(code, 10004):
			return jsonObj{"valid": false, "msg": pyStrDefault(data["message"], "登录态失效")}
		default:
			failMsg = fmt.Sprintf("%s（code=%s）", pyStrDefault(data["message"], "验证失败"), pyStr(code))
		}
	}

	if valid && uid == 0 {
		if d2, err := getJSON(wanmeiInitConfigAPI, headers, wanmeiParams()); err == nil && numEq(d2["code"], 0) {
			uid = findUserID(d2["result"], 0)
		}
	}

	var freeDuration, totalDuration any
	if uid != 0 {
		query := wanmeiParams()
		query.Set("userId", strconv.FormatInt(uid, 10))
		if u, err := getJSON(wanmeiUserInfoAPI, headers, query); err == nil {
			ucode := u["code"]
			if numEq(ucode, 0) {
				res := asMap(u["result"])
				valid = true
				if v, ok := numInt(res["userId"]); ok && v != 0 {
					uid = v
				}
				freeDuration = normalizeNumber(res["remainedFreeDuration"])
				totalDuration = normalizeNumber(res["remainedDuration"])
			} else if numEq(ucode, 10004) {
				return jsonObj{"valid": false, "msg": pyStrDefault(u["message"], "登录态失效")}
			}
		}
	}

	if valid {
		msg := "Cookie 有效"
		if freeDuration != nil {
			msg += fmt.Sprintf("，剩余免费时长 %s 分钟", pyStr(freeDuration))
		}
		uidStr := ""
		if uid != 0 {
			uidStr = strconv.FormatInt(uid, 10)
		}
		return jsonObj{
			"valid":          true,
			"msg":            msg,
			"game_uid":       uidStr,
			"nickname":       "",
			"free_duration":  freeDuration,
			"total_duration": totalDuration,
		}
	}
	if failMsg == "" {
		failMsg = "无法确定登录态"
	}
	return jsonObj{"valid": nil, "msg": failMsg}
}

// checkBilibiliValid is _check_bilibili_valid.
func checkBilibiliValid(order []string, cmap map[string]string) jsonObj {
	if cmap["SESSDATA"] == "" && cmap["DedeUserID"] == "" {
		return jsonObj{"valid": false, "msg": "Cookie 中缺少 SESSDATA / DedeUserID，未登录"}
	}
	cookieStr := cookieMapString(order, cmap)
	headers := map[string]string{
		"Cookie":     cookieStr,
		"User-Agent": ua,
		"Referer":    "https://www.bilibili.com/",
		"Accept":     "application/json, text/plain, */*",
	}
	data, err := getJSON(biliNavAPI, headers, nil)
	if err != nil {
		return jsonObj{"valid": nil, "msg": "请求失败：" + err.Error()}
	}

	code := data["code"]
	d := asMap(data["data"])
	if numEq(code, 0) && pyTruthy(d["isLogin"]) {
		gameUID := ""
		if pyTruthy(d["mid"]) {
			gameUID = pyStr(d["mid"])
		}
		return jsonObj{
			"valid":         true,
			"msg":           "Cookie 有效",
			"nickname":      pyStrDefault(d["uname"], ""),
			"game_uid":      gameUID,
			"level":         normalizeNumber(asMap(d["level_info"])["current_level"]),
			"region":        "",
			"free_duration": nil,
		}
	}
	if numEq(code, -101) {
		return jsonObj{"valid": false, "msg": "账号未登录，Cookie 已失效"}
	}
	return jsonObj{"valid": false, "msg": fmt.Sprintf("%s（code=%s）",
		pyStrDefault(data["message"], "验证失败"), pyStr(code))}
}

// findUserID is _find_user_id (depth limited to 4, integers only).
func findUserID(v any, depth int) int64 {
	if depth > 4 || v == nil {
		return 0
	}
	switch obj := v.(type) {
	case map[string]any:
		for k, val := range obj {
			lower := strings.ToLower(k)
			if lower == "userid" || lower == "uid" || lower == "user_id" {
				if i, ok := numInt(val); ok && i > 0 {
					return i
				}
			}
			switch val.(type) {
			case map[string]any, []any:
				if got := findUserID(val, depth+1); got != 0 {
					return got
				}
			}
		}
	case []any:
		for _, item := range obj {
			if got := findUserID(item, depth+1); got != 0 {
				return got
			}
		}
	}
	return 0
}
