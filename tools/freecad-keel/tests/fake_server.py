"""A fake Keel server for the tests: the subset of ``/api/v1/cad`` the add-on uses, with the protocol's behaviour that matters (bearer token, locks, SHA-256
checks, atomic check-in, 409 on duplicates, metadata-first multipart). Real sockets (http.server on 127.0.0.1), so the client's HTTP code is really exercised.
It is a model of the documented protocol (docs/integration/cad-connector.md), NOT the Keel server: the real server is covered by the .NET CadApiTests."""
from __future__ import annotations

import hashlib
import io
import json
import re
import threading
import uuid
import zipfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any
from urllib.parse import parse_qs, unquote, urlsplit


def jwt(exp: int = 4_000_000_000) -> str:
    import base64

    def b(d: dict) -> str:
        return base64.urlsafe_b64encode(json.dumps(d).encode()).decode().rstrip("=")

    return f"{b({'alg': 'none'})}.{b({'sub': 'alice', 'exp': exp})}.sig"


TOKEN = jwt()


class Doc:
    def __init__(self, number: str, role: str, files: dict[str, bytes], refs: list[str] | None = None, locked_by: str | None = None):
        self.number, self.role, self.files, self.refs = number, role, files, refs or []
        self.locked_by = locked_by
        self.iteration = 1
        self.working: dict[str, bytes] = {}
        self.markings = {"exportJurisdiction": "None", "distributionStatement": "A", "owningOrg": "ACME", "cuiCategories": []}


class FakeKeel:
    def __init__(self) -> None:
        self.docs: dict[str, Doc] = {}
        self.sets: dict[str, dict[str, Any]] = {}
        self.requests: list[tuple[str, str]] = []
        self.associations: set[tuple[str, str, str]] = set()
        self.proposals: list[dict] = []
        self.corrupt_download: str | None = None
        self.zip_traversal = False
        self.stale_numbers: list[str] = []
        self.user = "alice"
        self.checkins: list[dict] = []
        self.undone: list[str] = []
        self.srv: ThreadingHTTPServer | None = None
        self._lock = threading.Lock()

    # ------------------------------------------------------------ lifecycle
    def start(self) -> str:
        fake = self

        class H(BaseHTTPRequestHandler):
            def log_message(self, *a: Any) -> None:  # silence
                pass

            def _go(self, method: str) -> None:
                length = int(self.headers.get("Content-Length") or 0)
                body = self.rfile.read(length) if length else b""
                with fake._lock:
                    # HTTP header names are case-insensitive (urllib sends "X-content-sha256")
                    status, ctype, payload, extra = fake.handle(method, self.path, {k.title(): v for k, v in self.headers.items()}, body)
                self.send_response(status)
                self.send_header("Content-Type", ctype)
                self.send_header("Content-Length", str(len(payload)))
                for k, v in extra.items():
                    self.send_header(k, v)
                self.end_headers()
                self.wfile.write(payload)

            def do_GET(self) -> None: self._go("GET")
            def do_POST(self) -> None: self._go("POST")
            def do_PUT(self) -> None: self._go("PUT")
            def do_DELETE(self) -> None: self._go("DELETE")

        self.srv = ThreadingHTTPServer(("127.0.0.1", 0), H)
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()
        return f"http://127.0.0.1:{self.srv.server_address[1]}"

    def stop(self) -> None:
        if self.srv:
            self.srv.shutdown()
            self.srv.server_close()

    # ------------------------------------------------------------ helpers
    @staticmethod
    def _json(status: int, obj: Any) -> tuple[int, str, bytes, dict]:
        return status, "application/json", json.dumps(obj).encode(), {}

    @staticmethod
    def _problem(status: int, title: str, code: str | None = None) -> tuple[int, str, bytes, dict]:
        p: dict[str, Any] = {"title": title, "status": status}
        if code:
            p["code"] = code
        return status, "application/problem+json", json.dumps(p).encode(), {}

    def add_doc(self, d: Doc) -> Doc:
        self.docs[d.number] = d
        return d

    def _manifest(self, set_id: str) -> dict:
        s = self.sets[set_id]
        return {"id": set_id, "top": s["top"], "banner": "UNCLASSIFIED", "open": True, "redactedCount": 0, "members": [
            {"number": n, "name": n, "documentType": "CAD", "cadRole": self.docs[n].role, "revision": "A", "iteration": self.docs[n].iteration, "state": "In Work",
             "banner": "UNCLASSIFIED", "isTop": n == s["top"], "isNew": False, "references": self.docs[n].refs,
             "files": [{"role": "primary", "fileName": fn, "mediaType": "application/octet-stream", "sizeBytes": len(b), "sha256": hashlib.sha256(b).hexdigest()}
                       for fn, b in self.docs[n].files.items()]} for n in s["members"]]}

    def _closure(self, top: str) -> list[str]:
        out, stack = [], [top]
        while stack:
            n = stack.pop()
            if n in out:
                continue
            out.append(n)
            stack.extend(self.docs[n].refs)
        # dependents: documents that reference something in the set (drawings)
        for d in self.docs.values():
            if d.number not in out and any(r in out for r in d.refs):
                out.append(d.number)
        return out

    # ------------------------------------------------------------ the protocol
    def handle(self, method: str, raw_path: str, headers: dict, body: bytes) -> tuple[int, str, bytes, dict]:
        parts = urlsplit(raw_path)
        path, query = parts.path, parse_qs(parts.query)
        self.requests.append((method, path))
        if headers.get("Authorization") != f"Bearer {TOKEN}":
            return self._problem(401, "Unauthorized")
        if path == "/redirect-me":
            return 302, "text/plain", b"", {"Location": "http://elsewhere.invalid/"}
        m = re.fullmatch(r"/api/v1/documents/([^/]+)", path)
        if m and method == "GET":
            d = self.docs.get(unquote(m.group(1)))
            if d is None:
                return self._problem(404, "Not found")
            co = {"userId": d.locked_by, "userName": d.locked_by, "isMine": d.locked_by == self.user} if d.locked_by else None
            return self._json(200, {"number": d.number, "name": d.number, "markings": d.markings, "checkout": co})
        if not path.startswith("/api/v1/cad"):
            return self._problem(404, "Not found")
        p = path[len("/api/v1/cad"):]

        if p == "/packages/checkout" and method == "POST":
            req = json.loads(body)
            top = req["top"]
            if top not in self.docs:
                return self._problem(404, "Not found")
            members = self._closure(top)
            busy = [n for n in members if self.docs[n].locked_by not in (None,)]
            if busy:
                return self._problem(409, f"cad.locked: {busy[0]} is checked out by {self.docs[busy[0]].locked_by}.", "cad.locked")
            for n in members:
                self.docs[n].locked_by = self.user
                self.docs[n].working = {}
            sid = str(uuid.uuid4())
            self.sets[sid] = {"top": top, "members": members, "open": True}
            return self._json(201, self._manifest(sid))

        if p == "/checkouts" and method == "GET":
            return self._json(200, [self._manifest(i) for i, s in self.sets.items() if s["open"]])

        m = re.fullmatch(r"/checkouts/([^/]+)/members/([^/]+)/files/([0-9a-f]{64})", p)
        if m and method == "GET":
            sid, number, sha = m.group(1), unquote(m.group(2)), m.group(3)
            d = self.docs[number]
            fn = next((f for f, b in d.files.items() if hashlib.sha256(b).hexdigest() == sha), None)
            if fn is None:
                return self._problem(404, "Not found")
            buf = io.BytesIO()
            with zipfile.ZipFile(buf, "w") as z:
                z.writestr("MARKINGS.txt", "UNCLASSIFIED\n")
                content = b"corrupted" if self.corrupt_download == fn else d.files[fn]
                z.writestr(("../evil/" + fn) if self.zip_traversal else fn, content)
            return 200, "application/zip", buf.getvalue(), {}

        m = re.fullmatch(r"/checkouts/([^/]+)/members/([^/]+)/files", p)
        if m and method == "PUT":
            sid, number = m.group(1), unquote(m.group(2))
            claimed = headers.get("X-Content-Sha256", "")
            if hashlib.sha256(body).hexdigest() != claimed:
                return self._problem(422, "cad.integrity_mismatch: the bytes do not match the SHA-256.", "cad.integrity_mismatch")
            self.docs[number].working[query["fileName"][0]] = body
            return self._json(200, {"fileName": query["fileName"][0], "sha256": claimed})

        m = re.fullmatch(r"/checkouts/([^/]+)/members", p)
        if m and method == "POST":
            sid = m.group(1)
            req = json.loads(body)
            number = f"D-9{len(self.docs):05d}"
            d = Doc(number, req["cadRole"], {}, req.get("references") or [], self.user)
            self.docs[number] = d
            self.sets[sid]["members"].append(number)
            return self._json(201, {"number": number})

        m = re.fullmatch(r"/checkouts/([^/]+)/checkin", p)
        if m and method == "POST":
            sid = m.group(1)
            req = json.loads(body)
            s = self.sets.get(sid)
            if s is None or not s["open"]:
                return self._problem(409, "cad.set_closed: the set is closed.", "cad.set_closed")
            # atomic: verify every expected hash first, apply after
            for mem in req["members"]:
                d = self.docs[mem["number"]]
                for f in mem["files"]:
                    data = d.working.get(f["fileName"], d.files.get(f["fileName"]))
                    if data is None or hashlib.sha256(data).hexdigest() != f["sha256"]:
                        return self._problem(422, "cad.integrity_mismatch: working copy differs.", "cad.integrity_mismatch")
            for mem in req["members"]:
                d = self.docs[mem["number"]]
                d.files.update(d.working)
                d.working = {}
                d.iteration += 1
                if mem.get("references") is not None:
                    d.refs = mem["references"]
                d.locked_by = None
            s["open"] = False
            self.checkins.append(req)
            return self._json(200, {"id": sid, "created": [{"number": mm["number"]} for mm in req["members"]], "released": [], "setClosed": True})

        m = re.fullmatch(r"/checkouts/([^/]+)/undo", p)
        if m and method == "POST":
            sid = m.group(1)
            s = self.sets.get(sid)
            if s is None or not s["open"]:
                return self._problem(409, "cad.set_closed: the set is closed.", "cad.set_closed")
            for n in s["members"]:
                self.docs[n].locked_by = None
                self.docs[n].working = {}
            s["open"] = False
            self.undone.append(sid)
            return self._json(200, {})

        m = re.fullmatch(r"/documents/([^/]+)/package", p)
        if m and method == "GET":
            number = unquote(m.group(1))
            stale = [{"number": n, "name": n, "cadRole": "drawing", "stale": True, "redacted": False, "seenRevision": "A", "seenIteration": 1, "children": []} for n in self.stale_numbers]
            return self._json(200, {"number": number, "name": number, "registered": True, "revision": "A", "iteration": 1, "uses": [], "usedBy": stale, "parts": [],
                                    "redactedCount": 0, "staleCount": 0, "banner": "UNCLASSIFIED"})  # as the real host: staleCount counts only what the top document uses, not its stale dependents

        if p == "/associations" and method == "POST":
            req = json.loads(body)
            key = (req["documentNumber"], req["partNumber"], req["kind"])
            if key in self.associations:
                return self._problem(409, f"{key[0]} is already the {key[2]} {key[1]}.")
            if req["partNumber"].startswith("MISSING"):
                return self._problem(400, f"Part '{req['partNumber']}' was not found, or you are not authorized to see it.")
            self.associations.add(key)
            return self._json(201, {"id": str(uuid.uuid4()), "kind": req["kind"]})

        if p == "/bom-proposals" and method == "POST":
            ctype = headers.get("Content-Type", "")
            boundary = ctype.split("boundary=")[1].encode()
            fields = []
            for chunk in body.split(b"--" + boundary):
                if b"Content-Disposition" not in chunk:
                    continue
                head, _, content = chunk.partition(b"\r\n\r\n")
                name = re.search(rb'name="([^"]+)"', head).group(1).decode()
                fields.append((name, content.rstrip(b"\r\n")))
            if [n for n, _ in fields] != ["metadata", "file"]:
                return self._problem(400, "Field 'metadata' (JSON) is required before 'file'.")
            meta = json.loads(fields[0][1])
            structure = json.loads(fields[1][1])
            proposal = {"number": f"SP-{len(self.proposals) + 1:06d}", "title": meta["title"], "meta": meta, "structure": structure}
            self.proposals.append(proposal)
            return self._json(201, {"number": proposal["number"], "status": "Draft"})

        if p == "/schemas/structure-v1" and method == "GET":
            return 200, "application/schema+json", b"{}", {}
        return self._problem(404, "Not found")
