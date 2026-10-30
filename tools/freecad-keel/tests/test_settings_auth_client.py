import json
import os
import sys
import time

import pytest
from fake_server import TOKEN, jwt

from keel_plm import auth
from keel_plm.client import KeelClient, KeelError
from keel_plm.settings import Settings, SettingsError, default_config_dir, load, save, validate_server_url

# ------------------------------------------------------------------ settings


def test_https_is_required_except_for_loopback():
    assert validate_server_url("https://keel.example.org/") == "https://keel.example.org"
    assert validate_server_url("http://localhost:5080") == "http://localhost:5080"
    assert validate_server_url("http://127.0.0.1:1") == "http://127.0.0.1:1"
    with pytest.raises(SettingsError, match="https"):
        validate_server_url("http://keel.example.org")
    assert validate_server_url("http://keel.example.org", allow_insecure_http=True)


@pytest.mark.parametrize("url", ["", "ftp://x", "https://user:pw@keel.example.org", "https://keel.example.org/?token=abc", "keel.example.org", "https://"])
def test_bad_urls_are_refused(url):
    with pytest.raises(SettingsError):
        validate_server_url(url)


def test_settings_round_trip_and_hold_no_credentials(tmp_path):
    path = tmp_path / "s.json"
    save(Settings(server_url="https://keel.example.org", token_file="/home/a/.keel/token", work_root="/work"), path)
    again = load(path)
    assert again.server_url == "https://keel.example.org" and again.token_file == "/home/a/.keel/token"
    raw = json.loads(path.read_text())
    assert not [k for k in raw if "password" in k or k in ("token", "secret")]


def test_unknown_settings_keys_are_ignored_and_a_missing_file_gives_defaults(tmp_path):
    (tmp_path / "s.json").write_text(json.dumps({"server_url": "https://k", "password": "hunter2"}))
    s = load(tmp_path / "s.json")
    assert s.server_url == "https://k" and not hasattr(s, "password")
    assert load(tmp_path / "none.json") == Settings()


def test_config_dir_per_platform():
    assert str(default_config_dir({"APPDATA": "C:/Users/a/AppData/Roaming"}, "win32")).replace("\\", "/").endswith("Roaming/keel")
    assert str(default_config_dir({"XDG_CONFIG_HOME": "/x"}, "linux")).replace("\\", "/") == "/x/keel"

# ------------------------------------------------------------------ auth


def test_token_from_environment_wins():
    assert auth.read_token(Settings(token_file="/nonexistent"), {"KEEL_CAD_TOKEN": TOKEN}) == TOKEN


@pytest.mark.skipif(os.name != "posix", reason="POSIX file modes")
def test_token_file_must_not_be_readable_by_others(tmp_path):
    f = tmp_path / "token"
    f.write_text(TOKEN)
    f.chmod(0o644)
    with pytest.raises(auth.TokenError, match="chmod 600") as e:
        auth.read_token(Settings(token_file=str(f)), {})
    assert TOKEN not in str(e.value)
    f.chmod(0o600)
    assert auth.read_token(Settings(token_file=str(f)), {}) == TOKEN


def test_a_value_that_is_not_a_jwt_is_refused_without_echoing_it():
    with pytest.raises(auth.TokenError) as e:
        auth.read_token(Settings(), {"KEEL_CAD_TOKEN": "my-password-is-hunter2"})
    assert "hunter2" not in str(e.value)


def test_no_token_source_says_what_to_set():
    with pytest.raises(auth.TokenError, match="KEEL_CAD_TOKEN"):
        auth.read_token(Settings(), {})


def test_token_command_runs_without_a_shell(tmp_path):
    script = tmp_path / "print_token.py"
    script.write_text(f"print({TOKEN!r})")
    assert auth.read_token(Settings(token_command=f'"{sys.executable}" "{script}"'), {}) == TOKEN
    failing = tmp_path / "fail.py"
    failing.write_text("raise SystemExit(3)")
    with pytest.raises(auth.TokenError, match="status 3"):
        auth.read_token(Settings(token_command=f'"{sys.executable}" "{failing}"'), {})
    # shell metacharacters are plain arguments, never interpreted
    marker = tmp_path / "pwned"
    with pytest.raises(auth.TokenError):
        auth.read_token(Settings(token_command=f"{sys.executable} -c pass ; touch {marker}"), {})
    assert not marker.exists()


def test_expiry_is_read_without_trusting_it():
    assert auth.jwt_expiry(jwt(1234)) == 1234
    assert auth.expired(jwt(int(time.time()) - 10))
    assert not auth.expired(jwt(int(time.time()) + 3600))
    assert auth.jwt_expiry("a.b.c") is None

# ------------------------------------------------------------------ client


def test_the_bearer_token_is_sent_and_a_wrong_one_is_refused(keel):
    assert KeelClient(keel.url, lambda: TOKEN).open_checkouts() == []
    with pytest.raises(KeelError) as e:
        KeelClient(keel.url, "wrong.token.value").open_checkouts()
    assert e.value.status == 401


def test_problem_documents_become_errors_with_the_protocol_code(package, client):
    package.docs["D-000001"].locked_by = "bob"
    with pytest.raises(KeelError) as e:
        client.checkout("D-000001")
    assert (e.value.status, e.value.code) == (409, "cad.locked")
    assert "bob" in str(e.value) and "[cad.locked]" in str(e.value)


def test_a_redirect_is_refused_so_the_token_never_leaves_the_host(client):
    with pytest.raises(KeelError, match="redirect"):
        client._request("GET", "/redirect-me")


def test_an_unreachable_server_is_a_readable_error():
    with pytest.raises(KeelError, match="could not be reached") as e:
        KeelClient("http://127.0.0.1:1", lambda: TOKEN, timeout=2).open_checkouts()
    assert e.value.status == 0


def test_plain_http_to_a_remote_host_is_refused_at_construction():
    with pytest.raises(SettingsError):
        KeelClient("http://keel.example.org", lambda: TOKEN)
