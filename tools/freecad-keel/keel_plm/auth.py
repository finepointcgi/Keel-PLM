"""Where the bearer token comes from. The add-on reuses the token mechanism of the ``keel-cad`` CLI (docs/integration/cad-connector.md):
a JWT of the site's identity provider in ``KEEL_CAD_TOKEN``, or in a token file. Keel never sees a password and neither does this add-on.

Order: the environment variable, the token file of the settings, the token command of the settings (a program that prints a token, run without a
shell; use it to wrap whatever your site uses to sign in). The token is read again for every request, so a renewed file is picked up without a restart.
"""
from __future__ import annotations

import base64
import json
import os
import re
import shlex
import stat
import subprocess
import time
from pathlib import Path

from .settings import Settings

ENV_VAR = "KEEL_CAD_TOKEN"
_JWT = re.compile(r"^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*$")


class TokenError(RuntimeError):
    """The token could not be found or is unusable. The message never contains the token."""


def _check(token: str, source: str) -> str:
    token = token.strip()
    if not token:
        raise TokenError(f"The token from {source} is empty.")
    if not _JWT.match(token):
        raise TokenError(f"The token from {source} is not a JWT bearer token.")
    return token


def jwt_expiry(token: str) -> float | None:
    """The ``exp`` claim of a JWT (seconds since the epoch), read WITHOUT verifying it (the server verifies). None when absent or unreadable."""
    try:
        payload = token.split(".")[1]
        payload += "=" * (-len(payload) % 4)
        exp = json.loads(base64.urlsafe_b64decode(payload)).get("exp")
        return float(exp) if exp is not None else None
    except (IndexError, ValueError, TypeError, json.JSONDecodeError):
        return None


def read_token(settings: Settings, env: dict[str, str] | None = None) -> str:
    env = os.environ if env is None else env
    if env.get(ENV_VAR):
        return _check(env[ENV_VAR], ENV_VAR)
    if settings.token_file:
        path = Path(settings.token_file).expanduser()
        try:
            st = path.stat()
        except OSError as e:
            raise TokenError(f"The token file {path} cannot be read: {e.strerror}.") from e
        if os.name == "posix" and st.st_mode & (stat.S_IRWXG | stat.S_IRWXO):
            raise TokenError(f"The token file {path} can be read by other users; run: chmod 600 '{path}'")
        return _check(path.read_text(encoding="utf-8"), f"token file {path}")
    if settings.token_command:
        try:
            result = subprocess.run(shlex.split(settings.token_command), capture_output=True, text=True, timeout=60, check=False)  # noqa: S603 - no shell
        except (OSError, subprocess.TimeoutExpired, ValueError) as e:
            raise TokenError(f"The token command failed: {type(e).__name__}.") from e
        if result.returncode != 0:
            raise TokenError(f"The token command exited with status {result.returncode}.")
        return _check(result.stdout, "the token command")
    raise TokenError(f"No token: set {ENV_VAR}, or a token file or token command in the Keel settings.")


def expired(token: str, now: float | None = None, skew: float = 30.0) -> bool:
    exp = jwt_expiry(token)
    return exp is not None and (now if now is not None else time.time()) + skew >= exp
