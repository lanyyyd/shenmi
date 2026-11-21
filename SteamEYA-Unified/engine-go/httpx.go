package main

import (
	"compress/gzip"
	"compress/zlib"
	"encoding/json"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
)

// apiClient bounds the vendor API calls. Python's requests calls have no
// explicit timeout; 60s keeps a single request from hanging forever while
// staying far more generous than the client's 90s overall budget.
var apiClient = &http.Client{Timeout: 60 * time.Second}

// jsonObj is the generic decoded JSON object (json.Number preserved).
type jsonObj = map[string]any

// requestJSON performs an HTTP request and decodes a JSON object response.
// query is appended to the URL, form is sent as application/x-www-form-urlencoded.
func requestJSON(method, rawURL string, headers map[string]string, query, form url.Values) (jsonObj, error) {
	u, err := url.Parse(rawURL)
	if err != nil {
		return nil, err
	}
	if len(query) > 0 {
		q := u.Query()
		for k, vs := range query {
			for _, v := range vs {
				q.Add(k, v)
			}
		}
		u.RawQuery = q.Encode()
	}

	var body io.Reader
	if form != nil {
		body = strings.NewReader(form.Encode())
	}
	req, err := http.NewRequest(method, u.String(), body)
	if err != nil {
		return nil, err
	}
	for k, v := range headers {
		if strings.EqualFold(k, "Host") {
			req.Host = v
			continue
		}
		req.Header.Set(k, v)
	}
	if form != nil && req.Header.Get("Content-Type") == "" {
		req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	}

	resp, err := apiClient.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()

	// Python's requests transparently decodes compressed responses; since we
	// set Accept-Encoding ourselves, Go's transport will not.
	var bodyReader io.Reader = resp.Body
	switch strings.ToLower(resp.Header.Get("Content-Encoding")) {
	case "gzip":
		zr, err := gzip.NewReader(resp.Body)
		if err != nil {
			return nil, err
		}
		defer zr.Close()
		bodyReader = zr
	case "deflate":
		zr, err := zlib.NewReader(resp.Body)
		if err != nil {
			return nil, err
		}
		defer zr.Close()
		bodyReader = zr
	}

	dec := json.NewDecoder(bodyReader)
	dec.UseNumber()
	var out jsonObj
	if err := dec.Decode(&out); err != nil {
		return nil, err
	}
	return out, nil
}

// getJSON is requests.get(...).json().
func getJSON(rawURL string, headers map[string]string, query url.Values) (jsonObj, error) {
	return requestJSON(http.MethodGet, rawURL, headers, query, nil)
}

// asMap returns v as a JSON object, or nil.
func asMap(v any) jsonObj {
	m, _ := v.(map[string]any)
	return m
}

// asList returns v as a JSON array, or nil.
func asList(v any) []any {
	l, _ := v.([]any)
	return l
}

// pyTruthy mirrors Python truthiness for JSON values.
func pyTruthy(v any) bool {
	switch x := v.(type) {
	case nil:
		return false
	case bool:
		return x
	case string:
		return x != ""
	case json.Number:
		f, err := x.Float64()
		return err == nil && f != 0
	case float64:
		return x != 0
	case int:
		return x != 0
	case int64:
		return x != 0
	default:
		return true
	}
}

// pyStr mirrors str(value or "").
func pyStr(v any) string {
	if v == nil {
		return ""
	}
	switch x := v.(type) {
	case string:
		return x
	case json.Number:
		return x.String()
	case bool:
		if x {
			return "True"
		}
		return "False"
	case float64:
		return strconv.FormatFloat(x, 'f', -1, 64)
	default:
		return ""
	}
}

// pyOr mirrors Python's `value or default`.
func pyOr(v any, def string) string {
	if !pyTruthy(v) {
		return def
	}
	return pyStr(v)
}

// pyStrDefault is data.get(key, def) then str().
func pyStrDefault(v any, def string) string {
	if v == nil {
		return def
	}
	s := pyStr(v)
	if s == "" {
		if _, ok := v.(string); ok {
			return ""
		}
		return def
	}
	return s
}

// numInt converts a JSON integer (json.Number) to int64.
func numInt(v any) (int64, bool) {
	switch x := v.(type) {
	case json.Number:
		i, err := x.Int64()
		if err != nil {
			return 0, false
		}
		return i, true
	case float64:
		if x != float64(int64(x)) {
			return 0, false
		}
		return int64(x), true
	case int64:
		return x, true
	case int:
		return int64(x), true
	}
	return 0, false
}

// numEq mirrors `value == n` where the value came from json.
func numEq(v any, n int64) bool {
	i, ok := numInt(v)
	return ok && i == n
}

// pyInt mirrors int(value) with Python's TypeError/ValueError on failure.
func pyInt(v any) (int64, bool) {
	switch x := v.(type) {
	case json.Number:
		if i, err := x.Int64(); err == nil {
			return i, true
		}
		if f, err := x.Float64(); err == nil {
			return int64(f), true
		}
		return 0, false
	case float64:
		return int64(x), true
	case bool:
		if x {
			return 1, true
		}
		return 0, true
	case string:
		s := strings.TrimSpace(x)
		if s == "" {
			return 0, false
		}
		if i, err := strconv.ParseInt(s, 10, 64); err == nil {
			return i, true
		}
		if f, err := strconv.ParseFloat(s, 64); err == nil {
			return int64(f), true
		}
		return 0, false
	case int64:
		return x, true
	case int:
		return int64(x), true
	}
	return 0, false
}

// normalizeNumber converts json.Number to plain int64/float64 so that
// re-encoding matches Python's json output.
func normalizeNumber(v any) any {
	if n, ok := v.(json.Number); ok {
		if i, err := n.Int64(); err == nil {
			return i
		}
		if f, err := n.Float64(); err == nil {
			return f
		}
		return n.String()
	}
	return v
}

// cookieJarString builds "k=v; k=v" preserving cookie order.
func cookieJarString(cookies []map[string]any) string {
	var b strings.Builder
	for _, c := range cookies {
		name, _ := c["name"].(string)
		if name == "" {
			continue
		}
		if b.Len() > 0 {
			b.WriteString("; ")
		}
		b.WriteString(name)
		b.WriteString("=")
		b.WriteString(pyStr(c["value"]))
	}
	return b.String()
}

// orderedCookieValues is `{c["name"]: c["value"] for c in cookies if c["name"]}`
// while keeping the first-seen key order of the source list.
func orderedCookieValues(cookies []map[string]any) ([]string, map[string]string) {
	order := make([]string, 0, len(cookies))
	values := make(map[string]string, len(cookies))
	for _, c := range cookies {
		name, ok := c["name"].(string)
		if !ok || name == "" {
			continue
		}
		if _, seen := values[name]; !seen {
			order = append(order, name)
		}
		values[name] = pyStr(c["value"])
	}
	return order, values
}

func cookieMapString(order []string, values map[string]string) string {
	var b strings.Builder
	for _, name := range order {
		if b.Len() > 0 {
			b.WriteString("; ")
		}
		b.WriteString(name)
		b.WriteString("=")
		b.WriteString(values[name])
	}
	return b.String()
}
