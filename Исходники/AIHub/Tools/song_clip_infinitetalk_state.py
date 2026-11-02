"""Input-bound, atomic checkpoints around the author's InfiniteTalk windows.

JSON receipts are the commit point. Interrupted tensor/PNG writes are never
accepted, and resuming verifies every committed frame before loading a model.
This module deliberately has no torch/model dependency.
"""
import os
from pathlib import Path
import re
import secrets

from song_clip_motion_state import (atomic_json, contained, file_hash,
                                    object_hash, read_json, reject_links)

FORMAT = 'LOPATA.SongClip.InfiniteTalk/1'
SOURCE_REVISION = '088128b224242e110d3906c6750e9a3a348a659b'
LOOP_SHA256 = '6627148f7bb04818ba30dc004e7c9624b20bb9d7bd022e89acd4f2b8fe494872'
MAX_TENSOR_BYTES = 256 * 1024 * 1024


def windows(total_frames, frame_window=81, motion_frames=9):
    if (type(total_frames) is not int or not 1 <= total_frames <= 180000
            or frame_window != 81 or motion_frames != 9):
        raise ValueError('Unsupported InfiniteTalk frame grid.')
    advance = frame_window - motion_frames
    count = 1 + max(0, (total_frames - frame_window + advance - 1) // advance)
    return [dict(index=i + 1, startFrame=i * advance, rawFrames=frame_window,
                 contextFrames=motion_frames if i else 0) for i in range(count)]


class InfiniteState:
    def __init__(self, job, identity, requested_seed, total_frames):
        if type(requested_seed) is not int or not -1 <= requested_seed < 2**53:
            raise ValueError('Invalid InfiniteTalk seed.')
        self.root = Path(job).absolute()
        reject_links(self.root)
        self.root.mkdir(parents=True, exist_ok=True)
        self.frames = contained(self.root, 'infinite-frames')
        self.receipts = contained(self.root, 'infinite-checkpoints')
        self.frames.mkdir(exist_ok=True)
        self.receipts.mkdir(exist_ok=True)
        self.plan = windows(total_frames)
        self.total_frames = total_frames
        self.request_hash = object_hash(identity)
        path = contained(self.root, 'infinite-state.json')
        if path.exists():
            binding = read_json(path)
            if (binding.get('format') != FORMAT or binding.get('identity') != identity
                    or binding.get('totalFrames') != total_frames
                    or binding.get('requestSha256') != self.request_hash
                    or type(binding.get('seed')) is not int
                    or not 0 <= binding['seed'] < 2**53):
                raise ValueError('InfiniteTalk inputs or checkpoint binding changed.')
            self.seed = binding['seed']
            if requested_seed >= 0 and self.seed != requested_seed:
                raise ValueError('InfiniteTalk checkpoint seed changed.')
        else:
            self.seed = secrets.randbits(53) if requested_seed == -1 else requested_seed
            atomic_json(path, dict(format=FORMAT, identity=identity, totalFrames=total_frames,
                                   requestSha256=self.request_hash, seed=self.seed))
        self.chain = self.validate_prefix()

    @property
    def latest(self):
        return self.chain[-1] if self.chain else None

    @property
    def done(self):
        return bool(self.latest and self.latest['final'])

    @property
    def frame_count(self):
        return self.latest['frameCount'] if self.latest else 0

    def validate_prefix(self):
        files = sorted(self.receipts.glob('*.json'))
        chain = []
        previous = None
        start = 0
        for index, path in enumerate(files, 1):
            reject_links(path)
            if path.name != f'{index:06d}.json' or index > len(self.plan):
                raise ValueError('InfiniteTalk checkpoint gap or unexpected receipt.')
            record = read_json(path)
            expected_count = 81 + (index - 1) * 72
            counters = record.get('counters', {})
            final = index == len(self.plan)
            if (record.get('format') != FORMAT or record.get('index') != index
                    or record.get('requestSha256') != self.request_hash
                    or record.get('previous') != previous
                    or record.get('frameCount') != expected_count or record.get('final') is not final
                    or counters.get('iteration_count') != index
                    or counters.get('img_counter') != expected_count
                    or counters.get('audio_start_idx') != (index - 1 if final else index) * 72
                    or counters.get('audio_end_idx') != counters.get('audio_start_idx', -1) + 81
                    or counters.get('current_condframe_index') != index
                    or type(counters.get('step_iteration_count')) is not int
                    or counters['step_iteration_count'] < 1
                    or counters.get('arrive_last_frame') is not (final or index == len(self.plan) - 1)
                    or counters.get('cur_motion_frames_num') != (1 if final and index == 1 else 9)
                    or counters.get('is_first_clip') is not (final and index == 1)):
                raise ValueError('InfiniteTalk checkpoint counters or identity are invalid.')
            tensor = contained(self.receipts, f'{index:06d}.safetensors')
            if (not tensor.is_file() or not 0 < tensor.stat().st_size <= MAX_TENSOR_BYTES
                    or file_hash(tensor) != record.get('tensorSha256')):
                raise ValueError('InfiniteTalk checkpoint tensors changed or are incomplete.')
            frames = record.get('frames')
            if not isinstance(frames, list) or len(frames) != expected_count - start:
                raise ValueError('InfiniteTalk frame receipt length is invalid.')
            for offset, entry in enumerate(frames, start):
                name = f'frame_{offset:05d}.png'
                if not isinstance(entry, dict) or entry.get('file') != name:
                    raise ValueError('InfiniteTalk frame receipt order is invalid.')
                frame = contained(self.frames, name)
                if not frame.is_file() or file_hash(frame) != entry.get('sha256'):
                    raise ValueError('A committed InfiniteTalk frame is missing or changed.')
            previous = object_hash(record)
            chain.append(record)
            start = expected_count
        return chain

    def quarantine_uncommitted(self):
        """Keep interrupted output for diagnosis without mixing it into a resume."""
        orphaned = []
        for path in self.frames.iterdir():
            reject_links(path)
            match = re.fullmatch(r'frame_(\d{5,})\.png', path.name)
            pending = re.fullmatch(r'frame_(\d{5,})\.png\.[0-9a-f]{16}\.tmp', path.name)
            if not (match or pending) or not path.is_file():
                raise ValueError('Unexpected file in the InfiniteTalk frame directory.')
            if pending and int(pending[1]) < self.frame_count:
                raise ValueError('Unexpected pending file for an accepted InfiniteTalk frame.')
            if pending or int(match[1]) >= self.frame_count:
                orphaned.append(path)
        if orphaned:
            target = contained(self.root, 'infinite-uncommitted/' + secrets.token_hex(12))
            target.mkdir(parents=True)
            for path in orphaned:
                os.replace(path, contained(target, path.name))

    def commit(self, counters, final, tensor_writer):
        index = len(self.chain) + 1
        if self.done or index > len(self.plan) or final != (index == len(self.plan)):
            raise ValueError('Unexpected InfiniteTalk window commit.')
        expected_count = 81 + (index - 1) * 72
        if counters['img_counter'] != expected_count:
            raise ValueError('Native InfiniteTalk emitted an unexpected frame count.')
        path = contained(self.receipts, f'{index:06d}.safetensors')
        staging = contained(self.receipts, f'{index:06d}.{secrets.token_hex(8)}.tmp')
        try:
            tensor_writer(staging)
            if not 0 < staging.stat().st_size <= MAX_TENSOR_BYTES:
                raise ValueError('Invalid InfiniteTalk checkpoint tensor size.')
            with staging.open('r+b') as stream:
                os.fsync(stream.fileno())
            os.replace(staging, path)
        finally:
            if staging.exists():
                staging.unlink()
        frames = []
        for offset in range(self.frame_count, expected_count):
            frame = contained(self.frames, f'frame_{offset:05d}.png')
            with frame.open('r+b') as stream:
                os.fsync(stream.fileno())
            frames.append(dict(file=frame.name, sha256=file_hash(frame)))
        receipt = dict(format=FORMAT, index=index, requestSha256=self.request_hash,
                       previous=object_hash(self.latest) if self.latest else None,
                       final=final, frameCount=expected_count, counters=counters,
                       tensorSha256=file_hash(path), frames=frames)
        atomic_json(contained(self.receipts, f'{index:06d}.json'), receipt)
        self.chain.append(receipt)
        return receipt
