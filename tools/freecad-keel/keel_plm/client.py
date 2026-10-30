"""HTTP client for the Keel CAD API (``/api/v1/cad``) using only the Python standard library. The calls are those of ``keel-cad`` (src/Keel.CadAgent)."""
from __future__ import annotations

import hashlib
import io
import json
import ssl
import uuid
import urllib.error
import urllib.parse
import urllib.request
from collections.abc import Callable
from pathlib import Path
from typing import Any

from .settings import Settings, validate_server_url

CAD = "/api/v1/cad"


class KeelError(Exception):
    """The server refused a request (status and the protocol's stable ``code``, for example ``cad.locked``), or could not be reached (status 0)."""

    def __init__(self, message: str, status: int = 0, code: str | None = None):
        super().__init__(message)
        self.status = status
        self.code = code

    def __str__(self) -> str:
        base = super().__str__()
        return f"{base} [{self.code}]" if self.code else base


def sha256_file(path: Path | str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    """A redirect would carry the bearer token to another host; the API never redirects, so any redirect is refused."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):  # noqa: D401, ANN001
        return None


class KeelClient:
    def __init__(self, base_url: str, token: Callable[[], str] | str, *, ca_file: str | None = None, allow_insecure_http: bool = False, timeout: float = 60.0):
        self.base_url = validate_server_url(base_url, allow_insecure_http)
        self._token = token if callable(token) else (lambda t=token: t)
        self.timeout = timeout
        handlers: list[urllib.request.BaseHandler] = [_NoRedirect()]
        if self.base_url.startswith("https://"):
            ctx = ssl.create_default_context(cafile=ca_file or None)
            handlers.append(urllib.request.HTTPSHandler(context=ctx))
        self._opener = urllib.request.build_opener(*handlers)

    @classmethod
    def from_settings(cls, settings: Settings, token: Callable[[], str] | str) -> "KeelClient":
        return cls(settings.server_url, token, ca_file=settings.ca_file or None, allow_insecure_http=settings.allow_insecure_http, timeout=settings.timeout_seconds)

    # ------------------------------------------------------------------ plumbing

    def _request(self, method: str, path: str, *, body: Any = None, raw: bytes | None = None, headers: dict[str, str] | None = None) -> tuple[bytes, Any]:
        url = self.base_url + path
        data = raw
        hdrs = {"Authorization": f"Bearer {self._token()}", "Accept": "application/json, application/problem+json, application/zip"}
        if body is not None:
            data = json.dumps(body).encode("utf-8")
            hdrs["Content-Type"] = "application/json"
        if headers:
            hdrs.update(headers)
        req = urllib.request.Request(url, data=data, method=method, headers=hdrs)
        try:
            with self._opener.open(req, timeout=self.timeout) as res:
                return res.read(), res.headers
        except urllib.error.HTTPError as e:
            raise self._error(e) from None
        except urllib.error.URLError as e:
            raise KeelError(f"The Keel server could not be reached: {e.reason}") from None
        except (TimeoutError, ssl.SSLError, ConnectionError) as e:
            raise KeelError(f"The Keel server could not be reached: {type(e).__name__}") from None

    @staticmethod
    def _error(e: urllib.error.HTTPError) -> KeelError:
        title, code = e.reason or "", None
        try:
            problem = json.loads(e.read().decode("utf-8"))
            title = problem.get("title") or title
            code = problem.get("code")
            if isinstance(problem.get("errors"), dict):
                title = " ".join(m for v in problem["errors"].values() for m in v)
        except (ValueError, AttributeError, TypeError):
            pass
        if e.code in (301, 302, 303, 307, 308):
            title = "The server answered with a redirect, which is refused (the token would leave the Keel host)"
        return KeelError(f"{e.code} {title}", e.code, code)

    def _json(self, method: str, path: str, body: Any = None) -> Any:
        data, _ = self._request(method, path, body=body)
        return json.loads(data) if data.strip() else {}

    # ------------------------------------------------------------------ packages

    def checkout(self, top: str, roles: list[str] | None = None, allow_partial: bool = False) -> dict:
        return self._json("POST", f"{CAD}/packages/checkout", {"top": top, "roles": roles, "includeDependents": True, "allowPartial": allow_partial})

    def open_checkouts(self) -> list[dict]:
        r = self._json("GET", f"{CAD}/checkouts")
        return r if isinstance(r, list) else r.get("items", [])

    def download_member_file(self, checkout_id: str, number: str, sha256: str) -> bytes:
        data, _ = self._request("GET", f"{CAD}/checkouts/{checkout_id}/members/{urllib.parse.quote(number, safe='')}/files/{sha256}")
        return data

    def upload_file(self, checkout_id: str, number: str, role: str, file_name: str, path: Path | str, sha256: str) -> dict:
        qs = urllib.parse.urlencode({"fileName": file_name, "role": role})
        with open(path, "rb") as f:
            body = f.read()  # CAD files here are bounded by the server limit; one buffer keeps the retry logic out of the add-on
        data, _ = self._request("PUT", f"{CAD}/checkouts/{checkout_id}/members/{urllib.parse.quote(number, safe='')}/files?{qs}", raw=body,
                                headers={"Content-Type": "application/octet-stream", "X-Content-SHA256": sha256})
        return json.loads(data) if data.strip() else {}

    def add_member(self, checkout_id: str, name: str, document_type: str, cad_role: str, references: list[str] | None = None, description: str | None = None) -> dict:
        return self._json("POST", f"{CAD}/checkouts/{checkout_id}/members",
                          {"name": name, "documentType": document_type, "cadRole": cad_role, "description": description, "references": references or []})

    def checkin(self, checkout_id: str, comment: str | None, skip_unchanged: bool, members: list[dict]) -> dict:
        return self._json("POST", f"{CAD}/checkouts/{checkout_id}/checkin", {"comment": comment, "skipUnchanged": skip_unchanged, "members": members})

    def undo(self, checkout_id: str) -> dict:
        return self._json("POST", f"{CAD}/checkouts/{checkout_id}/undo")

    # ------------------------------------------------------------------ graph, associations

    def package_view(self, number: str, levels: int | None = None) -> dict:
        q = f"?levels={levels}" if levels else ""
        return self._json("GET", f"{CAD}/documents/{urllib.parse.quote(number, safe='')}/package{q}")

    def stale(self, limit: int = 200) -> dict:
        return self._json("GET", f"{CAD}/stale?limit={limit}")

    def register_role(self, number: str, role: str, cad_system: str | None = None) -> dict:
        return self._json("PUT", f"{CAD}/documents/{urllib.parse.quote(number, safe='')}/role", {"role": role, "cadSystem": cad_system})

    def add_association(self, document_number: str, part_number: str, kind: str) -> dict:
        return self._json("POST", f"{CAD}/associations", {"documentNumber": document_number, "partNumber": part_number, "kind": kind})

    def part_documents(self, part_number: str) -> Any:
        return self._json("GET", f"{CAD}/parts/{urllib.parse.quote(part_number, safe='')}/documents")

    def document(self, number: str) -> dict:
        """The ordinary document resource (``/api/v1/documents/{number}``): markings and who holds the check-out."""
        return self._json("GET", f"/api/v1/documents/{urllib.parse.quote(number, safe='')}")

    # ------------------------------------------------------------------ structure proposals

    def create_proposal(self, structure: dict, title: str, markings: dict, root_number: str | None = None, apply_removals: bool = False) -> dict:
        metadata = {"title": title, "format": "json", "markings": markings, "rootNumber": root_number, "applyRemovals": apply_removals}
        boundary = "keel" + uuid.uuid4().hex
        buf = io.BytesIO()

        def part(name: str, content: bytes, ctype: str, filename: str | None = None) -> None:
            disp = f'form-data; name="{name}"' + (f'; filename="{filename}"' if filename else "")
            buf.write(f"--{boundary}\r\nContent-Disposition: {disp}\r\nContent-Type: {ctype}\r\n\r\n".encode())
            buf.write(content)
            buf.write(b"\r\n")

        part("metadata", json.dumps(metadata).encode("utf-8"), "application/json")  # metadata first, as the API requires
        part("file", json.dumps(structure).encode("utf-8"), "application/json", "structure.json")
        buf.write(f"--{boundary}--\r\n".encode())
        data, _ = self._request("POST", f"{CAD}/bom-proposals", raw=buf.getvalue(), headers={"Content-Type": f"multipart/form-data; boundary={boundary}"})
        return json.loads(data)

    def schema(self) -> dict:
        return self._json("GET", f"{CAD}/schemas/structure-v1")
