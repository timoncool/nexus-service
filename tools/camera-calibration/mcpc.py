import json, sys, urllib.request
def call(method, params):
    tok = json.load(open(r"C:\ProgramData\Nexus\settings.json", encoding="utf-8-sig"))["aiIntegration"]["token"]
    body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params}).encode()
    req = urllib.request.Request("http://127.0.0.1:9420/mcp", data=body, headers={
        "Authorization": "Bearer " + tok, "Content-Type": "application/json", "Accept": "application/json, text/event-stream"})
    raw = urllib.request.urlopen(req, timeout=30).read().decode()
    return json.loads(raw[raw.index("{"):raw.rindex("}") + 1])["result"]
def tool(name, args):
    r = call("tools/call", {"name": name, "arguments": args})
    if r.get("isError"): raise RuntimeError(r["content"][0]["text"])
    return r["content"][0]["text"]
if __name__ == "__main__":
    if sys.argv[1] == "list":
        for t in call("tools/list", {})["tools"]:
            if any(w in (t["name"] + t.get("description", "")).lower() for w in sys.argv[2:]): print(t["name"], "|", t.get("description", "")[:200])
    else:
        print(tool(sys.argv[1], json.loads(sys.argv[2])))
