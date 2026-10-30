"""Settings of the add-on: a small JSON file. It never holds a password or a token (only the path of a token file or a command that prints one)."""
from __future__ import annotations

import json
import os
import sys
from dataclasses import asdict, dataclass, fields
from pathlib import Path
from urllib.parse import urlsplit

FORBIDDEN_KEYS = ("token", "password", "secret")


class SettingsError(ValueError):
    pass


@dataclass
class Settings:
    server_url: str = ""
    # Where the bearer token comes from (see auth.py). The token itself is never saved here.
    token_file: str = ""
    token_command: str = ""
    # Folder under which a check-out creates one sub-folder per top document.
    work_root: str = ""
    # PEM file of the CA that signed the Keel server certificate (air-gapped sites use a private CA).
    ca_file: str = ""
    # Plain http is refused except to a loopback address unless this is set (never needed in production).
    allow_insecure_http: bool = False
    # Refuse to push a BOM when a part has no KeelPartNumber (otherwise it is proposed as a new part and a warning is shown).
    require_part_numbers: bool = False
    timeout_seconds: int = 60


def default_config_dir(env: dict[str, str] | None = None, platform: str | None = None) -> Path:
    env = os.environ if env is None else env
    platform = platform or sys.platform
    if platform.startswith("win"):
        return Path(env.get("APPDATA", str(Path.home() / "AppData" / "Roaming"))) / "keel"
    if platform == "darwin":
        return Path.home() / "Library" / "Application Support" / "keel"
    return Path(env.get("XDG_CONFIG_HOME", str(Path.home() / ".config"))) / "keel"


def default_settings_path() -> Path:
    return default_config_dir() / "freecad-addon.json"


def validate_server_url(url: str, allow_insecure_http: bool = False) -> str:
    """Returns the URL without a trailing slash, or raises SettingsError. https only, except loopback (or when explicitly allowed)."""
    url = (url or "").strip()
    if not url:
        raise SettingsError("The Keel server URL is not set.")
    parts = urlsplit(url)
    if parts.scheme not in ("http", "https") or not parts.hostname:
        raise SettingsError("The Keel server URL must look like https://keel.example.org")
    if parts.username or parts.password:
        raise SettingsError("Do not put a user name or password in the server URL.")
    if parts.query or parts.fragment:
        raise SettingsError("The Keel server URL must not have a query or fragment.")
    loopback = parts.hostname in ("localhost", "127.0.0.1", "::1")
    if parts.scheme == "http" and not loopback and not allow_insecure_http:
        raise SettingsError("The Keel server URL must use https (plain http is only accepted for localhost).")
    return url.rstrip("/")


def load(path: Path | str | None = None) -> Settings:
    path = Path(path) if path else default_settings_path()
    if not path.exists():
        return Settings()
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as e:
        raise SettingsError(f"The settings file {path} could not be read: {e}") from e
    known = {f.name for f in fields(Settings)}
    if not isinstance(raw, dict):
        raise SettingsError(f"The settings file {path} must hold a JSON object.")
    return Settings(**{k: v for k, v in raw.items() if k in known})


def save(settings: Settings, path: Path | str | None = None) -> Path:
    path = Path(path) if path else default_settings_path()
    data = asdict(settings)
    for key in data:
        if any(word in key.lower() for word in FORBIDDEN_KEYS) and key not in ("token_file", "token_command"):
            raise SettingsError(f"Refusing to save the setting '{key}': credentials are never stored.")
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps(data, indent=2), encoding="utf-8")
    os.replace(tmp, path)
    return path
