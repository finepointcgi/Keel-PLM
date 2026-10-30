import hashlib
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "tests"))

from fake_server import TOKEN, Doc, FakeKeel  # noqa: E402

from keel_plm.client import KeelClient  # noqa: E402

REPO = ROOT.parents[1]
FIXTURES = REPO / "tests" / "Keel.UnitTests" / "Fixtures" / "cad"
SCHEMA = REPO / "docs" / "integration" / "cad" / "structure-v1.schema.json"


@pytest.fixture()
def keel():
    fake = FakeKeel()
    fake.url = fake.start()
    yield fake
    fake.stop()


@pytest.fixture()
def client(keel):
    return KeelClient(keel.url, lambda: TOKEN)


@pytest.fixture()
def package(keel):
    """A small package in the fake server: top assembly model, a part model, a drawing that references the top. Files are the REAL STEP fixtures when present."""

    def read(name: str, fallback: bytes) -> bytes:
        f = FIXTURES / name
        return f.read_bytes() if f.exists() else fallback

    keel.add_doc(Doc("D-000002", "model", {"bracket.step": read("bracket.step", b"bracket")}))
    keel.add_doc(Doc("D-000001", "model", {"frame.step": read("frame-asm-ap242.step", b"frame")}, refs=["D-000002"]))
    keel.add_doc(Doc("D-000003", "drawing", {"frame.pdf": b"%PDF-1.4 drawing"}, refs=["D-000001"]))
    return keel


def sha(b: bytes) -> str:
    return hashlib.sha256(b).hexdigest()
