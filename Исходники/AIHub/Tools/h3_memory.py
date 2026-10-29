"""Initialize the pinned ComfyUI memory manager as its native main.py does.

Importing nodes does not perform main.py's DynamicVRAM initialization. Leaving
the CLI in dynamic mode with a legacy patcher also misleads obvpm's budgeting.
No model, sampler, attention kernel or user quality setting is replaced here.
"""
import logging


def initialize_host_memory(settings):
    """Load the native control library before Torch, matching main.py startup."""
    import comfy.cli_args as cli
    if settings["MemoryMode"] == "auto" and cli.enables_dynamic_vram():
        import comfy_aimdo.control as control
        return control.init(simple_vram_headroom=int(settings["VramReserveGb"] * 1024**3),
                            nvml_pressure=not cli.args.disable_nvml_pressure)
    return False


def sampling_headroom(settings, mode, classic_slack_mb):
    # The upstream window budget includes this classic-allocator slack; plain
    # sampling needs it too. Keep the user's desktop reserve separate from it.
    reserve = float(settings["VramReserveGb"])
    if mode == "classic" and settings["MemoryMode"] != "gpu":
        reserve += max(0, float(classic_slack_mb)) / 1024
    return reserve


def context_window_model(model, settings, option_key):
    # H3ContextWindows already budgets the classic allocator slack. Reset only
    # this clone's desktop reserve so refinement does not count that slack twice.
    model = model.clone()
    model.model_options[option_key] = {**model.model_options[option_key],
                                       "headroom_gb": settings["VramReserveGb"]}
    return model


def initialize_memory(settings, emit):
    import comfy.cli_args as cli
    import comfy.model_management as mm
    import comfy.memory_management as memory
    import comfy.model_patcher as patcher

    supported = (mm.is_nvidia() or
                 mm.is_amd() and mm.rocm_version >= (7, 14))
    enabled = False
    if (settings["MemoryMode"] == "auto" and supported and
            mm.torch_version_numeric >= (2, 8) and cli.enables_dynamic_vram()):
        import comfy_aimdo.control as control
        devices = mm.get_all_torch_devices()
        try:
            enabled = control.init_devices((d.index, int(cli.args.vram_headroom * 1024**3)) for d in devices)
        except TypeError:
            # The 0.4.9 protocol is retained by the pinned native entry point.
            enabled = control.init_devices(d.index for d in devices)
        if enabled:
            control.set_log_info()
            patcher.CoreModelPatcher = patcher.ModelPatcherDynamic
            memory.aimdo_enabled = True
    if not enabled:
        # The timeline consults this flag when budgeting activation/allocator
        # slack. It must describe the manager actually running, not the default.
        cli.args.disable_dynamic_vram = True
        if settings["MemoryMode"] == "auto" and supported:
            logging.warning("H3: native DynamicVRAM unavailable; using the native classic loader")
    mode = "dynamic" if enabled else "classic"
    logging.info("H3 memory: %s loader, reserve %.2f GiB", mode, settings["VramReserveGb"])
    emit("memory", loader=mode, reserve_gb=settings["VramReserveGb"])
    return mode
