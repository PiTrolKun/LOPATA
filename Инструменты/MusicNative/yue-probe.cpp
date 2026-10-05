// LOPATA hardware/memory probe. GPL-3.0-or-later; linked GGML/yyjson retain MIT notices.
#define NOMINMAX
#include <windows.h>
#include "ggml.h"
#include "ggml-backend.h"
#include "gguf.h"
#include "yyjson.h"
#include <string>
#include <cstdio>
#include <stdexcept>
#include <cstring>
#include <cstdlib>

static void number(yyjson_mut_doc * d, yyjson_mut_val * o, const char * k, uint64_t v) {
    yyjson_mut_obj_add_uint(d, o, k, v);
}
static uint64_t weights(const char * path, yyjson_mut_doc * d, yyjson_mut_val * root, bool decoder) {
    ggml_context * ctx = nullptr;
    gguf_context * gf = gguf_init_from_file(path, {true, &ctx});
    if (!gf) throw std::runtime_error("Invalid GGUF metadata");
    uint64_t ar=0, nar=0, vae=0;
    for (int64_t i=0; i<gguf_get_n_tensors(gf); ++i) {
        std::string name = gguf_get_tensor_name(gf,i);
        auto t=ggml_get_tensor(ctx,name.c_str());
        uint64_t bytes=ggml_nbytes(t);
        // Biases and normalization vectors are expanded to F32 by upstream loaders.
        if (name.find("norm")!=std::string::npos || name.find(".bias")!=std::string::npos)
            bytes=ggml_nelements(t)*sizeof(float);
        if (decoder) vae+=bytes;
        else if (name=="model.norm.weight") { ar+=bytes; nar+=bytes; }
        else if (name.find("nar_")!=std::string::npos || name.find("vae2llm") == 0 ||
            name.find("llm2vae")==0 || name.find("time_embedder")==0) nar+=bytes;
        else ar+=bytes;
    }
    if (decoder) number(d,root,"VaeBytes",vae);
    else {
        number(d,root,"ArBytes",ar); number(d,root,"NarBytes",nar);
        auto key=gguf_find_key(gf,"yue2.config_json");
        if (key<0) throw std::runtime_error("Missing YuE2 config");
        auto config=yyjson_read(gguf_get_val_str(gf,key),strlen(gguf_get_val_str(gf,key)),0);
        if (!config) throw std::runtime_error("Invalid YuE2 config");
        auto o=yyjson_doc_get_root(config);
        uint64_t layers=yyjson_get_uint(yyjson_obj_get(o,"num_hidden_layers"));
        uint64_t heads=yyjson_get_uint(yyjson_obj_get(o,"num_key_value_heads"));
        uint64_t dim=yyjson_get_uint(yyjson_obj_get(o,"head_dim"));
        if (!layers || !heads || !dim) throw std::runtime_error("Unknown KV shape");
        number(d,root,"KvBytesPerToken",layers*heads*dim*2*sizeof(uint16_t));
        yyjson_doc_free(config);
    }
    gguf_free(gf); ggml_free(ctx); return ar+nar+vae;
}
int main(int argc, char ** argv) {
    try {
        auto d=yyjson_mut_doc_new(nullptr); auto root=yyjson_mut_obj(d); yyjson_mut_doc_set_root(d,root);
        if (argc==4 && std::string(argv[1])=="--weights") { weights(argv[2],d,root,false); weights(argv[3],d,root,true); }
        else if (argc==3 && std::string(argv[1])=="--devices") {
            std::string kind=argv[2]; std::string file="ggml-";
            file+=kind=="CUDA"?"cuda":kind=="Vulkan"?"vulkan":"cpu"; file+=".dll";
            auto reg=ggml_backend_load(file.c_str());
            if (!reg) throw std::runtime_error("Backend library or driver unavailable");
            auto devices=yyjson_mut_arr(d); yyjson_mut_obj_add_val(d,root,"Devices",devices);
            HMODULE cuda=kind=="CUDA"?LoadLibraryExW(L"nvcuda.dll",nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32):nullptr;
            using Driver=int(__stdcall*)(int*); using Cap=int(__stdcall*)(int*,int*,int);
            using ByPci=int(__stdcall*)(int*,const char*);
            auto driver=cuda?(Driver)GetProcAddress(cuda,"cuDriverGetVersion"):nullptr;
            auto cap=cuda?(Cap)GetProcAddress(cuda,"cuDeviceComputeCapability"):nullptr;
            auto byPci=cuda?(ByPci)GetProcAddress(cuda,"cuDeviceGetByPCIBusId"):nullptr;
            int version=0; if (driver) driver(&version);
            for (size_t i=0;i<ggml_backend_reg_dev_count(reg);++i) {
                auto dev=ggml_backend_reg_dev_get(reg,i); auto item=yyjson_mut_obj(d);
                size_t free=0,total=0; ggml_backend_dev_memory(dev,&free,&total);
                yyjson_mut_obj_add_strcpy(d,item,"Backend",kind.c_str());
                yyjson_mut_obj_add_strcpy(d,item,"Name",ggml_backend_dev_name(dev));
                yyjson_mut_obj_add_strcpy(d,item,"Description",ggml_backend_dev_description(dev));
                number(d,item,"FreeBytes",free); number(d,item,"TotalBytes",total);
                // GGML CUDA ordinals can differ from driver ordinals on multi-GPU systems.
                ggml_backend_dev_props props{}; ggml_backend_dev_get_props(dev,&props);
                int major=0,minor=0,handle=0;
                if (cap && byPci && props.device_id && byPci(&handle,props.device_id)==0)
                    cap(&major,&minor,handle);
                number(d,item,"Capability",major*10+minor); number(d,item,"Driver",version);
                yyjson_mut_arr_add_val(devices,item);
            }
            if (cuda) FreeLibrary(cuda);
        } else throw std::runtime_error("Expected --devices BACKEND or --weights MODEL VAE");
        char * json=yyjson_mut_write(d,0,nullptr); puts(json); free(json); yyjson_mut_doc_free(d); return 0;
    } catch (const std::exception & e) { fprintf(stderr,"[Probe] %s\n",e.what()); return 1; }
}
