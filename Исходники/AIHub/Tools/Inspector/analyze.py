"""Run the pinned IBM TSPulse pipeline on one saved local telemetry series."""

import json
import os
import sys
from pathlib import Path


def main(component, request):
    sys.dont_write_bytecode = True
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
    os.environ["OMP_NUM_THREADS"] = "1"
    os.environ["MKL_NUM_THREADS"] = "1"
    sys.path.insert(0, str(component / "packages"))

    import numpy as np
    import pandas as pd
    import torch
    from tsfm_public.models.tspulse.modeling_tspulse import TSPulseForReconstruction
    from tsfm_public.toolkit.time_series_anomaly_detection_pipeline import TimeSeriesAnomalyDetectionPipeline

    payload = json.loads(request.read_text(encoding="utf-8"))
    values = np.asarray(payload["values"], dtype=np.float64)
    if values.ndim != 1 or not 513 <= len(values) <= 2048 or not np.isfinite(values).all():
        raise ValueError("Expected one finite and sufficiently long telemetry series.")

    torch.set_num_threads(1)
    torch.set_num_interop_threads(1)
    torch.manual_seed(424242)
    model = TSPulseForReconstruction.from_pretrained(str(component / "model"), local_files_only=True).eval()
    pipeline = TimeSeriesAnomalyDetectionPipeline(model, device="cpu", batch_size=8, smoothing_length=1)
    timestamps = pd.date_range(payload["startUtc"], periods=len(values), freq=f'{payload["intervalSeconds"]}s')
    telemetry = pd.DataFrame({"utc": timestamps, "value": values})
    frame = pipeline(telemetry, timestamp_column="utc", target_columns=["value"])
    scores = frame["anomaly_score"].to_numpy() if "anomaly_score" in frame else frame["value_anomaly_score"].to_numpy()
    context = model.config.context_length
    candidates = np.flatnonzero(np.isfinite(scores[context:])) + context
    if not len(candidates):
        raise ValueError("The pipeline produced no usable scores.")
    strongest = candidates[np.argmax(scores[candidates])]
    print(json.dumps({"sampleCount": len(values), "peakUtc": timestamps[strongest].isoformat()}))


if __name__ == "__main__":
    main(Path(sys.argv[1]).resolve(strict=True), Path(sys.argv[2]).resolve(strict=True))
