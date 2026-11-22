import contextlib
import hashlib
import json
import os
from pathlib import Path
import socket
import sqlite3
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid
from concurrent.futures import ThreadPoolExecutor


def request(base, method, path, token=None, data=None, headers=None):
    headers = dict(headers or {})
    if token:
        headers["Authorization"] = "Bearer " + token
    if data is not None:
        headers.setdefault("Content-Type", "application/json")
        data = json.dumps(data).encode()
    req = urllib.request.Request(base + path, data=data, headers=headers, method=method)
    try:
        response = urllib.request.urlopen(req, timeout=8)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        raw = response.read()
        return response.status, json.loads(raw) if raw else None, response.headers


@contextlib.contextmanager
def run_server(command, env, base):
    child = subprocess.Popen(command, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        for _ in range(80):
            if child.poll() is not None:
                raise RuntimeError(f"Backend stopped during startup: {child.returncode}")
            try:
                if request(base, "GET", "/healthz")[0] == 200:
                    break
            except urllib.error.URLError:
                time.sleep(0.15)
        else:
            raise TimeoutError("Backend did not start")
        yield
    finally:
        if child.poll() is None:
            child.terminate()
            try:
                child.wait(timeout=6)
            except subprocess.TimeoutExpired:
                child.kill()
                child.wait(timeout=6)


def operation(dataset, record_id, *, date=None, version=0, action="upsert"):
    return {
        "datasetId": dataset, "operationId": str(uuid.uuid4()), "recordId": record_id,
        "expectedVersion": version, "action": action, "date": date,
    }


def verify(command):
    with tempfile.TemporaryDirectory(prefix="vapemeter-smoke-") as directory:
        root = Path(directory)
        with socket.socket() as port_finder:
            port_finder.bind(("127.0.0.1", 0))
            port = port_finder.getsockname()[1]
        base = f"http://127.0.0.1:{port}"
        tokens = {device: f"{device}.{uuid.uuid4().hex}" for device in ("windows", "android")}
        hashes = {device: hashlib.sha256(token.encode()).hexdigest() for device, token in tokens.items()}
        credentials = root / "hashes.json"
        credentials.write_text(json.dumps(hashes), encoding="utf-8")
        env = {
            **os.environ, "VAPEMETER_DATA_DIR": str(root / "data"), "VAPEMETER_PORT": str(port),
            "VAPEMETER_DEVICE_CREDENTIALS": str(credentials),
        }
        windows = tokens["windows"]
        android = tokens["android"]
        backup = root / "backup.sqlite"
        with run_server(command, env, base):
            assert request(base, "GET", "/v1/state")[0] == 401
            assert request(base, "GET", "/v1/state", "windows.invalid")[0] == 401
            status, state, headers = request(base, "GET", "/v1/state", windows)
            assert status == 200 and state["revision"] == 0 and not state["records"]
            assert headers["Cache-Control"] == "no-store"
            etag = headers["ETag"]
            assert request(base, "GET", "/v1/state", android, headers={"If-None-Match": etag})[0] == 304
            dataset = state["datasetId"]

            record_id = str(uuid.uuid4())
            create = operation(dataset, record_id, date="2026-10-31")
            result = request(base, "POST", "/v1/operations", windows, create)
            assert result[0] == 200 and result[1]["record"]["version"] == 1, result[:2]
            assert request(base, "POST", "/v1/operations", windows, create)[:2] == result[:2]
            assert request(base, "GET", "/v1/state", android, headers={"If-None-Match": etag})[0] == 200
            altered = {**create, "date": "2026-11-01"}
            assert request(base, "POST", "/v1/operations", windows, altered)[1]["outcome"] == "operation_id_reused"
            assert request(base, "POST", "/v1/operations", android, create)[1]["outcome"] == "operation_id_reused"

            duplicate = operation(dataset, str(uuid.uuid4()), date="2026-10-31")
            code, conflict, _ = request(base, "POST", "/v1/operations", android, duplicate)
            assert code == 409 and conflict["outcome"] == "duplicate_date" and conflict["existing"]["id"] == record_id

            edit = operation(dataset, record_id, date="2026-11-01", version=1)
            assert request(base, "POST", "/v1/operations", android, edit)[1]["record"]["version"] == 2
            stale_delete = operation(dataset, record_id, version=1, action="delete")
            assert request(base, "POST", "/v1/operations", windows, stale_delete)[1]["outcome"] == "version_conflict"
            delete = operation(dataset, record_id, version=2, action="delete")
            assert request(base, "POST", "/v1/operations", windows, delete)[1]["record"]["deleted"]
            assert request(base, "POST", "/v1/operations", windows, create)[:2] == result[:2]
            assert request(base, "GET", "/v1/state", android)[1]["records"][0]["deleted"]

            simultaneous = [operation(dataset, str(uuid.uuid4()), date="2026-11-02") for _ in range(2)]
            with ThreadPoolExecutor(max_workers=2) as pool:
                result_codes = list(pool.map(
                    lambda pair: request(base, "POST", "/v1/operations", *pair)[0],
                    zip((windows, android), simultaneous),
                ))
            assert sorted(result_codes) == [200, 409], result_codes
            subprocess.run([*command, "--backup", str(backup)], env=env, check=True, timeout=15)
            with contextlib.closing(sqlite3.connect(backup)) as snapshot:
                assert snapshot.execute("PRAGMA quick_check").fetchone() == ("ok",)
                assert snapshot.execute("SELECT COUNT(*) FROM replacement WHERE deleted = 0").fetchone() == (1,)

        with run_server(command, env, base):
            assert request(base, "GET", "/v1/state", android)[1]["revision"] == 4
            assert request(base, "POST", "/v1/operations", android, duplicate)[1]["outcome"] == "duplicate_date"
        subprocess.run([*command, "--renew-dataset"], env=env, check=True, timeout=15, stdout=subprocess.DEVNULL)
        with run_server(command, env, base):
            current = request(base, "GET", "/v1/state", android)[1]
            assert current["datasetId"] != dataset and current["revision"] == 4
            assert request(base, "POST", "/v1/operations", windows, delete)[1]["outcome"] == "dataset_changed"
    print("PASS authentication, cache, idempotency, collisions, concurrent writes, tombstones, backup and restart")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise SystemExit("Pass the application command, e.g. python smoke.py dotnet VapeMeter.Backend.dll")
    verify(sys.argv[1:])
