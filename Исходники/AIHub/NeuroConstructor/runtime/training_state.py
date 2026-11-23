"""Durable local training jobs. Only a worker holding the project lock writes state."""
import contextlib
import errno
import hashlib
import json
import os
import tempfile
import time
from pathlib import Path


def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def write(path, emit):
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=path.parent, prefix=path.name + ".", delete=False) as stream:
        temporary = Path(stream.name)
        try:
            emit(stream)
            stream.flush()
            os.fsync(stream.fileno())
        except BaseException:
            stream.close()
            temporary.unlink(missing_ok=True)
            raise
    try:
        for attempt in range(6):
            try:
                os.replace(temporary, path)
                break
            except PermissionError:
                if attempt == 5:
                    raise
                time.sleep(0.025 * (attempt + 1))
    finally:
        temporary.unlink(missing_ok=True)


def write_json(path, obj):
    data = json.dumps(obj, ensure_ascii=False, allow_nan=False, indent=2).encode("utf-8")
    write(path, lambda stream: stream.write(data))


class WorkerBusy(Exception):
    pass


@contextlib.contextmanager
def exclusive(folder):
    with (folder / "worker.lock").open("a+b") as stream:
        stream.seek(0, 2)
        if not stream.tell():
            stream.write(b"0")
            stream.flush()
        stream.seek(0)
        try:
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError as error:
            if error.errno not in (errno.EACCES,errno.EAGAIN,errno.EDEADLK):
                raise
            raise WorkerBusy from error
        yield


class StopRequested(Exception):
    def __init__(self, status):
        super().__init__(status)
        self.status = status


class Job:
    def __init__(self, folder, run):
        self.folder = folder
        self.run = run
        self.started = time.monotonic()
        self.offset = 0.0
        self.step = self.total = 0
        self.last = None
        self.stage = "Подготовка"
        self.remaining = None
        self.estimated_training = None
        self.training_seconds = 0.
        self.training_wall = 0.
        self.memory_guard = None
        self.last_update = 0.

    @property
    def elapsed(self):
        return self.offset + time.monotonic() - self.started

    def status(self, state="running", message=""):
        write_json(self.folder / "status.json", dict(runId=self.run.get("runId"), workerPid=os.getpid(),
                   status=state, stage=self.stage, step=self.step, total=self.total,
                   elapsedSeconds=round(self.elapsed, 3), remainingSeconds=self.remaining,
                   estimatedTrainingSeconds=self.estimated_training, message=message))

    def check(self):
        for path, state in (("cancel.request", "canceled"), ("pause.request", "paused")):
            if (self.folder / path).exists():
                raise StopRequested(state)
        if self.memory_guard:
            self.memory_guard()

    def stage_begin(self, label):
        self.check()
        if label != self.stage:
            self.remaining = None
        self.stage = label
        self.status()

    def progress(self, label, done, total, started, newly_measured):
        now = time.monotonic()
        if now-self.last_update < 0.5 and done != total:
            return
        self.last_update = now
        self.stage = f"{label} · {done} из {total}"
        self.remaining = round((now-started)/newly_measured*(total-done),1) if newly_measured>=2 else None
        self.status()

    def restore(self, signature):
        import torch
        index = self.folder / "checkpoints.json"
        if not index.is_file():
            if self.step:
                raise ValueError("Не найден указатель на сохранённый шаг. Начните отдельный прогон.")
            return None
        records = read_json(index)
        if records["signature"] != signature:
            raise ValueError("Настройки, обучающие данные или исходные веса изменились. Начните новый прогон.")
        for item in records["copies"]:
            file = self.folder / ("checkpoint-%d.pt" % int(item["slot"]))
            if file.is_file() and digest(file) == item["sha256"]:
                data = torch.load(file, map_location="cpu", weights_only=True)
                self.last = item
                self.step = data["globalStep"]
                self.offset = data["elapsed"]
                self.estimated_training = data.get("estimatedTrainingSeconds")
                self.training_seconds = data.get("trainingComputeSeconds",0.)
                self.training_wall = data.get("trainingWallSeconds",0.)
                return data
        raise ValueError("Обе копии сохранённого прогресса повреждены.")

    def save(self, signature, data):
        import torch
        slot = 1 - self.last["slot"] if self.last else 0
        path = self.folder / ("checkpoint-%d.pt" % slot)
        data.update(globalStep=self.step, elapsed=self.elapsed, estimatedTrainingSeconds=self.estimated_training,
                    trainingComputeSeconds=self.training_seconds, trainingWallSeconds=self.training_wall)
        write(path, lambda stream: torch.save(data, stream))
        item = dict(slot=slot, sha256=digest(path))
        copies = [item] + ([self.last] if self.last else [])
        write_json(self.folder / "checkpoints.json", dict(signature=signature, copies=copies))
        self.last = item
