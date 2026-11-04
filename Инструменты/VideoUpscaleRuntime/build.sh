#!/usr/bin/env bash
set -euo pipefail
test "${MSYSTEM:-}" = UCRT64
root=$(realpath "${1:?workspace root}")
capture="$root/Runtime/Capture/Minimal-8.1"
build="$root/Runtime/VideoUpscale/Build-8.1"
prefix="$build/prefix"
mkdir -p "$build/work" "$build/logs" "$prefix"
export PKG_CONFIG_PATH="$capture/prefix/lib/pkgconfig"
export PKG_CONFIG_LIBDIR="$PKG_CONFIG_PATH"
cd "$build/work"
"$capture/work/ffmpeg/configure" --prefix="$prefix" --pkg-config-flags=--static \
 --disable-autodetect --disable-everything --disable-network --enable-shared \
 --disable-static --disable-debug --disable-doc --disable-ffplay --enable-zlib \
 --enable-w32threads --disable-pthreads --enable-mediafoundation --enable-d3d11va \
 --enable-libvpx --enable-libopus --enable-libopenh264 --enable-ffnvcodec --enable-nvenc \
 --disable-cuvid --disable-cuda-llvm \
 --enable-encoder=ffv1,h264_mf,h264_nvenc,hevc_nvenc,libopenh264,aac,libopus,rawvideo,pcm_s16le,png \
 --enable-decoder=ffv1,wrapped_avframe,rawvideo,h264,hevc,vp8,vp9,av1,mpeg4,mpeg2video,aac,ac3,eac3,dca,opus,mp3,flac,vorbis,pcm_u8,pcm_s16le,pcm_s24le,pcm_s32le,pcm_f32le,png,mjpeg,bmp,gif,webp \
 --enable-parser=h264,hevc,vp8,vp9,av1,mpeg4video,mpegaudio,aac,ac3,dca,opus,flac,png,mjpeg \
 --enable-demuxer=concat,rawvideo,pcm_s16le,mov,matroska,avi,wav,mp3,flac,ogg,aac,image2,png_pipe,jpeg_pipe,bmp_pipe,gif,webp_pipe \
 --enable-muxer=mp4,matroska,rawvideo,pcm_s16le,wav,image2 \
 --enable-protocol=file,pipe,fd --enable-indev=lavfi \
 --enable-filter=anullsrc,sine,aresample,aformat,apad,atrim,anull,amix,volume,asetpts,format,null,scale,setsar,setpts,trim,fps,pad,color,overlay,crop,select,showinfo,tpad,testsrc2,blend,unsharp,hqdn3d \
 --extra-cflags="-I$capture/prefix/include" \
 --extra-ldflags="-L$capture/prefix/lib -static-libgcc -static-libstdc++ -static" \
 --extra-version=lopata-video-upscale-2 >"$build/logs/configure.log" 2>&1
make -j8 >"$build/logs/build.log" 2>&1
make install >>"$build/logs/build.log" 2>&1
