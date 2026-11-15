#!/usr/bin/env python3
"""Fill a UEToVS golden file with clangd's answers (accuracy benchmark, see docs/基准测试.md).

Input: the JSON lines written by `UEToVS.Engine bench --emit-samples <file>`:
    {"file": "D:/Lyra/Source/...cpp", "line": 12, "col": 5, "word": "Foo", "expected": []}
Output: the same lines with "expected" filled with clangd's go-to-definition and go-to-declaration locations
(1-based lines). Positions clangd cannot answer keep an empty "expected" and are not scored by `bench --golden`.

Usage:
    python scripts/golden_clangd.py samples.jsonl golden.jsonl --compile-commands-dir D:/Lyra
                                    [--clangd clangd] [--timeout 120] [--limit 0]

compile_commands.json comes from UBT, e.g. for Lyra:
    Engine\\Build\\BatchFiles\\RunUBT.bat -mode=GenerateClangDatabase -project=D:\\Lyra\\Lyra.uproject LyraEditor Win64 Development
(UBT writes it to the engine root; pass that folder, or copy the file next to the .uproject.)

Only the Python standard library is used.
"""
import argparse
import json
import os
import subprocess
import sys
import threading
import time
import urllib.parse
import urllib.request
from pathlib import Path


def path_to_uri(path: str) -> str:
    return Path(path).resolve().as_uri()


def uri_to_path(uri: str) -> str:
    if not uri.startswith("file:"):
        return uri
    path = urllib.request.url2pathname(urllib.parse.urlparse(uri).path)
    # url2pathname on Windows returns "\\C:\\x" for "file:///C:/x" in some versions
    if os.name == "nt" and path.startswith("\\") and len(path) > 2 and path[2] == ":":
        path = path[1:]
    return path


class LspClient:
    """Minimal JSON-RPC over stdio client for clangd (requests are answered in order of arrival)."""

    def __init__(self, cmd, timeout):
        self.proc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        self.timeout = timeout
        self.next_id = 0
        self.responses = {}
        self.cv = threading.Condition()
        self.reader = threading.Thread(target=self._read_loop, daemon=True)
        self.reader.start()

    def _read_loop(self):
        out = self.proc.stdout
        while True:
            headers = {}
            while True:
                line = out.readline()
                if not line:
                    with self.cv:
                        self.cv.notify_all()
                    return
                line = line.decode("ascii", "replace").strip()
                if not line:
                    break
                k, _, v = line.partition(":")
                headers[k.strip().lower()] = v.strip()
            body = out.read(int(headers.get("content-length", "0")))
            try:
                msg = json.loads(body.decode("utf-8"))
            except ValueError:
                continue
            if "id" in msg and ("result" in msg or "error" in msg):
                with self.cv:
                    self.responses[msg["id"]] = msg
                    self.cv.notify_all()

    def _send(self, msg):
        data = json.dumps(msg).encode("utf-8")
        self.proc.stdin.write(b"Content-Length: %d\r\n\r\n" % len(data) + data)
        self.proc.stdin.flush()

    def notify(self, method, params):
        self._send({"jsonrpc": "2.0", "method": method, "params": params})

    def request(self, method, params):
        self.next_id += 1
        rid = self.next_id
        self._send({"jsonrpc": "2.0", "id": rid, "method": method, "params": params})
        deadline = time.time() + self.timeout
        with self.cv:
            while rid not in self.responses:
                left = deadline - time.time()
                if left <= 0 or self.proc.poll() is not None:
                    return None
                self.cv.wait(left)
            msg = self.responses.pop(rid)
        return msg.get("result")

    def close(self):
        try:
            self.request("shutdown", None)
            self.notify("exit", None)
        except OSError:
            pass  # clangd already gone
        try:
            self.proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.proc.kill()


def locations(result):
    """LSP Location | Location[] | LocationLink[] -> [(path, 1-based line)]."""
    if not result:
        return []
    if isinstance(result, dict):
        result = [result]
    found = []
    for item in result:
        uri = item.get("uri") or item.get("targetUri")
        rng = item.get("range") or item.get("targetSelectionRange") or item.get("targetRange")
        if uri and rng:
            found.append((uri_to_path(uri), rng["start"]["line"] + 1))
    return found


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("samples", help="bench --emit-samples output")
    ap.add_argument("golden", help="file to write")
    ap.add_argument("--compile-commands-dir", required=True, help="folder holding compile_commands.json")
    ap.add_argument("--clangd", default="clangd", help="clangd executable (default: clangd on PATH)")
    ap.add_argument("--timeout", type=float, default=120, help="seconds per request (first request of a file parses it)")
    ap.add_argument("--limit", type=int, default=0, help="only the first N positions (0 = all)")
    args = ap.parse_args(argv)

    entries = []
    with open(args.samples, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                entries.append(json.loads(line))
    if args.limit > 0:
        entries = entries[: args.limit]

    cmd = [args.clangd, "--compile-commands-dir=" + args.compile_commands_dir, "--background-index=false",
           "--header-insertion=never", "--log=error"]
    client = LspClient(cmd, args.timeout)
    root = Path(args.compile_commands_dir).resolve()
    client.request("initialize", {"processId": os.getpid(), "rootUri": root.as_uri(), "capabilities": {}})
    client.notify("initialized", {})

    by_file = {}
    for i, e in enumerate(entries):
        by_file.setdefault(e["file"], []).append(i)

    answered = 0
    started = time.time()
    for n, (path, indexes) in enumerate(sorted(by_file.items()), 1):
        try:
            text = Path(path).read_text(encoding="utf-8-sig", errors="replace")
        except OSError as ex:
            print(f"skip {path}: {ex}", file=sys.stderr)
            continue
        uri = path_to_uri(path)
        client.notify("textDocument/didOpen", {"textDocument": {"uri": uri, "languageId": "cpp", "version": 1, "text": text}})
        for i in indexes:
            e = entries[i]
            pos = {"textDocument": {"uri": uri}, "position": {"line": e["line"] - 1, "character": e["col"] - 1}}
            found = locations(client.request("textDocument/definition", pos)) + locations(client.request("textDocument/declaration", pos))
            seen, expected = set(), []
            for p, ln in found:
                key = (os.path.normcase(os.path.normpath(p)), ln)
                if key not in seen:
                    seen.add(key)
                    expected.append({"file": p, "line": ln})
            e["expected"] = expected
            if expected:
                answered += 1
        client.notify("textDocument/didClose", {"textDocument": {"uri": uri}})
        print(f"[{n}/{len(by_file)}] {path}  answered {answered}/{len(entries)}  {time.time() - started:.0f}s", file=sys.stderr)

    client.close()
    with open(args.golden, "w", encoding="utf-8") as f:
        for e in entries:
            f.write(json.dumps(e, ensure_ascii=False) + "\n")
    print(f"wrote {args.golden}: {answered} of {len(entries)} positions have an answer")
    return 0 if answered else 1


if __name__ == "__main__":
    sys.exit(main())
