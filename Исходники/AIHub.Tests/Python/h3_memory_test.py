"""Memory-manager startup regression, without loading Torch or model weights."""
import importlib.util
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import Mock, patch

spec = importlib.util.spec_from_file_location("h3_memory", Path(__file__).parents[2] / "AIHub" / "Tools" / "h3_memory.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class MemoryStartup(unittest.TestCase):
    def setUp(self):
        self.cli = types.SimpleNamespace(args=types.SimpleNamespace(disable_dynamic_vram=False, disable_nvml_pressure=False, vram_headroom=0), enables_dynamic_vram=lambda: True)
        self.mm = types.SimpleNamespace(is_nvidia=lambda: True, is_amd=lambda: False,
            rocm_version=(0, 0), torch_version_numeric=(2, 10),
            get_all_torch_devices=lambda: [types.SimpleNamespace(index=2)])
        self.memory = types.SimpleNamespace(aimdo_enabled=False)
        self.patcher = types.SimpleNamespace(CoreModelPatcher="classic", ModelPatcherDynamic="dynamic")
        self.control = types.SimpleNamespace(init=Mock(return_value=True), init_devices=Mock(return_value=True), set_log_info=Mock())
        comfy = types.ModuleType("comfy")
        modules = {"comfy": comfy, "comfy_aimdo": types.ModuleType("comfy_aimdo"), "comfy_aimdo.control": self.control}
        modules["comfy_aimdo"].control = self.control
        for name, obj in {"cli_args":self.cli, "model_management":self.mm,
                          "memory_management":self.memory, "model_patcher":self.patcher}.items():
            setattr(comfy, name, obj); modules["comfy." + name] = obj
        self.scope = patch.dict(sys.modules, modules); self.scope.start(); self.addCleanup(self.scope.stop)
        self.emit = Mock()

    def run_mode(self, mode="auto"):
        return module.initialize_memory({"MemoryMode":mode, "VramReserveGb":2}, self.emit)

    def test_auto_installs_native_dynamic_patcher_before_any_weights(self):
        devices = []
        self.control.init_devices.side_effect = lambda items: devices.extend(items) or True
        self.assertEqual("dynamic", self.run_mode())
        self.assertEqual([(2, 0)], devices)
        self.assertEqual("dynamic", self.patcher.CoreModelPatcher)
        self.assertTrue(self.memory.aimdo_enabled)
        self.assertFalse(self.cli.args.disable_dynamic_vram)

    def test_native_control_library_initializes_with_nvml_pressure_before_torch(self):
        self.assertTrue(module.initialize_host_memory({"MemoryMode":"auto", "VramReserveGb":2}))
        self.control.init.assert_called_once_with(simple_vram_headroom=2 * 1024**3, nvml_pressure=True)
        self.control.init.reset_mock()
        for mode in ("gpu", "offload"):
            self.assertFalse(module.initialize_host_memory({"MemoryMode":mode, "VramReserveGb":2}))
        self.control.init.assert_not_called()

    def test_gpu_and_explicit_offload_keep_the_user_mode(self):
        for mode in ("gpu", "offload"):
            with self.subTest(mode=mode):
                self.assertEqual("classic", self.run_mode(mode))
        self.control.init_devices.assert_not_called()

    def test_unsupported_device_and_old_torch_align_native_budget_flags(self):
        for nvidia, amd, rocm, torch in ((False, False, (0,0), (2,10)),
                (False, True, (7,13), (2,10)), (True, False, (0,0), (2,7))):
            self.mm.is_nvidia=lambda:nvidia; self.mm.is_amd=lambda:amd
            self.mm.rocm_version=rocm; self.mm.torch_version_numeric=torch
            self.assertEqual("classic", self.run_mode())
            self.assertTrue(self.cli.args.disable_dynamic_vram)
        self.control.init_devices.assert_not_called()

    def test_supported_rocm_uses_the_same_native_manager(self):
        self.mm.is_nvidia=lambda:False; self.mm.is_amd=lambda:True; self.mm.rocm_version=(7,14)
        self.assertEqual("dynamic", self.run_mode())

    def test_unavailable_native_manager_reports_classic_and_no_false_dynamic(self):
        self.control.init_devices.return_value=False
        self.assertEqual("classic", self.run_mode())
        self.assertTrue(self.cli.args.disable_dynamic_vram)
        self.assertFalse(self.memory.aimdo_enabled)
        self.emit.assert_called_once_with("memory", loader="classic", reserve_gb=2)

    def test_old_native_protocol_and_unexpected_failures(self):
        self.control.init_devices.side_effect=[TypeError("old protocol"), True]
        self.assertEqual("dynamic", self.run_mode())
        self.control.init_devices.side_effect=RuntimeError("native failure")
        with self.assertRaisesRegex(RuntimeError, "native failure"):
            self.run_mode()

    def test_classic_budget_adds_native_allocator_slack_without_changing_user_settings(self):
        settings = {"MemoryMode":"auto", "VramReserveGb":2}
        self.assertEqual(6, module.sampling_headroom(settings, "classic", 4096))
        self.assertEqual(2, module.sampling_headroom(settings, "dynamic", 4096))
        self.assertEqual(2, settings["VramReserveGb"])
        settings["MemoryMode"] = "gpu"
        self.assertEqual(2, module.sampling_headroom(settings, "classic", 4096))

    def test_refinement_does_not_double_count_native_window_allocator_slack(self):
        class Model:
            def __init__(self):
                self.model_options = {"headroom":{"headroom_gb":6, "count_guides":True}, "other":17}
            def clone(self):
                result=Model(); result.model_options=self.model_options.copy(); return result
        source=Model()
        result=module.context_window_model(source, {"VramReserveGb":2}, "headroom")
        self.assertEqual(6, source.model_options["headroom"]["headroom_gb"])
        self.assertEqual({"headroom_gb":2, "count_guides":True}, result.model_options["headroom"])
        self.assertEqual(17, result.model_options["other"])


if __name__ == "__main__":
    unittest.main()
