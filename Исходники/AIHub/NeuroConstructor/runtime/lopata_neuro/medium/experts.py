"""Pinned native encoders. Reduce each message on the device which produced it."""
import json
from pathlib import Path

import torch
from safetensors import safe_open
from torch.nn import functional as F


def message(tensor, size=128):
    tensor = tensor.float().reshape(tensor.shape[0],-1)
    return F.normalize(F.adaptive_avg_pool1d(tensor[:,None],size)[:,0])


def dtype_for(device):
    if device.type == "cuda":
        with torch.cuda.device(device):
            return torch.bfloat16 if torch.cuda.is_bf16_supported() else torch.float16
    return torch.float32


def tensors_for(folder,prefix):
    index = folder/"model.safetensors.index.json"
    shards = ["model.safetensors"]
    if index.exists():
        mapping = json.loads(index.read_text("utf-8"))["weight_map"]
        shards = sorted({v for k,v in mapping.items() if k.startswith(prefix)})
    for name in shards:
        if not isinstance(name,str) or not name.endswith(".safetensors") or Path(name).name!=name or "/" in name or "\\" in name:
            raise ValueError("Недопустимое имя части весов.")
        with safe_open(str(folder/name),framework="pt") as f:
            for k in f.keys():
                if k.startswith(prefix):
                    yield k[len(prefix):],f.get_tensor(k)


def load_subset(module, folder, prefix, rename, device, dtype):
    tensors = {rename(k):v for k,v in tensors_for(folder,prefix)}
    module.load_state_dict(tensors, strict=True, assign=True)
    return module.requires_grad_(False).eval().to(device=device,dtype=dtype)


class Hearing:
    def __init__(self, folder, device):
        from transformers import Qwen3Config, Qwen3ForCausalLM, Qwen2Tokenizer, WhisperFeatureExtractor
        from transformers.models.qwen3_omni_moe.modeling_qwen3_omni_moe import Qwen3OmniMoeAudioEncoder
        from transformers.models.qwen3_omni_moe.configuration_qwen3_omni_moe import Qwen3OmniMoeAudioEncoderConfig
        cfg = json.loads((folder / "config.json").read_text())["thinker_config"]
        self.device = device
        self.dtype = dtype_for(device)
        self.fe = WhisperFeatureExtractor.from_pretrained(folder,local_files_only=True)
        self.tokenizer = Qwen2Tokenizer.from_pretrained(folder,local_files_only=True)
        config = Qwen3OmniMoeAudioEncoderConfig(**cfg["audio_config"])
        # These released ASR checkpoints use the Omni audio tower and a Qwen3 text decoder.
        # Strict loading checks all keys and tensor shapes in both subgraphs.
        self.encoder = load_subset(Qwen3OmniMoeAudioEncoder(config),folder,"thinker.audio_tower.",
                                   str,device,self.dtype)
        text_cfg = dict(cfg["text_config"])
        text_cfg.pop("rope_scaling",None)  # A 1-D audio sequence has equal coordinates on every rotary axis.
        self.decoder = Qwen3ForCausalLM(Qwen3Config(**text_cfg))
        self.decoder.model = load_subset(self.decoder.model,folder,"thinker.model.",str,device,self.dtype)
        head = dict(tensors_for(folder,"thinker.lm_head.")).get("weight")
        if head is not None:
            self.decoder.lm_head.weight = torch.nn.Parameter(head.to(device,self.dtype),requires_grad=False)
        else:
            self.decoder.tie_weights()
        self.decoder.eval().requires_grad_(False)
        self.pad = cfg["audio_token_id"]
        template = json.loads((folder/"chat_template.json").read_text())["chat_template"]
        self.prompt = self.tokenizer.apply_chat_template([{"role":"system","content":""},
                      {"role":"user","content":[{"type":"audio"}]}],chat_template=template,
                      add_generation_prompt=True,tokenize=False)

    @torch.inference_mode()
    def read(self, signal, transcript=False):
        data = self.fe(signal,sampling_rate=16000,return_tensors="pt",padding="longest",return_attention_mask=True)
        frames = data.input_features[0].to(self.device,self.dtype)
        length = frames.shape[-1]
        hidden = self.encoder(frames,feature_lens=torch.tensor([length],device=self.device)).last_hidden_state
        pooled = F.adaptive_avg_pool1d(hidden.T[None].float(),3).flatten(1)
        code = message(pooled)
        if not transcript:
            return code,""
        prompt = self.prompt.replace("<|audio_pad|>","<|audio_pad|>"*len(hidden))
        ids = self.tokenizer(prompt,return_tensors="pt",add_special_tokens=False).input_ids.to(self.device)
        embeds = self.decoder.get_input_embeddings()(ids)
        embeds[ids==self.pad] = hidden
        end = self.tokenizer.convert_tokens_to_ids("<|im_end|>")
        tokens = self.decoder.generate(inputs_embeds=embeds,attention_mask=torch.ones_like(ids),
                   eos_token_id=end,pad_token_id=end,max_new_tokens=22,do_sample=False)
        text = self.tokenizer.decode(tokens[0],skip_special_tokens=False).split("<|im_end|>")[0]
        return code,text.split("<asr_text>")[-1].strip()


def florence_name(name):
    if name.startswith("convs."):
        return name.replace(".proj.",".conv.")
    return (name.replace(".conv1.fn.dw.",".conv1.").replace(".conv2.fn.dw.",".conv2.")
            .replace(".window_attn.norm.",".norm1.").replace(".channel_attn.norm.",".norm1.")
            .replace(".ffn.norm.",".norm2.").replace(".ffn.fn.net.",".ffn.")
            .replace("_attn.fn.","_attn."))


class Vision:
    def __init__(self, root, device, vl_repo):
        from transformers import (CLIPImageProcessor, Florence2VisionConfig, PaddleOCRVLForConditionalGeneration,
                                  Qwen3VLForConditionalGeneration, Qwen2VLImageProcessor)
        from transformers.models.florence2.modeling_florence2 import Florence2VisionBackbone
        from transformers.models.paddleocr_vl.image_processing_paddleocr_vl import PaddleOCRVLImageProcessor
        self.device, self.dtype = device,dtype_for(device)
        omni = root/"microsoft/OmniParser-v2.0"
        self.detector = torch.jit.load(str(omni/"icon_detect_v3/model.pt"),map_location=device).eval()
        cfg = json.loads((omni/"icon_caption/config.json").read_text())["vision_config"]
        cfg.update(embed_dim=cfg["dim_embed"],depths=cfg["depths"])
        self.caption = load_subset(Florence2VisionBackbone(Florence2VisionConfig(**cfg)),omni/"icon_caption",
                                   "vision_tower.",florence_name,device,self.dtype)
        self.caption_pre = CLIPImageProcessor.from_pretrained(root/"microsoft/Florence-2-base",local_files_only=True)
        self.vl = []
        for repo, cls, proc in ((vl_repo,Qwen3VLForConditionalGeneration,Qwen2VLImageProcessor),
                                ("PaddlePaddle/PaddleOCR-VL",PaddleOCRVLForConditionalGeneration,PaddleOCRVLImageProcessor)):
            folder = root/repo
            net,info = cls.from_pretrained(folder,dtype=self.dtype,local_files_only=True,use_safetensors=True,
                                           output_loading_info=True)
            if any(info.values()):
                raise ValueError(f"Incomplete pretrained weights: {repo} {info}")
            # Keep only the native visual encoder and multimodal projector resident for icon matching.
            net.model.language_model = None
            self.vl.append((net.model.eval().requires_grad_(False).to(device),proc.from_pretrained(folder,local_files_only=True)))

    @torch.inference_mode()
    def detect(self, image):
        from ..model import pixels
        heads = []
        for i,head in enumerate(self.detector(pixels(image).to(self.device))):
            h,w = head.shape[-2:]
            if i % 2 == 0:
                head = head.sigmoid()
            parts = torch.cat([head[...,y*h//2:(y+1)*h//2,x*w//2:(x+1)*w//2] for y in range(2) for x in range(2)])
            heads.append(F.adaptive_avg_pool2d(parts,(3,3)).flatten(1))
        return message(torch.cat(heads,1))

    @torch.inference_mode()
    def describe(self,chips):
        pixel = self.caption_pre(chips,return_tensors="pt").pixel_values.to(self.device,self.dtype)
        states = self.caption(pixel).last_hidden_state
        return message(states.mean(dim=(-2,-1)) if states.ndim==4 else states.mean(1))

    @torch.inference_mode()
    def multimodal(self,chips,index):
        net,processor = self.vl[index]
        batch = processor(chips,return_tensors="pt")
        grid = batch.image_grid_thw.to(self.device)
        outputs = net.get_image_features(batch.pixel_values.to(self.device,self.dtype),grid,return_dict=True).pooler_output
        if isinstance(outputs,torch.Tensor):
            outputs = outputs.reshape(len(chips),-1,outputs.shape[-1]).unbind()
        return message(torch.stack([item.reshape(-1,item.shape[-1]).mean(0) for item in outputs]))
