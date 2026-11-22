"""Exercise Android Keystore, recovery, and calendar with synthetic data only."""
import json
import re
import subprocess
import sys
import threading
import time
import uuid
import xml.etree.ElementTree as ET
from datetime import date, timedelta
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PACKAGE = "org.stalsky.vapemeter.smoketest"
TOKEN = "android.vapemeterSyntheticSmokeTestKey00000000"
PORT = 18889
UI_DUMP = "/data/local/tmp/vapemeter-smoke.xml"


def adb(*arguments):
    return subprocess.check_output(["adb", *arguments], text=True).strip()


def tree():
    adb("shell", "uiautomator", "dump", UI_DUMP)
    output = adb("shell", "cat", UI_DUMP)
    return list(ET.fromstring(output).iter("node"))


def node(nodes, identifier):
    return next((item for item in nodes if item.get("resource-id", "").endswith(identifier)), None)


def wait_for(identifier, check=lambda item: True):
    deadline = time.monotonic() + 55
    while time.monotonic() < deadline:
        nodes = tree()
        item = node(nodes, identifier)
        if item is not None and check(item):
            return item
    available = [{key: item.get(key) for key in ("resource-id", "content-desc", "class", "package", "bounds")}
                 for item in tree()[:24]]
    raise AssertionError(f"{identifier} unavailable. Visible nodes: {available!r}")


def tap(item):
    left, top, right, bottom = map(int, re.findall(r"\d+", item.attrib["bounds"]))
    adb("shell", "input", "tap", str((left + right) // 2), str((top + bottom) // 2))


def day_id(day):
    return f"VapeMeter.Day.{day}"


def is_marked(item):
    return "есть замена" in item.get("text", "") + item.get("content-desc", "")


def choose_day(day, action):
    tap(wait_for(day_id(day)))
    item = wait_for("VapeMeter.MobileDateAction")
    assert action in item.get("text", "") + item.get("content-desc", ""), item.attrib
    tap(item)


class SyntheticServer:
    def __init__(self, original_day):
        self.lock = threading.Lock()
        self.dataset = str(uuid.uuid4())
        self.id = str(uuid.uuid4())
        self.records = {self.id: self.record(self.id, original_day, 1)}
        self.revision = 1
        self.online = True

    @staticmethod
    def record(record_id, day, version, deleted=False):
        return dict(id=record_id, date=str(day), version=version, deleted=deleted)

    def change_remote_date(self, day):
        with self.lock:
            self.revision += 1
            self.records[self.id] = self.record(self.id, day, self.revision)
            self.online = True

    def get(self):
        return dict(datasetId=self.dataset, revision=self.revision, records=list(self.records.values()))

    def apply(self, change):
        record_id = change["recordId"]
        current = self.records.get(record_id)
        reply = dict(datasetId=self.dataset, revision=self.revision)
        if change["expectedVersion"] != (current["version"] if current else 0):
            return 409, dict(reply, outcome="version_conflict", record=current)
        self.revision += 1
        deleting = change["action"] == "delete"
        day = current["date"] if deleting else change["date"]
        saved = self.record(record_id, day, self.revision, deleting)
        self.records[record_id] = saved
        return 200, dict(reply, revision=self.revision, outcome="applied", record=saved)


def handler_for(state):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def dispatch(self, path, request):
            if self.path != path:
                self.send_error(404)
                return
            if self.headers.get("Authorization") != "Bearer " + TOKEN:
                self.send_error(401)
                return
            with state.lock:
                if not state.online:
                    self.send_error(503)
                    return
                status, value = request()
            body = json.dumps(value).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            self.dispatch("/v1/state", lambda: (200, state.get()))

        def do_POST(self):
            self.dispatch("/v1/operations", lambda: state.apply(json.loads(self.read_body())))

        def read_body(self):
            if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
                chunks = bytearray()
                while size := int(self.rfile.readline().split(b";", 1)[0], 16):
                    chunks.extend(self.rfile.read(size))
                    if self.rfile.read(2) != b"\r\n":
                        raise ValueError("Invalid chunk separator")
                return chunks
            return self.rfile.read(int(self.headers["Content-Length"]))

    return Handler


def exercise(apk):
    today = date.fromisoformat(adb("shell", "date", "+%F"))
    original = today - timedelta(days=1)
    changed = today - timedelta(days=3)
    state = SyntheticServer(original)
    adb("install", apk)
    activity = adb("shell", "cmd", "package", "resolve-activity", "--brief", PACKAGE).splitlines()[-1]
    assert activity.startswith(PACKAGE + "/")
    with ThreadingHTTPServer(("127.0.0.1", PORT), handler_for(state)) as server:
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        adb("reverse", f"tcp:{PORT}", f"tcp:{PORT}")
        try:
            adb("logcat", "-b", "crash", "-c")
            output = adb("shell", "am", "start", "-W", "-n", activity)
            print(output, flush=True)
            tap(wait_for("VapeMeter.AndroidKey"))
            adb("shell", "input", "text", TOKEN)
            adb("shell", "input", "keyevent", "KEYCODE_BACK")
            tap(wait_for("VapeMeter.ConnectAndroid"))
            wait_for(day_id(original), is_marked)

            with state.lock:
                state.online = False
            adb("shell", "am", "force-stop", PACKAGE)
            adb("shell", "am", "start", "-W", "-n", activity)
            wait_for(day_id(original), is_marked)
            wait_for("VapeMeter.SyncUnavailable")
            assert node(tree(), "VapeMeter.AndroidKey") is None
            assert node(tree(), "VapeMeter.PageTitle") is None
            calendar_bounds = wait_for("VapeMeter.PreviousPeriod").attrib["bounds"]
            print("Calendar navigation button bounds:", calendar_bounds, flush=True)
            window_insets = adb("shell", "dumpsys", "window", "displays")
            bars = [line.strip() for line in window_insets.splitlines()
                    if "frame=" in line and ("type=statusBars" in line or "type=navigationBars" in line)]
            print("Reported system bar insets:", bars[:4], flush=True)
            status_bottoms = [int(value) for line in bars if "type=statusBars" in line
                              for value in re.findall(r"frame=\[\d+,\d+\]\[\d+,(\d+)\]", line)]
            if status_bottoms:
                calendar_top = int(re.findall(r"\d+", calendar_bounds)[1])
                density = int(re.findall(r"\d+", adb("shell", "wm", "density"))[-1])
                assert 0 <= calendar_top - max(status_bottoms) <= 58 * density / 160
            print("Saved key opened the cached calendar after a cold restart without network.", flush=True)

            choose_day(today, "Добавить замену")
            wait_for(day_id(today), is_marked)
            choose_day(original, "Убрать замену")
            wait_for(day_id(original), lambda item: not is_marked(item))
            adb("shell", "am", "force-stop", PACKAGE)
            adb("shell", "am", "start", "-W", "-n", activity)
            wait_for(day_id(today), is_marked)
            wait_for(day_id(original), lambda item: not is_marked(item))

            state.change_remote_date(changed)
            adb("shell", "input", "keyevent", "KEYCODE_HOME")
            adb("shell", "am", "start", "-W", "-n", activity)
            tap(wait_for("VapeMeter.SyncKeepServer"))
            wait_for(day_id(changed), is_marked)
            wait_for(day_id(today), is_marked)
            with state.lock:
                assert any(item["date"] == str(today) and not item["deleted"] for item in state.records.values())
            print("Calendar actions survived offline restart, synced on reconnect and showed the version choice.", flush=True)
        except Exception:
            top = adb("shell", "dumpsys", "activity", "activities")
            print("\n".join(line for line in top.splitlines() if "topResumedActivity" in line), flush=True)
            print(adb("logcat", "-d", "-b", "crash"), flush=True)
            raise
        finally:
            adb("shell", "am", "force-stop", PACKAGE)
            adb("uninstall", PACKAGE)
            adb("shell", "rm", "-f", UI_DUMP)
            adb("reverse", "--remove", f"tcp:{PORT}")
            server.shutdown()
            worker.join()


if __name__ == "__main__":
    exercise(sys.argv[1])
