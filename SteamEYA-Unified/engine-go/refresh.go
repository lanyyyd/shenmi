package main

import (
	"crypto/md5"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"math/rand"
	"net/url"
	"sort"
	"strconv"
	"strings"
	"time"
)

const (
	passportAPI    = "https://passport-api.mihoyo.com"
	ltokenURL      = passportAPI + "/account/auth/api/getLTokenBySToken"
	cookieTokenURL = passportAPI + "/account/auth/api/getCookieAccountInfoBySToken"
	passportX4Salt = "xV8v4Qu54lUKrEYFZkJhB8cuOh9Asafs"
	bbsVersion     = "2.106.2"
	bbsUA          = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) miHoYoBBS/" + bbsVersion
	loginTicketAPI = "https://api-takumi.mihoyo.com/auth/api/getMultiTokenByLoginTicket"
)

func md5Hex(s string) string {
	sum := md5.Sum([]byte(s))
	return hex.EncodeToString(sum[:])
}

func sha256Hex(s string) string {
	sum := sha256.Sum256([]byte(s))
	return hex.EncodeToString(sum[:])
}

// dsX4 is _ds_x4: t,r,md5(salt&t&r&b&q).
func dsX4(query string) string {
	t := strconv.FormatInt(time.Now().Unix(), 10)
	r := strconv.Itoa(100000 + rand.Intn(100001))
	sign := md5Hex("salt=" + passportX4Salt + "&t=" + t + "&r=" + r + "&b=&q=" + query)
	return t + "," + r + "," + sign
}

// exchangeByStoken is _exchange_by_stoken.
func exchangeByStoken(rawURL, stoken, mid, deviceID, clientType string) jsonObj {
	headers := map[string]string{
		"user-agent":        bbsUA,
		"x-rpc-app_version": bbsVersion,
		"x-rpc-client_type": clientType,
		"x-requested-with":  "com.mihoyo.hyperion",
		"referer":           "https://webstatic.mihoyo.com",
		"x-rpc-device_id":   deviceID,
		"ds":                dsX4("stoken=" + stoken),
		"cookie":            "mid=" + mid + ";stoken=" + stoken,
		"accept":            "application/json, text/plain, */*",
	}
	if clientType == "2" {
		headers["x-rpc-aigis"] = ""
	}

	query := url.Values{"stoken": {stoken}}
	data, err := getJSON(rawURL, headers, query)
	if err != nil {
		return jsonObj{"ok": false, "msg": "请求失败：" + err.Error()}
	}
	if !numEq(data["retcode"], 0) {
		return jsonObj{
			"ok":  false,
			"msg": fmt.Sprintf("%s（retcode=%s）", pyOr(data["message"], "续期失败"), pyStr(data["retcode"])),
		}
	}
	return jsonObj{"ok": true, "value": asMap(data["data"]), "msg": ""}
}

// applyTokens is _apply_tokens.
func applyTokens(cookies []map[string]any, ltoken, cookieToken string) ([]map[string]any, []string) {
	out := make([]map[string]any, 0, len(cookies)+2)
	updated := map[string]bool{}
	existing := map[string]bool{}

	for _, c := range cookies {
		c2 := make(map[string]any, len(c))
		for k, v := range c {
			c2[k] = v
		}
		name, _ := c2["name"].(string)
		switch name {
		case "ltoken", "ltoken_v2":
			c2["value"] = ltoken
			updated[name] = true
		case "cookie_token", "cookie_token_v2":
			c2["value"] = cookieToken
			updated[name] = true
		}
		existing[name] = true
		out = append(out, c2)
	}

	if !existing["ltoken"] {
		out = append(out, map[string]any{
			"name": "ltoken", "value": ltoken, "domain": ".mihoyo.com", "path": "/",
		})
		updated["ltoken"] = true
	}
	if !existing["cookie_token"] {
		out = append(out, map[string]any{
			"name": "cookie_token", "value": cookieToken, "domain": ".mihoyo.com", "path": "/",
		})
		updated["cookie_token"] = true
	}

	names := make([]string, 0, len(updated))
	for k := range updated {
		names = append(names, k)
	}
	sort.Strings(names)
	return out, names
}

// refreshCookies is refresh_cookies.
func refreshCookies(cookies []map[string]any, game string) jsonObj {
	cfg := cfgFor(game, "")
	if cfg.Vendor != "mihoyo" {
		return jsonObj{
			"ok":      false,
			"msg":     fmt.Sprintf("%s 使用会话 Cookie，不支持自动续期", cfg.Name),
			"cookies": cookies,
			"updated": []string{},
		}
	}

	_, cmap := orderedCookieValues(cookies)
	stoken := cmap["stoken"]
	mid := firstNonEmpty(cmap["mid"], cmap["account_mid_v2"], cmap["ltmid_v2"])
	uid := firstNonEmpty(cmap["stuid"], cmap["account_id"], cmap["ltuid"], cmap["ltuid_v2"], cmap["account_id_v2"])

	stokenMsg := "Cookie 中没有 stoken"
	if stoken != "" {
		if strings.HasPrefix(stoken, "v2_") && mid == "" {
			stokenMsg = "v2 stoken 缺少 mid，无法自动续期"
		} else {
			deviceID := sha256Hex(fmt.Sprintf("teatt-engine:%s:%s", uid, tail(stoken, 8)))[:32]
			lt := exchangeByStoken(ltokenURL, stoken, mid, deviceID, "5")
			ct := exchangeByStoken(cookieTokenURL, stoken, mid, deviceID, "2")
			if pyTruthy(lt["ok"]) && pyTruthy(ct["ok"]) {
				newLtoken := pyStr(asMap(lt["value"])["ltoken"])
				newCookie := pyStr(asMap(ct["value"])["cookie_token"])
				if newLtoken != "" && newCookie != "" {
					newCookies, updated := applyTokens(cookies, newLtoken, newCookie)
					return jsonObj{"ok": true, "cookies": newCookies, "msg": "续期成功", "updated": updated}
				}
				stokenMsg = "续期接口未返回新 token"
			} else {
				failed := lt
				if pyTruthy(lt["ok"]) {
					failed = ct
				}
				stokenMsg = "stoken 续期失败：" + pyStr(failed["msg"])
			}
		}
	}

	// Fallback: very old accounts may only carry login_ticket.
	loginTicket := cmap["login_ticket"]
	if loginTicket == "" {
		return jsonObj{
			"ok":      false,
			"msg":     stokenMsg + "，且 Cookie 中没有 login_ticket",
			"cookies": cookies,
			"updated": []string{},
		}
	}
	if uid == "" {
		return jsonObj{
			"ok":      false,
			"msg":     "Cookie 中没有 account_id / stuid，无法自动续期",
			"cookies": cookies,
			"updated": []string{},
		}
	}

	headers := map[string]string{
		"User-Agent": ua,
		"Referer":    "https://webstatic.mihoyo.com/",
		"Accept":     "application/json, text/plain, */*",
	}
	jar := "login_ticket=" + loginTicket + "; stuid=" + uid
	if stoken != "" {
		jar += "; stoken=" + stoken
	}
	headers["Cookie"] = jar

	query := url.Values{
		"login_ticket": {loginTicket},
		"uid":          {uid},
		"token_types":  {"3"},
	}
	data, err := getJSON(loginTicketAPI, headers, query)
	if err != nil {
		return jsonObj{
			"ok":      false,
			"msg":     "请求失败：" + err.Error(),
			"cookies": cookies,
			"updated": []string{},
		}
	}
	if !numEq(data["retcode"], 0) {
		return jsonObj{
			"ok":      false,
			"msg":     fmt.Sprintf("续期失败：%s（retcode=%s）", pyStr(data["message"]), pyStr(data["retcode"])),
			"cookies": cookies,
			"updated": []string{},
		}
	}

	tokens := asList(asMap(data["data"])["list"])
	if len(tokens) == 0 {
		return jsonObj{"ok": false, "msg": "续期接口未返回新 token", "cookies": cookies, "updated": []string{}}
	}

	mapping := map[string]string{}
	for _, item := range tokens {
		t := asMap(item)
		token := pyStr(t["token"])
		if token == "" {
			continue
		}
		switch {
		case numEq(t["token_type"], 1):
			mapping["cookie_token"] = token
		case numEq(t["token_type"], 2):
			mapping["ltoken"] = token
		}
	}
	if mapping["ltoken"] == "" || mapping["cookie_token"] == "" {
		return jsonObj{"ok": false, "msg": "续期接口返回的 token 类型无法识别", "cookies": cookies, "updated": []string{}}
	}

	newCookies, updated := applyTokens(cookies, mapping["ltoken"], mapping["cookie_token"])
	return jsonObj{"ok": true, "cookies": newCookies, "msg": "续期成功（login_ticket）", "updated": updated}
}

func firstNonEmpty(values ...string) string {
	for _, v := range values {
		if v != "" {
			return v
		}
	}
	return ""
}

func tail(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return s[len(s)-n:]
}
